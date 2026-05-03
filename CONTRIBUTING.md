# Contributing to OpenClaw.NET

Thank you for your interest in contributing! This guide covers the contributor workflow. For the project shape, repository map, and how the runtime fits together, start with [docs/GETTING_STARTED.md](docs/GETTING_STARTED.md). If you are evaluating the project for the first time, read [docs/START_HERE.md](docs/START_HERE.md). For a first local run, follow [docs/QUICKSTART.md](docs/QUICKSTART.md).

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Git
- A C# editor (VS Code with C# Dev Kit, Visual Studio, or Rider)

## Build & Test

```bash
git clone https://github.com/clawdotnet/openclaw.net
cd openclaw.net

dotnet restore OpenClaw.Net.slnx
dotnet build OpenClaw.Net.slnx --configuration Release --no-restore

# All tests
dotnet test OpenClaw.Net.slnx --configuration Release --no-build

# First deterministic runtime smoke
dotnet run --project samples/OpenClaw.HelloAgent -c Release --no-build
```

## Code Style

- **C# 14** — file-scoped namespaces, primary constructors, collection expressions
- **NativeAOT compatibility** — no `System.Reflection.Emit`, no dynamic loading; use source-generated JSON serialization (`CoreJsonContext`)
- **Naming** — `PascalCase` for public members, `_camelCase` for private fields, `camelCase` for locals/parameters
- **Formatting** — 4-space indentation, Allman braces, `var` when the type is obvious
- **No warnings** — code must compile with zero warnings

## Making Changes

1. **Pick an issue.** Look for issues labeled `good first issue` or `help wanted`. Comment to let others know you're working on it.
2. **Create a branch.** Use one of: `feature/`, `fix/`, `docs/`, `refactor/` — e.g. `feature/your-feature-name`.
3. **Write tests.** All changes must include tests. We use xUnit with NSubstitute for mocking. Test naming: `MethodName_WhenCondition_ShouldExpectedBehavior`.
4. **Verify before submitting:**

   ```bash
   dotnet build OpenClaw.Net.slnx --configuration Release --no-restore
   dotnet test OpenClaw.Net.slnx --configuration Release --no-build
   dotnet run --project samples/OpenClaw.HelloAgent -c Release --no-build
   ```

5. **Open a PR.** Fill out the template, reference related issues (`Fixes #123`), and keep PRs focused — one feature or fix per PR. Rebase on `main` if your branch is behind.

## Good First Contribution Areas

- Documentation gaps where a setup or diagnostic step is unclear.
- Focused regression tests for existing runtime, gateway, CLI, setup, or plugin behavior.
- Small CLI/help-text improvements that make failures more actionable.
- Compatibility catalog additions that exercise a real upstream package or intentionally unsupported case.
- Sample improvements that demonstrate existing behavior without adding new runtime architecture.

## Pull Request Review

PRs need at least one approval before merging. Reviewers check for:

- **Correctness** — does it work as described?
- **Tests** — are there sufficient tests? Do they pass?
- **NativeAOT** — does it avoid reflection and dynamic code?
- **Security** — does it handle untrusted input safely?
- **Style** — does it follow project conventions?

## Adding a New Tool or LLM Provider

Tools implement `ITool` in `src/OpenClaw.Agent/Tools/` and register their JSON types in `CoreJsonContext`. Providers plug in through `Microsoft.Extensions.AI` and the gateway composition pipeline — add through the active provider registration path, not a `Program.cs` factory. Both must be wired through the current composition seams (see [docs/architecture-startup-refactor.md](docs/architecture-startup-refactor.md)) and covered by tests in `src/OpenClaw.Tests/`.

## Reporting Bugs and Feature Requests

- **Bugs:** [Bug Report template](../../issues/new?template=bug_report.md). Include `dotnet --version`, OS, reproduction steps, expected vs actual behavior, and relevant logs (with secrets redacted).
- **Features:** [Feature Request template](../../issues/new?template=feature_request.md). Describe the problem, your proposed solution, and alternatives considered.

## Code of Conduct and License

This project follows the [Contributor Covenant](CODE_OF_CONDUCT.md). By contributing, you agree your contributions will be licensed under the [MIT License](LICENSE).
