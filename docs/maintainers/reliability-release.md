# Reliability release verification

This release repairs operator session invalidation, goal completion/resume/blocking, dashboard token login, and startup recovery pagination. It also persists goal state and routes the native runtime through shared execution components.

Required checks:

- Standard and OpenSandbox-enabled Release tests.
- Full solution build, including Dashboard, and the HelloAgent source smoke.
- Packaged NativeAOT CLI/gateway smoke on the release platform matrix.
- One desktop bundle setup/start/reconnect/shutdown smoke using isolated state.
- Match the release tag and all artifact checksums to the verified commit.

`ReliabilityRegressionTests` covers the reported regressions. `RuntimeScenarioRunner` accepts a factory for an actual `IAgentRuntime` with deterministic provider responses and isolated tools, emits tool/approval/error evidence, and applies the existing scenario oracles. The factory controls the execution environment; use fake side-effecting tools in CI. Scripted scenarios remain useful for oracle validation, but do not establish production runtime behavior.

Compatibility notes:

- Account updates require a new browser login. Existing browser sessions were already process-local.
- Goal records live under `Memory.StoragePath/goals`; old JSONL history is retained. Previously lost in-memory goals cannot be reconstructed automatically from history.
- Custom `IGoalService` implementations must implement turn boundaries and validated model transitions. Custom `IBackgroundSessionStore` implementations must supply stable recovery pagination.
- The four larger roadmap additions (action reconciliation, real-run capture, full-instance restore, and run explanation) are not claimed as shipped by this reliability release.
