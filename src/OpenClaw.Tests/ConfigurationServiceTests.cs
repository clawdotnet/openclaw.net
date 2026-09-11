using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Gateway;
using OpenClaw.Gateway.Bootstrap;
using Xunit;

namespace OpenClaw.Tests;

public sealed class ConfigurationServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "openclaw-config-test", Guid.NewGuid().ToString("N"));
    private GatewayConfig Config() => new() { Memory = new() { StoragePath = _root }, Llm = new() { ApiKey = "test-secret" } };
    private AdminSettingsService Service(GatewayConfig config) => new(config, AdminSettingsService.CreateSnapshot(config), AdminSettingsService.GetSettingsPath(config), NullLogger<AdminSettingsService>.Instance);
    private static ConfigurationRequest Request(string revision, string changes)
        => new() { Revision = revision, Changes = JsonSerializer.Deserialize("{\"changes\":" + changes + "}", ConfigurationJsonContext.Default.ConfigurationRequest)!.Changes };

    [Fact]
    public void PreviewApply_PreservesSecrets_RejectsStaleAndUnknownChanges_TracksPendingRestart()
    {
        var config = Config();
        config.Channels.WhatsApp.CloudApiToken = "private-secret";
        var service = Service(config);
        var state = service.DescribeConfiguration();
        Assert.DoesNotContain("private-secret", JsonSerializer.Serialize(state, ConfigurationJsonContext.Default.ConfigurationState));
        Assert.DoesNotContain("whatsappCloudApiToken", state.Values.Keys);
        var request = Request(state.Revision, "{\"maxConcurrentSessions\":77}");
        Assert.True(service.ChangeConfiguration(request, false).Success);
        Assert.NotEqual(77, config.MaxConcurrentSessions);
        Assert.False(File.Exists(AdminSettingsService.GetSettingsPath(config)));
        var saved = service.ChangeConfiguration(request, true);
        Assert.True(saved.Success, string.Join(" ", saved.Errors));
        Assert.Equal(77, config.MaxConcurrentSessions);
        Assert.True(saved.RestartRequired);
        Assert.Equal("private-secret", config.Channels.WhatsApp.CloudApiToken);
        Assert.False(service.ChangeConfiguration(request, true).Success);
        Assert.False(service.ChangeConfiguration(Request(saved.Revision, "{\"whatsappCloudApiToken\":\"stolen\"}"), true).Success);
        Assert.False(service.ChangeConfiguration(Request(saved.Revision, "{\"madeUpSetting\":true}"), true).Success);
        Assert.False(service.ChangeConfiguration(Request(saved.Revision, "{\"maxConcurrentSessions\":\"77\"}"), true).Success);
        Assert.False(service.ChangeConfiguration(Request(saved.Revision, "{\"autonomyMode\":\"autonomous\"}"), true).Success);
        Assert.False(service.ChangeConfiguration(Request(saved.Revision, "{\"usageFooter\":\"anything\"}"), true).Success);
        var second = service.ChangeConfiguration(Request(saved.Revision, "{\"usageFooter\":\"tokens\"}"), true);
        Assert.True(second.Success);
        Assert.True(second.RestartRequired);
        Assert.Contains("general.maxConcurrentSessions", second.RestartRequiredFields);
    }

    [Fact]
    public void PersistedSettings_LoadAtStartup_AndBaseSettingsRemainAvailableForReset()
    {
        var config = Config();
        var service = Service(config);
        var result = service.ChangeConfiguration(Request(service.DescribeConfiguration().Revision, "{\"maxConcurrentSessions\":77,\"modelName\":\"test-model\"}"), true);
        Assert.True(result.Success, string.Join(" ", result.Errors));
        var source = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["OpenClaw:Memory:StoragePath"] = _root }).Build();
        var loaded = GatewayBootstrapExtensions.LoadGatewayConfig(source);
        Assert.Equal(77, loaded.MaxConcurrentSessions);
        Assert.Equal(Environment.GetEnvironmentVariable("MODEL_PROVIDER_MODEL") ?? "test-model", loaded.Llm.Model);
        Assert.NotEqual(77, GatewayBootstrapExtensions.LoadGatewayConfig(source, false).MaxConcurrentSessions);
        Assert.False(Service(loaded).ChangeConfiguration(Request(result.Revision, "{\"usageFooter\":\"off\"}"), true).Success);
    }

    [Fact]
    public void PersistenceFailure_DoesNotChangeRuntime_AndInvalidValuesDoNotPersist()
    {
        var config = Config();
        var service = Service(config);
        var original = config.MaxConcurrentSessions;
        var state = service.DescribeConfiguration();
        Assert.False(service.ChangeConfiguration(Request(state.Revision, "{\"maxConcurrentSessions\":-9}"), true).Success);
        Directory.CreateDirectory(AdminSettingsService.GetSettingsPath(config)); // Block the target file.
        var result = service.ChangeConfiguration(Request(state.Revision, "{\"maxConcurrentSessions\":77}"), true);
        Assert.False(result.Success);
        Assert.Equal(original, config.MaxConcurrentSessions);
        Assert.Equal(state.Revision, service.DescribeConfiguration().Revision);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public void PublicBind_RejectsChangesThatCannotPassStartupHardening()
    {
        var config = Config();
        config.BindAddress = "0.0.0.0";
        config.AuthToken = "test-admin-token";
        config.Security.AllowUnsafeToolingOnPublicBind = false;
        config.Canvas.Enabled = false;
        var service = Service(config);
        var result = service.ChangeConfiguration(Request(service.DescribeConfiguration().Revision, "{\"allowShell\":true}"), true);
        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("Refusing to start"));
        Assert.False(File.Exists(AdminSettingsService.GetSettingsPath(config)));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
