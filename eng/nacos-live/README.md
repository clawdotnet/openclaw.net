# Nacos live acceptance

This harness verifies the production optional adapters against authenticated
Nacos **3.2.4**, Router **0.2.2**, and a correctly registered `weather-mcp`
server exposing `get_weather(city)`. It creates a temporary standalone Java
deployment with generated credentials and available ports. It does not change
an existing service, gateway configuration, or database. Child processes and
the temporary deployment are removed on exit; the output directory retains
reports and logs. Check third-party logs before publishing them.

The weather tool returns explicitly labelled fixture observations by default.
`--live-weather` instead queries Open-Meteo for Oslo. Fixture mode proves live
registry/Router/event transport and binding behavior, not external weather
availability or broad discovery quality. Neither mode calls an LLM.

## Run locally

Requires .NET 10 with NativeAOT prerequisites, Java 17+, Python 3.12, and network
access to the pinned packages, official Nacos release, and Router embedding
model. Run from the repository root. Use a fresh output directory for each run.

```sh
python3.12 -m venv /tmp/openclaw-nacos-python
/tmp/openclaw-nacos-python/bin/pip install -r eng/nacos-live/requirements.txt
/tmp/openclaw-nacos-python/bin/python -c 'from chromadb.utils.embedding_functions import DefaultEmbeddingFunction; DefaultEmbeddingFunction()(["weather city"])'
curl --fail --location --retry 3 \
  https://github.com/alibaba/nacos/releases/download/3.2.4/nacos-server-3.2.4.tar.gz \
  -o /tmp/nacos-server-3.2.4.tar.gz

# Replace linux-x64 with the current host RID, e.g. osx-arm64.
dotnet publish eng/NacosLiveSmoke -c Release -r linux-x64 -p:PublishAot=true -o /tmp/openclaw-nacos-native
dotnet build eng/NacosLiveSmoke -c Release -p:PublishAot=false
dotnet build src/OpenClaw.Tests -c Release -p:OpenClawSkipDashboardBuild=true

/tmp/openclaw-nacos-python/bin/python eng/nacos-live/verify.py \
  --archive /tmp/nacos-server-3.2.4.tar.gz \
  --managed eng/NacosLiveSmoke/bin/Release/net10.0/NacosLiveSmoke.dll \
  --native /tmp/openclaw-nacos-native/NacosLiveSmoke \
  --test-dll src/OpenClaw.Tests/bin/Release/net10.0/OpenClaw.Tests.dll \
  --output /tmp/openclaw-nacos-evidence
```

The archive is checked against a pinned SHA-256 before extraction. The dedicated
`Nacos live acceptance` workflow runs these checks on Linux and also publishes
the full Gateway with both optional Nacos flags and NativeAOT enabled. The
ordinary adapter matrix still verifies that default Gateway builds contain no
Nacos SDK and Router-only builds contain no RedNb packages.

## Acceptance checks

- Exactly three Router tools; real search, add, and use-tool round trips.
- Static and dynamic MetaSkills complete with a weather payload in both the
  native agent runtime and Microsoft Agent Framework runtime, reuse bindings,
  avoid duplicate tool registration, and make zero model calls.
- Managed and NativeAOT smoke executables both keep JSON reflection disabled.
- The production subscription adapter reaches `active` with authentication.
- After warming both binding modes, a real configuration publish clears the
  cache and advances its generation within **2,000 ms**; both next invocations
  bind again successfully.

Success produces `NACOS_LIVE_ACCEPTANCE_PASS`, `managed.json`, `native.json`,
`acceptance.json`, and (with `--test-dll`) `live-runtimes.trx`. A skipped live test
or a successful AOT build alone does not satisfy these checks. Measurements
apply to the provisioned standalone deployment; cluster recovery, TLS/proxies,
and production service availability require deployment-specific validation.

Zhang (@geffzhang) supplied the original Nacos integration, protocol fixtures,
and five-run token measurements. Those measurements and the earlier +310 ms
prototype event result remain in [the Router guide](../../docs/nacos-mcp-router.md#historical-contributor-evidence).
This harness validates the subsequent provider-neutral implementation without
replacing or relabelling that contributor evidence.
