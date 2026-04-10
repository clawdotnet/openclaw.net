using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Gateway.Backends;
using Xunit;

namespace OpenClaw.Tests;

public sealed class CliBackendAdapterTests
{
    [Fact]
    public async Task CodexBackend_BuildsArgsEnvironment_AndFallbackEvents()
    {
        var backend = new CodexCliBackend(
            CreateConfig(cfg =>
            {
                cfg.Enabled = true;
                cfg.BackendId = "codex";
                cfg.Provider = "codex";
                cfg.Args = ["exec"];
                cfg.Environment = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["STATIC_ENV"] = "static-value"
                };
            }),
            new StubCredentialResolver("codex-secret"),
            new CodingBackendProcessHost(NullLogger<CodingBackendProcessHost>.Instance));

        var args = InvokeSessionArguments(backend, new BackendSessionRecord
        {
            SessionId = "session_codex",
            BackendId = backend.Definition.BackendId,
            Provider = backend.Definition.Provider,
            WorkspacePath = "/tmp/openclaw-codex",
            Model = "gpt-5-codex",
            ReadOnly = true
        }, structuredOutputEnabled: true);

        Assert.Equal(["exec", "--json", "-m", "gpt-5-codex", "-C", "/tmp/openclaw-codex", "-s", "read-only"], args);

        var environment = await InvokeEnvironmentAsync(
            backend,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["REQUEST_ENV"] = "request-value" });
        Assert.Equal("static-value", environment["STATIC_ENV"]);
        Assert.Equal("request-value", environment["REQUEST_ENV"]);
        Assert.Equal("codex-secret", environment["OPENAI_API_KEY"]);
        Assert.Equal("codex-secret", environment["CODEX_API_KEY"]);
        Assert.Equal("codex-secret", environment["OPENCLAW_BACKEND_CREDENTIAL"]);

        var shellEvent = Assert.Single(InvokeStdoutParse(backend, "session_codex", "$ git status", structuredOutputEnabled: false));
        Assert.IsType<BackendShellCommandProposedEvent>(shellEvent);
        Assert.Equal("git status", ((BackendShellCommandProposedEvent)shellEvent).Command);
        Assert.Equal("$ git status", shellEvent.RawLine);

        var toolEvent = Assert.Single(InvokeStdoutParse(backend, "session_codex", "tool: shell", structuredOutputEnabled: false));
        Assert.IsType<BackendToolCallRequestedEvent>(toolEvent);
        Assert.Equal("shell", ((BackendToolCallRequestedEvent)toolEvent).ToolName);
    }

    [Fact]
    public async Task GeminiBackend_BuildsArgsEnvironment_AndFallbackEvents()
    {
        var backend = new GeminiCliBackend(
            CreateConfig(cfg =>
            {
                cfg.Enabled = true;
                cfg.BackendId = "gemini-cli";
                cfg.Provider = "gemini-cli";
                cfg.Args = ["--interactive"];
            }),
            new StubCredentialResolver("gemini-secret"),
            new CodingBackendProcessHost(NullLogger<CodingBackendProcessHost>.Instance));

        var args = InvokeSessionArguments(backend, new BackendSessionRecord
        {
            SessionId = "session_gemini",
            BackendId = backend.Definition.BackendId,
            Provider = backend.Definition.Provider,
            Model = "gemini-2.5-pro",
            ReadOnly = true
        }, structuredOutputEnabled: false);

        Assert.Equal(["--interactive", "--model", "gemini-2.5-pro", "--sandbox"], args);

        var environment = await InvokeEnvironmentAsync(backend, null);
        Assert.Equal("gemini-secret", environment["GEMINI_API_KEY"]);
        Assert.Equal("gemini-secret", environment["GOOGLE_API_KEY"]);
        Assert.Equal("gemini-secret", environment["OPENCLAW_BACKEND_CREDENTIAL"]);

        var fileEvent = Assert.Single(InvokeStdoutParse(backend, "session_gemini", "write src/foo.cs", structuredOutputEnabled: false));
        Assert.IsType<BackendFileWriteEvent>(fileEvent);
        Assert.Equal("src/foo.cs", ((BackendFileWriteEvent)fileEvent).Path);

        var patchEvent = Assert.Single(InvokeStdoutParse(backend, "session_gemini", "patch applied: src/foo.cs", structuredOutputEnabled: false));
        Assert.IsType<BackendPatchAppliedEvent>(patchEvent);
    }

    [Fact]
    public async Task GitHubCopilotBackend_BuildsArgsEnvironment_AndFallbackEvents()
    {
        var backend = new GitHubCopilotCliBackend(
            CreateConfig(cfg =>
            {
                cfg.Enabled = true;
                cfg.BackendId = "copilot-cli";
                cfg.Provider = "github-copilot-cli";
                cfg.Args = ["chat"];
                cfg.WriteEnabled = true;
            }),
            new StubCredentialResolver("copilot-secret"),
            new CodingBackendProcessHost(NullLogger<CodingBackendProcessHost>.Instance));

        var args = InvokeSessionArguments(backend, new BackendSessionRecord
        {
            SessionId = "session_copilot",
            BackendId = backend.Definition.BackendId,
            Provider = backend.Definition.Provider,
            Model = "gpt-4.1",
            ReadOnly = false
        }, structuredOutputEnabled: false);

        Assert.Equal(["chat", "--model", "gpt-4.1", "--yolo"], args);

        var environment = await InvokeEnvironmentAsync(backend, null);
        Assert.Equal("copilot-secret", environment["GITHUB_TOKEN"]);
        Assert.Equal("copilot-secret", environment["COPILOT_TOKEN"]);
        Assert.Equal("copilot-secret", environment["OPENCLAW_BACKEND_CREDENTIAL"]);

        var assistantEvent = Assert.Single(InvokeStdoutParse(backend, "session_copilot", "plain fallback line", structuredOutputEnabled: false));
        Assert.IsType<BackendStdoutOutputEvent>(assistantEvent);
        Assert.Equal("plain fallback line", ((BackendStdoutOutputEvent)assistantEvent).Text);
        Assert.Equal("plain fallback line", assistantEvent.RawLine);

        var executedEvent = Assert.Single(InvokeStdoutParse(backend, "session_copilot", "executed: npm test", structuredOutputEnabled: false));
        Assert.IsType<BackendShellCommandExecutedEvent>(executedEvent);
    }

    private static GatewayConfig CreateConfig(Action<CodingCliBackendConfig> configureBackend)
    {
        var backendConfig = new CodingCliBackendConfig();
        configureBackend(backendConfig);

        return new GatewayConfig
        {
            CodingBackends = new CodingBackendsConfig
            {
                Codex = backendConfig,
                GeminiCli = backendConfig,
                GitHubCopilotCli = backendConfig
            }
        };
    }

    private static string[] InvokeSessionArguments(object backend, BackendSessionRecord session, bool structuredOutputEnabled)
    {
        var method = backend.GetType().BaseType!.GetMethod("BuildSessionArguments", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var result = method!.Invoke(backend, [session, structuredOutputEnabled]);
        Assert.NotNull(result);
        return ((IEnumerable<string>)result!).ToArray();
    }

    private static IReadOnlyList<BackendEvent> InvokeStdoutParse(object backend, string sessionId, string line, bool structuredOutputEnabled)
    {
        var method = backend.GetType().BaseType!.GetMethod("ParseStdout", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var result = method!.Invoke(backend, [sessionId, line, structuredOutputEnabled]);
        Assert.NotNull(result);
        return ((IEnumerable<BackendEvent>)result!).ToArray();
    }

    private static async Task<Dictionary<string, string>> InvokeEnvironmentAsync(object backend, IReadOnlyDictionary<string, string>? requestEnvironment)
    {
        var method = backend.GetType().BaseType!.GetMethod("BuildEnvironmentAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var task = (Task<Dictionary<string, string>>)method!.Invoke(backend, [null, requestEnvironment, CancellationToken.None])!;
        return await task;
    }

    private sealed class StubCredentialResolver(string secret) : IBackendCredentialResolver
    {
        public ValueTask<ResolvedBackendCredential?> ResolveAsync(string provider, BackendCredentialSourceConfig? source, CancellationToken ct)
            => ValueTask.FromResult<ResolvedBackendCredential?>(new ResolvedBackendCredential
            {
                Provider = provider,
                SourceKind = ConnectedAccountSecretKind.ProtectedBlob,
                Secret = secret
            });

        public ValueTask<ResolvedBackendCredential?> ResolveAsync(string provider, ConnectedAccountSecretRef? source, CancellationToken ct)
            => ValueTask.FromResult<ResolvedBackendCredential?>(new ResolvedBackendCredential
            {
                Provider = provider,
                SourceKind = ConnectedAccountSecretKind.ProtectedBlob,
                Secret = secret
            });
    }
}
