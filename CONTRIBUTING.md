# Contributing to OpenClaw.NET

Thank you for your interest in contributing! This guide covers the contributor workflow. For the project shape, repository map, and how the runtime fits together, start with [docs/GETTING_STARTED.md](docs/GETTING_STARTED.md). If you are evaluating the project for the first time, read [docs/START_HERE.md](docs/START_HERE.md). For a first local run, follow [docs/QUICKSTART.md](docs/QUICKSTART.md).

For project governance, maintainer roles, sponsorship boundaries, branch protection, and architecture scope, see [docs/project/governance.md](docs/project/governance.md), [docs/project/maintainers.md](docs/project/maintainers.md), [docs/project/sponsors.md](docs/project/sponsors.md), [docs/project/branch-protection.md](docs/project/branch-protection.md), [docs/ARCHITECTURE_BOUNDARIES.md](docs/ARCHITECTURE_BOUNDARIES.md), [docs/CAPABILITY_MATRIX.md](docs/CAPABILITY_MATRIX.md), and [docs/architecture/optional-dependency-split.md](docs/architecture/optional-dependency-split.md).

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [PowerShell 7](https://learn.microsoft.com/powershell/scripting/install/installing-powershell), available as `pwsh` on `PATH`, for script execution tests and central package policy checks
- Git
- A C# editor (VS Code with C# Dev Kit, Visual Studio, or Rider)

Windows PowerShell 5.1 (`powershell.exe`) does not replace `pwsh`. With the .NET SDK installed, you can install PowerShell as a global tool:

```bash
dotnet tool install --global PowerShell
pwsh --version
```

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

## NuGet Dependencies

NuGet dependency versions are managed in the root [Directory.Packages.props](Directory.Packages.props). Add or update a `PackageVersion` there, and keep project `PackageReference` items versionless. Keep conditions, `PrivateAssets`, and `IncludeAssets` on the project reference; a central version declaration does not add a dependency to a project.

Do not introduce nested central files, `VersionOverride`, global package references, project-local central management settings, or conditional central versions. Each package must have one unconditional `PackageVersion Include` declaration with a fixed version; `PackageVersion Update` items and floating versions are not allowed. Transitive pinning is disabled. The `PackageVersion` property used by `dotnet pack -p:PackageVersion=...` controls our published package version and is separate from central dependency version items.

The MCP packages are aligned to `2.2.0`, and `Microsoft.Extensions.Http` and `Microsoft.Extensions.Logging.Abstractions` to `10.0.10` across projects. The Semantic Kernel adapter is pinned to its previously resolved `1.80.1` version. Packages with independent release schedules need not share the same version number. Central management does not change the existing NativeAOT/JIT or optional integration boundaries.

Run the policy self-tests and project evaluation checks before restoring:

```powershell
pwsh -File eng/verify-central-packages.ps1 -SelfTest
pwsh -File eng/verify-central-packages.ps1
pwsh -File eng/verify-central-packages.ps1 -Configuration Debug
```

CI also evaluates the optional integration configuration. The checks cover source XML and evaluated references, but do not replace restore, build, tests, or publish checks. If restore reports `NU1507` for multiple package sources, review the effective NuGet configuration and use approved package source mapping rather than suppressing the diagnostic.

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
- Optional protocol or provider work that follows the split guidance in [docs/architecture/optional-dependency-split.md](docs/architecture/optional-dependency-split.md).

## Pull Request Review

PRs need at least one approval before merging. Reviewers check for:

- **Correctness** — does it work as described?
- **Tests** — are there sufficient tests? Do they pass?
- **NativeAOT** — does it avoid reflection and dynamic code?
- **Security** — does it handle untrusted input safely?
- **Style** — does it follow project conventions?

Maintainers use the scoped review guidance in [docs/maintainers/review-checklist.md](docs/maintainers/review-checklist.md). If a contribution directly supports a company or customer use case, disclose that context in the PR so reviewers can evaluate scope, vendor neutrality, and whether the work belongs in core or an extension.

## Adding a New Tool or LLM Provider

Tools implement `ITool` in `src/OpenClaw.Agent/Tools/` when they are part of the default agent surface. Protocol-specific optional tools should live in an optional project, as Browser does in `src/OpenClaw.Protocols.Browser` and MQTT does in `src/OpenClaw.Protocols.Mqtt`, and be composed through the gateway or another explicit extension seam. Providers plug in through `Microsoft.Extensions.AI` and the gateway composition pipeline — add through the active provider registration path, not a `Program.cs` factory. Both must be wired through the current composition seams (see [docs/architecture-startup-refactor.md](docs/architecture-startup-refactor.md)) and covered by tests in `src/OpenClaw.Tests/`.

## Reporting Bugs and Feature Requests

- **Bugs:** [Bug Report template](../../issues/new?template=bug_report.md). Include `dotnet --version`, OS, reproduction steps, expected vs actual behavior, and relevant logs (with secrets redacted).
- **Features:** [Feature Request template](../../issues/new?template=feature_request.md). Describe the problem, your proposed solution, and alternatives considered.

## Code of Conduct and License

This project follows the [Contributor Covenant](CODE_OF_CONDUCT.md). By contributing, you agree your contributions will be licensed under the [MIT License](LICENSE).
