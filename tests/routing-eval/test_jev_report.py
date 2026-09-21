import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location("jev_report", Path(__file__).resolve().parents[2] / "scripts/evaluate-jev-routing.py")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class JevReportTests(unittest.TestCase):
    def test_labels_compare_effective_policy_including_fallback(self):
        rows = [
            {"decision_id": "a", "baseline_tier": "T2", "applied_tier": "T2", "proposed_tier": "T0", "latency_ms": 100, "input_tokens": 2000, "estimated_cost_usd": .000084},
            {"decision_id": "b", "baseline_tier": "T3", "applied_tier": "T3", "reason": "timeout", "latency_ms": 1500},
        ]
        labels = [{"decision_id": "a", "expected_tier": "T0"}, {"decision_id": "b", "expected_tier": "T3", "high_risk": True}]
        report = module.summarize(rows, labels)
        self.assertEqual(1, report["quality"]["jev_with_fallback"]["accuracy"])
        self.assertEqual(.5, report["quality"]["baseline"]["accuracy"])
        self.assertEqual(.5, report["quality"]["always_t2"]["under_routing_rate"])
        self.assertEqual(1, report["quality"]["jev_with_fallback"]["high_risk_capability_retention"])
        self.assertEqual(.000084, report["estimated_reported_decision_cost_usd"])
        self.assertEqual(1500, report["added_latency_ms"]["p95"])

    def test_calibration_is_split_by_checkpoint_and_counts_abstention(self):
        rows = [{"decision_id": str(i), "baseline_tier": "T2", "applied_tier": "T2", "latency_ms": 1,
                 "provider": "laya", "model": "laya@revision", "rubric_version": "v1",
                 "metadata": {"checkpoint": checkpoint, "calibration_id": "raw"},
                 "probabilities": {"T0": .1, "T1": .1, "T2": .1, "T3": .1, "abstain": .6}}
                for i, checkpoint in enumerate(["english", "multilingual"])]
        labels = [{"decision_id": str(i), "expected_tier": "T0"} for i in range(2)]
        report = module.summarize(rows, labels)
        self.assertEqual(2, len(report["calibration_quality"]))
        self.assertEqual(0, report["calibration_quality"][0]["accuracy"])
        self.assertIn("decision_with_fallback", report["quality"])
        self.assertNotIn("jev_with_fallback", report["quality"])

    def test_unlabeled_data_does_not_claim_accuracy(self):
        report = module.summarize([{"decision_id": "a", "baseline_tier": "T2", "applied_tier": "T2", "latency_ms": 0}])
        self.assertIsNone(report["quality"])
        self.assertIsNone(report["proposal_disagreement_with_baseline"])

    def test_rejects_empty_duplicate_or_unmatched_data(self):
        row = {"decision_id": "a", "baseline_tier": "T2", "applied_tier": "T2", "latency_ms": 1}
        with self.assertRaises(ValueError):
            module.summarize([])
        with self.assertRaises(ValueError):
            module.summarize([row, row])
        with self.assertRaises(ValueError):
            module.summarize([row], [{"decision_id": "unknown", "expected_tier": "T0"}])


if __name__ == "__main__":
    unittest.main()
