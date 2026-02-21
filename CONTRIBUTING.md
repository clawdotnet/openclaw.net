# Contributing to OpenClaw.NET

Thank you for your interest in contributing! This guide covers everything you need to get started.

## Development Setup

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Git
- A C# editor (VS Code with C# Dev Kit, Visual Studio, or Rider)

### Build & Test

```bash
# Clone
git clone https://github.com/user/openclaw.net.git
cd openclaw.net

# Build
dotnet build

# Run all tests
dotnet test

# Run a specific test project
dotnet test src/OpenClaw.Tests
```

### Run the Gateway

```bash
export OPENCLAW_API_KEY="sk-..."
dotnet run --project src/OpenClaw.Gateway -c Release
```

## Project Structure

```
src/
  OpenClaw.Core/        # Shared models, abstractions, pipeline, sessions, memory
  OpenClaw.Agent/       # Agent runtime, tool implementations
  OpenClaw.Channels/    # WebSocket + Twilio SMS channel adapters
  OpenClaw.Gateway/     # ASP.NET host, endpoints, Program.cs
  OpenClaw.Tests/       # All unit tests
```

## Code Style

- **C# 14** — use file-scoped namespaces, primary constructors, collection expressions
- **NativeAOT compatibility** — no `System.Reflection.Emit`, no dynamic loading; use source-generated JSON serialization (`CoreJsonContext`)
- **Naming** — `PascalCase` for public members, `_camelCase` for private fields, `camelCase` for locals/parameters
- **Formatting** — 4-space indentation, Allman braces, `var` when the type is obvious
- **No warnings** — code must compile with zero warnings

## Making Changes

### 1. Pick an Issue

Look for issues labeled `good first issue` or `help wanted`. Comment on the issue to let others know you're working on it.

### 2. Create a Branch

```bash
git checkout -b feature/your-feature-name
```

Branch naming conventions:
- `feature/` — new functionality
- `fix/` — bug fixes
- `docs/` — documentation changes
- `refactor/` — code restructuring

### 3. Write Tests

All changes must include tests. We use xUnit with NSubstitute for mocking.

```csharp
[Fact]
public async Task MyFeature_WhenCondition_ShouldExpectedBehavior()
{
    // Arrange
    var sut = new MyClass();

    // Act
    var result = await sut.DoSomething();

    // Assert
    Assert.Equal(expected, result);
}
```

Test naming convention: `MethodName_WhenCondition_ShouldExpectedBehavior`

### 4. Verify

Before submitting, make sure:

```bash
# All tests pass
dotnet test

# No build warnings
dotnet build -warnaserror

# (Optional) NativeAOT publish succeeds
dotnet publish src/OpenClaw.Gateway -c Release
```

### 5. Submit a Pull Request

- Fill out the PR template completely
- Reference related issues (`Fixes #123`)
- Keep PRs focused — one feature or fix per PR
- Rebase on `main` if your branch is behind

## Pull Request Review

PRs need at least one approval before merging. Reviewers will check for:

- **Correctness** — does it work as described?
- **Tests** — are there sufficient tests? Do they pass?
- **NativeAOT** — does it avoid reflection/dynamic code?
- **Security** — does it handle untrusted input safely?
- **Style** — does it follow the project conventions?

## Adding a New Tool

1. Create a class implementing `ITool` in `src/OpenClaw.Agent/Tools/`
2. Register the tool's JSON types in `CoreJsonContext`
3. Wire it up in `Program.cs`
4. Add tests in `src/OpenClaw.Tests/`
5. Document it in `README.md`

## Adding a New LLM Provider

Providers are handled through `Microsoft.Extensions.AI`. If your provider has an `IChatClient` implementation, it can be added to the provider factory in `Program.cs`.

## Reporting Bugs

Use the [Bug Report](../../issues/new?template=bug_report.md) issue template. Include:

- .NET version (`dotnet --version`)
- OS and architecture
- Steps to reproduce
- Expected vs actual behavior
- Relevant logs (with secrets redacted)

## Feature Requests

Use the [Feature Request](../../issues/new?template=feature_request.md) issue template. Describe:

- The problem you're trying to solve
- Your proposed solution
- Alternatives you've considered

## Code of Conduct

This project follows the [Contributor Covenant](CODE_OF_CONDUCT.md). By participating, you agree to uphold this code.

## License

By contributing, you agree that your contributions will be licensed under the [Apache License 2.0](LICENSE).
