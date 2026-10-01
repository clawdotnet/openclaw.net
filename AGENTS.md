# Agent Instructions

Instructions for coding agents working in the OpenClaw.NET repository.

## Project Priorities

- Keep Core lightweight and NativeAOT-friendly. Avoid reflection-heavy or trim-unsafe dependencies in core runtime paths.
- Keep optional integrations optional. Make plugin compatibility explicit and fail fast for unsupported surfaces; do not silently degrade.
- Do not broaden compatibility claims in documentation without tests.
- Keep gateway concerns separate from agent reasoning and runtime concerns where possible. Route new tool execution paths through the tool execution layer.
- Prioritize security and hardening for public-bind scenarios.
- Preserve existing configuration behavior unless a change is intentional.

## Build, Test, and Validate

Use the .NET 10 SDK. From the repository root, the standard verification flow is:

```powershell
dotnet restore OpenClaw.Net.slnx
dotnet build OpenClaw.Net.slnx --configuration Release --no-restore
dotnet test OpenClaw.Net.slnx --configuration Release --no-build
dotnet run --project samples/OpenClaw.HelloAgent -c Release --no-build
```

Add focused tests for behavior changes. Tests use xUnit and NSubstitute; use `MethodName_WhenCondition_ShouldExpectedBehavior` for test names. Keep the build free of warnings.

## Code and Dependency Conventions

- Use C# 14 conventions already present in the repository: file-scoped namespaces, primary constructors, and collection expressions where appropriate.
- Use 4-space indentation and Allman braces. Use `PascalCase` for public members, `_camelCase` for private fields, and `camelCase` for locals and parameters. Use `var` when the type is obvious.
- Preserve NativeAOT compatibility: avoid `System.Reflection.Emit` and dynamic loading in the AOT path; use source-generated JSON serialization such as `CoreJsonContext`.
- Manage NuGet versions in the root `Directory.Packages.props`; keep project `PackageReference` items versionless.
- Do not add nested central package files, `VersionOverride`, global package references, project-local central management settings, conditional central versions, or floating versions. Keep one unconditional fixed `PackageVersion Include` per package; transitive pinning is disabled.
- When changing central package policy, run `pwsh -File eng/verify-central-packages.ps1 -SelfTest`, `pwsh -File eng/verify-central-packages.ps1`, and `pwsh -File eng/verify-central-packages.ps1 -Configuration Debug`.

## Change Expectations

- Prefer small, composable changes over broad rewrites.
- Update tests when behavior changes and update documentation when user-visible behavior or project guidance changes.
- Do not broaden compatibility claims in documentation without tests.
- Call out NativeAOT and JIT implications for changes that affect runtime compatibility.
- Follow existing project patterns; do not copy conventions from other repositories unless they apply here.

## Repository Guidance

- [Contributor guide](CONTRIBUTING.md): prerequisites, build and test commands, coding style, and dependency policy.
- [Getting started](docs/GETTING_STARTED.md): repository map and runtime overview.
- [Architecture boundaries](docs/ARCHITECTURE_BOUNDARIES.md): core, gateway, and optional integration responsibilities.
- [Capability matrix](docs/CAPABILITY_MATRIX.md): supported capability and compatibility lanes.
- [Security](SECURITY.md): security posture and public-bind requirements.
- [Copilot instructions](.github/copilot-instructions.md): additional repository-wide agent guidance.
