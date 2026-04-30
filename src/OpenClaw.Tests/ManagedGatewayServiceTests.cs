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

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private static string BinaryName(string name)
        => OperatingSystem.IsWindows() ? name + ".exe" : name;
}
