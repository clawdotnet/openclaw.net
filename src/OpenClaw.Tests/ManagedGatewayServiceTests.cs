using System.Net;
using OpenClaw.Companion.Services;
using Xunit;

namespace OpenClaw.Tests;

public sealed class ManagedGatewayServiceTests : IDisposable
{
    private readonly string _tempDir;

    public ManagedGatewayServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "openclaw-managed-gateway-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void ResolveExecutables_FindsReleaseSiblingLayout()
    {
        var companionDir = Path.Combine(_tempDir, "companion");
        var gatewayDir = Path.Combine(_tempDir, "gateway");
        var cliDir = Path.Combine(_tempDir, "cli");
        Directory.CreateDirectory(companionDir);
        Directory.CreateDirectory(gatewayDir);
        Directory.CreateDirectory(cliDir);
        File.WriteAllText(Path.Combine(gatewayDir, BinaryName("OpenClaw.Gateway")), "");
        File.WriteAllText(Path.Combine(cliDir, BinaryName("openclaw")), "");

        var gateway = ManagedGatewayService.ResolveGatewayExecutable(companionDir);
        var cli = ManagedGatewayService.ResolveCliExecutable(companionDir);

        Assert.NotNull(gateway);
        Assert.NotNull(cli);
        Assert.Equal(gatewayDir, gateway.WorkingDirectory);
        Assert.Equal(cliDir, cli.WorkingDirectory);
    }

    [Fact]
    public void WebSocketUrl_UsesConfiguredPort()
    {
        var configPath = Path.Combine(_tempDir, "openclaw.settings.json");
        File.WriteAllText(configPath, """
        {
          "OpenClaw": {
            "BindAddress": "0.0.0.0",
            "Port": 19001
          }
        }
        """);

        using var service = new ManagedGatewayService(_tempDir, configPath: configPath);

        Assert.Equal("http://127.0.0.1:19001", service.BaseUrl);
        Assert.Equal("ws://127.0.0.1:19001/ws", service.WebSocketUrl.TrimEnd('/'));
    }

    [Fact]
    public void WebSocketUrl_BracketsIpv6BindAddress()
    {
        var configPath = Path.Combine(_tempDir, "openclaw.settings.json");
        File.WriteAllText(configPath, """
        {
          "OpenClaw": {
            "BindAddress": "::1",
            "Port": 19002
          }
        }
        """);

        using var service = new ManagedGatewayService(_tempDir, configPath: configPath);

        Assert.Equal("http://[::1]:19002", service.BaseUrl);
        Assert.Equal("ws://[::1]:19002/ws", service.WebSocketUrl.TrimEnd('/'));
    }

    [Theory]
    [InlineData("https://example.test", "wss://example.test/ws")]
    [InlineData("http://example.test/root", "ws://example.test/root/ws")]
    [InlineData("wss://example.test/ws", "wss://example.test/ws")]
    [InlineData("ws://example.test/root/ws", "ws://example.test/root/ws")]
    public void BuildWebSocketUrl_PreservesWsSchemeAndAvoidsDuplicatePath(string baseUrl, string expected)
    {
        Assert.Equal(expected, ManagedGatewayService.BuildWebSocketUrl(baseUrl).TrimEnd('/'));
    }

    [Fact]
    public async Task Dispose_DoesNotDisposeInjectedHttpClient()
    {
        using var httpClient = new HttpClient(new StaticHttpMessageHandler(HttpStatusCode.OK));
        using (new ManagedGatewayService(_tempDir, httpClient))
        {
        }

        using var response = await httpClient.GetAsync("http://example.test/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task RunSetupAsync_RequiresApiKeyForRemoteProviders()
    {
        var cliDir = Path.Combine(_tempDir, "cli");
        Directory.CreateDirectory(cliDir);
        File.WriteAllText(Path.Combine(cliDir, BinaryName("openclaw")), "");
        using var service = new ManagedGatewayService(_tempDir);

        var result = await service.RunSetupAsync(new ManagedGatewaySetupRequest(
            "openai",
            "gpt-4o",
            ApiKey: null,
            ModelPresetId: null,
            WorkspacePath: Path.Combine(_tempDir, "workspace"),
            ConfigPath: Path.Combine(_tempDir, "config.json")), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("API key", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunSetupAsync_PassesApiKeyAsChildEnvironmentReference()
    {
        if (OperatingSystem.IsWindows())
            return;

        var cliDir = Path.Combine(_tempDir, "cli");
        Directory.CreateDirectory(cliDir);
        var cliPath = Path.Combine(cliDir, BinaryName("openclaw"));
        File.WriteAllText(cliPath, """
        #!/usr/bin/env sh
        printf '%s\n' "$@" > "$OPENCLAW_ARG_CAPTURE_PATH"
        printf '%s' "$OPENCLAW_MODEL_PROVIDER_KEY" > "$OPENCLAW_ENV_CAPTURE_PATH"
        exit 0
        """);
        File.SetUnixFileMode(
            cliPath,
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.UserExecute);

        var argCapturePath = Path.Combine(_tempDir, "args.txt");
        var envCapturePath = Path.Combine(_tempDir, "env.txt");
        var previousProviderKey = Environment.GetEnvironmentVariable("OPENCLAW_MODEL_PROVIDER_KEY");
        var previousArgCapturePath = Environment.GetEnvironmentVariable("OPENCLAW_ARG_CAPTURE_PATH");
        var previousEnvCapturePath = Environment.GetEnvironmentVariable("OPENCLAW_ENV_CAPTURE_PATH");
        Environment.SetEnvironmentVariable("OPENCLAW_MODEL_PROVIDER_KEY", "parent-value");
        Environment.SetEnvironmentVariable("OPENCLAW_ARG_CAPTURE_PATH", argCapturePath);
        Environment.SetEnvironmentVariable("OPENCLAW_ENV_CAPTURE_PATH", envCapturePath);
        try
        {
            using var service = new ManagedGatewayService(_tempDir);

            var result = await service.RunSetupAsync(new ManagedGatewaySetupRequest(
                "openai",
                "gpt-4o",
                ApiKey: "super-secret",
                ModelPresetId: null,
                WorkspacePath: Path.Combine(_tempDir, "workspace"),
                ConfigPath: Path.Combine(_tempDir, "config.json")), CancellationToken.None);

            var args = File.ReadAllText(argCapturePath);
            var childProviderKey = File.ReadAllText(envCapturePath);

            Assert.True(result.IsSuccess, result.Message);
            Assert.Contains("env:OPENCLAW_MODEL_PROVIDER_KEY", args);
            Assert.DoesNotContain("super-secret", args);
            Assert.Equal("super-secret", childProviderKey);
            Assert.Equal("parent-value", Environment.GetEnvironmentVariable("OPENCLAW_MODEL_PROVIDER_KEY"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCLAW_MODEL_PROVIDER_KEY", previousProviderKey);
            Environment.SetEnvironmentVariable("OPENCLAW_ARG_CAPTURE_PATH", previousArgCapturePath);
            Environment.SetEnvironmentVariable("OPENCLAW_ENV_CAPTURE_PATH", previousEnvCapturePath);
        }
    }

    [Fact]
    public async Task StartAsync_InjectsStoredProviderApiKeyIntoGatewayProcess()
    {
        if (OperatingSystem.IsWindows())
            return;

        var gatewayDir = Path.Combine(_tempDir, "gateway");
        Directory.CreateDirectory(gatewayDir);
        var gatewayPath = Path.Combine(gatewayDir, BinaryName("OpenClaw.Gateway"));
        File.WriteAllText(gatewayPath, """
        #!/usr/bin/env sh
        printf '%s' "$OPENCLAW_MODEL_PROVIDER_KEY" > "$OPENCLAW_GATEWAY_ENV_CAPTURE_PATH"
        exit 0
        """);
        File.SetUnixFileMode(
            gatewayPath,
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.UserExecute);

        var configPath = Path.Combine(_tempDir, "openclaw.settings.json");
        File.WriteAllText(configPath, """
        {
          "OpenClaw": {
            "BindAddress": "127.0.0.1",
            "Port": 19003
          }
        }
        """);

        var envCapturePath = Path.Combine(_tempDir, "gateway-env.txt");
        var previousEnvCapturePath = Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_ENV_CAPTURE_PATH");
        Environment.SetEnvironmentVariable("OPENCLAW_GATEWAY_ENV_CAPTURE_PATH", envCapturePath);
        try
        {
            using var httpClient = new HttpClient(new StaticHttpMessageHandler(HttpStatusCode.ServiceUnavailable));
            using var service = new ManagedGatewayService(_tempDir, httpClient, configPath: configPath);
            service.SetProviderApiKey("super-secret");

            var result = await service.StartAsync(authToken: null, CancellationToken.None);

            Assert.False(result.IsSuccess);
            Assert.Equal("super-secret", File.ReadAllText(envCapturePath));
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCLAW_GATEWAY_ENV_CAPTURE_PATH", previousEnvCapturePath);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private static string BinaryName(string name)
        => OperatingSystem.IsWindows() ? name + ".exe" : name;

    private sealed class StaticHttpMessageHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(statusCode));
    }
}
