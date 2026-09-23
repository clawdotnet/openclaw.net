import copy
import hashlib
import json
from pathlib import Path
import sys
import tempfile
import threading
import types
import unittest
from unittest.mock import patch
from urllib.request import Request, urlopen
from urllib.error import HTTPError

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))
from tools.laya_service import calibration, protocol, compat, runtime, download
from tools.laya_service.__main__ import DecisionServer

MODEL = 'laya@' + protocol.DEFAULT_REVISION
QUESTIONS = {'tier': {'type': 'choice', 'instructions': 'Which task?', 'criteria': {'small': 'simple', 'large': 'complex'}}}


def request():
    return {'model': MODEL, 'state': 'hello', 'questions': copy.deepcopy(QUESTIONS), 'rubric_version': 'test-v1'}


def observation(identifier, label='small', probability=.99):
    return {'case_id': identifier, 'case_fingerprint': hashlib.sha256(identifier.encode()).hexdigest(), 'question_id': 'tier', 'checkpoint': 'english', 'model': MODEL,
            'schema_hash': protocol.schema_hash(QUESTIONS), 'sdk_version': protocol.SDK_VERSION,
            'source_calibration': 'raw', 'label': label,
            'answer': {'type': 'choice', 'choice': 'small', 'probabilities': {'small': probability, 'large': 1-probability}}}


class ProtocolTests(unittest.TestCase):
    def test_rubric_hash_matches_dotnet_golden_values(self):
        rubric = json.loads((Path(__file__).resolve().parents[2] /
                             'tools/laya_service/rubrics/openclaw-laya-tiers-v1.json').read_text())
        self.assertEqual('8c8de008c11734cf12b58c7914581ea2779c2e44356f3c5b1cf79df7fa953d9c',
                         protocol.schema_hash(rubric['questions']))
        rubric['questions']['tier']['instructions'] = "Judge l'utilisateur <x> & C++ café 中文 😀\u2028\u0001\n\t"
        self.assertEqual('2089e651a62138bf97aaf6c0ee38e4791540f7ec372064384cc7caf831b9da9c',
                         protocol.schema_hash(rubric['questions']))

    def test_rejects_duplicate_nonfinite_and_invalid_contracts(self):
        for value in ['{"x":1,"x":2}', '{"x":NaN}']:
            with self.assertRaises(ValueError): protocol.read_json(value)
        for mutate in [lambda r: r.update(model='laya@latest'),
                       lambda r: r.update(questions={}),
                       lambda r: r['questions']['tier'].update(criteria={str(i): '' for i in range(21)}),
                       lambda r: r.update(language='../model'),
                       lambda r: r['questions']['tier'].update(type='text')]:
            value = request(); mutate(value)
            with self.assertRaises(protocol.Rejected): protocol.validate_request(value, MODEL)
        self.assertEqual(QUESTIONS, protocol.validate_request(request(), MODEL))

    def test_schema_identity_preserves_choice_order(self):
        changed = copy.deepcopy(QUESTIONS)
        changed['tier']['criteria'] = dict(reversed(list(changed['tier']['criteria'].items())))
        self.assertNotEqual(protocol.schema_hash(QUESTIONS), protocol.schema_hash(changed))

    def test_manifest_rejects_corruption_and_paths_outside_bundle(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp); model = root/'model'; model.mkdir()
            hashes = {}
            for filename in protocol.MODEL_FILES:
                target = model/filename; target.parent.mkdir(exist_ok=True)
                target.write_text('test'); hashes[filename] = protocol.file_hash(target)
            manifest = root/'manifest.json'
            data = {'version': 1, 'revision': protocol.DEFAULT_REVISION,
                    'checkpoints': {'english': {'path': 'model', 'sha256': hashes}}}
            manifest.write_text(json.dumps(data))
            self.assertIn('english', runtime.load_manifest(manifest)['checkpoints'])
            (model/'model.safetensors').write_text('changed')
            with self.assertRaises(ValueError): runtime.load_manifest(manifest)
            data['checkpoints']['english']['path'] = '../outside'
            manifest.write_text(json.dumps(data))
            with self.assertRaises(ValueError): runtime.load_manifest(manifest)

    def test_download_selects_exact_files_and_pins_revision(self):
        calls = []
        def snapshot(repo, **kw):
            calls.append((repo, kw))
            for filename in kw['allow_patterns']:
                target = Path(kw['local_dir'])/filename; target.parent.mkdir(parents=True, exist_ok=True)
                target.write_text('{}' if filename.endswith('.json') else 'fake weight')
        hub = types.ModuleType('huggingface_hub'); hub.snapshot_download = snapshot
        with tempfile.TemporaryDirectory() as tmp, patch.dict(sys.modules, {'huggingface_hub': hub}):
            expected = {filename: hashlib.sha256(
                ('{}' if filename.endswith('.json') else 'fake weight').encode()).hexdigest()
                for filename in protocol.MODEL_FILES}
            with patch.object(download, 'DEFAULT_FILE_HASHES', {'english': expected}):
                manifest = download.prepare(tmp, protocol.DEFAULT_REVISION, ['english'])
            self.assertEqual(list(protocol.MODEL_FILES), calls[0][1]['allow_patterns'])
            self.assertEqual(protocol.DEFAULT_REVISION, calls[0][1]['revision'])
            self.assertIn('english', runtime.load_manifest(manifest)['checkpoints'])
            self.assertTrue((Path(tmp)/'LAYA-NOTICE.md').exists())
            with self.assertRaises(ValueError): download.prepare(tmp, 'latest', ['english'])
        with tempfile.TemporaryDirectory() as tmp, patch.dict(sys.modules, {'huggingface_hub': hub}), \
                patch.object(download, 'DEFAULT_FILE_HASHES', {'english': {**expected, 'model.safetensors': '0'*64}}):
            with self.assertRaisesRegex(ValueError, 'upstream digest'):
                download.prepare(tmp, protocol.DEFAULT_REVISION, ['english'])


class CompatibilityTests(unittest.TestCase):
    def test_armenian_minority_and_unknown_scripts_never_use_english(self):
        lang = types.ModuleType('laya.lang')
        lang.analyse = lambda state: {'is_english': True}
        common = types.ModuleType('laya.common')
        common.serialize_state = lambda state: state if isinstance(state, str) else str(state)
        with patch.dict(sys.modules, {'laya.lang': lang, 'laya.common': common}):
            for text in ['Հայերեն', 'English text with Հայերեն', 'हिन्दी', 'ქართული']:
                self.assertEqual('multilingual', compat.select_checkpoint(text))
                with self.assertRaises(protocol.Rejected): compat.select_checkpoint(text, 'en')
            self.assertEqual('english', compat.select_checkpoint('Hello!'))
            self.assertEqual('multilingual', compat.select_checkpoint('Bonjour', 'fr'))

    def test_token_budgets_fail_before_sdk_truncation(self):
        common = types.ModuleType('laya.common')
        common.render_options = lambda q: list(q['crit'].values())
        common.serialize_state = str
        class Tokenizer:
            mask_token = '[MASK]'
            def __call__(self, text, **kw): return {'input_ids': list(text)}
        agent = types.SimpleNamespace(tok=Tokenizer(), cfg={'max_len': 90, 'head_max_len': 50},
                                     _to_internal=lambda q: {'t': q['type'], 'ins': q['instructions'], 'crit': q['criteria']})
        with patch.dict(sys.modules, {'laya.common': common}):
            compat.ensure_complete(agent, 'hello', QUESTIONS)
            with self.assertRaisesRegex(protocol.Rejected, 'state_too_long'):
                compat.ensure_complete(agent, 'x'*100, QUESTIONS)
            long_question = copy.deepcopy(QUESTIONS); long_question['tier']['instructions'] = 'x'*60
            with self.assertRaisesRegex(protocol.Rejected, 'question_too_long'):
                compat.ensure_complete(agent, 'hello', long_question)
            long_option = copy.deepcopy(QUESTIONS); long_option['tier']['criteria']['small'] = 'x'*60
            with self.assertRaisesRegex(protocol.Rejected, 'option_too_long'):
                compat.ensure_complete(agent, 'hello', long_option)
            with self.assertRaisesRegex(protocol.Rejected, 'reserved_token'):
                compat.ensure_complete(agent, '[MASK]', QUESTIONS)


class CalibrationTests(unittest.TestCase):
    def test_fits_and_measures_disjoint_held_out_cases(self):
        train = [observation('train'+str(i), 'small' if i%2 else 'large') for i in range(40)]
        test = [observation('test'+str(i), 'small' if i%2 else 'large') for i in range(40)]
        artifact = calibration.fit(train, test)
        results = artifact['validation']['english']['choice:2']
        self.assertLess(results['calibrated']['nll'], results['raw']['nll'])
        self.assertLess(results['calibrated']['ece'], results['raw']['ece'])
        self.assertEqual(40, results['calibrated']['samples'])
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp)/'calibration.json'; path.write_text(json.dumps(artifact))
            fitted = calibration.Calibration(path, MODEL)
            fitted.check(protocol.schema_hash(QUESTIONS), 'english', QUESTIONS)
            with self.assertRaises(protocol.Rejected): fitted.check('a'*64, 'english', QUESTIONS)
            with self.assertRaises(protocol.Rejected): fitted.check(protocol.schema_hash(QUESTIONS), 'multilingual', QUESTIONS)
            self.assertEqual(protocol.file_hash(path), fitted.identifier)

    def test_rejects_leakage_mixed_versions_and_sparse_data(self):
        rows = [observation(str(i), 'small' if i%2 else 'large') for i in range(20)]
        with self.assertRaisesRegex(ValueError, 'overlap'): calibration.fit(rows, rows)
        with self.assertRaises(ValueError): calibration.fit(rows[:2], [observation('test')])
        other = [dict(r, case_id='test'+r['case_id'], schema_hash='a'*64) for r in rows]
        with self.assertRaises(ValueError): calibration.fit(rows, other)
        other = [dict(r, case_id='renamed'+r['case_id']) for r in rows]
        with self.assertRaisesRegex(ValueError, 'identical requests'): calibration.fit(rows, other)

    def test_metrics_include_abstention_errors_and_ties(self):
        rows = [observation('one'), observation('two', 'large')]
        result = calibration.metrics(rows)
        self.assertEqual(.5, result['accuracy'])
        self.assertEqual(2, result['samples'])
        self.assertTrue(all(x['coverage'] in (0, 1) for x in result['risk_coverage']))
        score = {'type': 'score', 'probabilities': {'0': .1, '1': .2, '2': .7}, 'score': 1.6}
        fixed = calibration.transform(score, 2)
        self.assertAlmostEqual(1, sum(fixed['probabilities'].values()))
        self.assertLess(fixed['score'], score['score'])
        self.assertAlmostEqual(.8, calibration.transform({'type': 'noul', 'noul': .8}, 1)['noul'])

    def test_rejects_boolean_probabilities(self):
        with self.assertRaisesRegex(protocol.Rejected, 'invalid_model_probabilities'):
            calibration.transform({'type': 'noul', 'noul': True}, 1)


class ServerTests(unittest.TestCase):
    def setUp(self):
        self.calls = 0
        self.failure = None
        owner = self
        class FakeRuntime:
            def health(self): return {'ready': True}
            def predict(self, value):
                owner.calls += 1
                protocol.validate_request(value, MODEL)
                if owner.failure:
                    raise owner.failure
                return {'ok': True}
        self.server = DecisionServer(0, FakeRuntime())
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True); self.thread.start()
        self.url = f'http://127.0.0.1:{self.server.server_port}'

    def tearDown(self):
        self.server.shutdown(); self.server.server_close(); self.thread.join()

    def post(self, value, **headers):
        return urlopen(Request(self.url+'/v1/decisions', data=json.dumps(value).encode(),
                               headers={'Content-Type': 'application/json', **headers}), timeout=2)

    def test_health_and_valid_request(self):
        with urlopen(self.url+'/health') as response: self.assertTrue(json.load(response)['ready'])
        with self.post(request()) as response: self.assertTrue(json.load(response)['ok'])
        self.assertEqual(1, self.calls)

    def test_browser_origins_bad_hosts_overload_and_large_body(self):
        for headers in [{'Origin': 'https://example.com'}, {'Host': 'attacker.invalid'}]:
            with self.assertRaises(HTTPError) as error: self.post(request(), **headers)
            self.assertEqual(403, error.exception.code); error.exception.close()
        with self.server.inference:
            with self.assertRaises(HTTPError) as error: self.post(request())
            self.assertEqual(503, error.exception.code); error.exception.close()
        with self.assertRaises(HTTPError) as error: self.post({'state': 'x'*70000})
        self.assertEqual(413, error.exception.code); error.exception.close()
        self.assertEqual(0, self.calls)

    def test_errors_do_not_echo_request_contents(self):
        value = request(); value['model'] = 'SECRET'
        with self.assertRaises(HTTPError) as error: self.post(value)
        self.assertEqual(422, error.exception.code)
        self.assertNotIn(b'SECRET', error.exception.read()); error.exception.close()

    def test_runtime_failures_are_service_errors_not_client_errors(self):
        self.failure = ValueError('invalid model output')
        with self.assertRaises(HTTPError) as error:
            self.post(request())
        self.assertEqual(503, error.exception.code)
        self.assertEqual({'error': 'inference_failed'}, json.load(error.exception))
        error.exception.close()


if __name__ == '__main__':
    unittest.main()
