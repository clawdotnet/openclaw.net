using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using OpenClaw.Client;
using OpenClaw.Core.Models;
using OpenClaw.Gateway;
using Xunit;

namespace OpenClaw.Tests;

public sealed partial class GatewayAdminEndpointTests
{
    [Fact]
    public async Task Configuration_RequiresAdminAndCsrf_AndAppliesThroughClient()
    {
        await using var harness = await CreateHarnessAsync(true, config =>
        {
            config.Canvas.Enabled = false;
            config.Tooling.AllowShell = false;
            config.Tooling.AllowedReadRoots = [config.Memory.StoragePath];
            config.Tooling.AllowedWriteRoots = [config.Memory.StoragePath];
            config.Plugins.Mcp.Enabled = false;
            config.Plugins.DynamicNative.Enabled = false;
        });
        Assert.Equal(HttpStatusCode.Unauthorized, (await harness.Client.GetAsync("/admin/configuration")).StatusCode);
        var viewer = CreateOperatorToken(harness, OperatorRoleNames.Viewer, "config-viewer");
        using var denied = new HttpRequestMessage(HttpMethod.Get, "/admin/configuration");
        denied.Headers.Authorization = new AuthenticationHeaderValue("Bearer", viewer);
        Assert.Equal(HttpStatusCode.Forbidden, (await harness.Client.SendAsync(denied)).StatusCode);
        var (cookie, csrf) = await LoginAsync(harness.Client, harness.AuthToken);
        using var noCsrf = new HttpRequestMessage(HttpMethod.Post, "/admin/configuration/apply") { Content = JsonContent("{}") };
        noCsrf.Headers.Add("Cookie", cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, (await harness.Client.SendAsync(noCsrf)).StatusCode);
        using var client = new OpenClawHttpClient(harness.Client.BaseAddress!.ToString(), harness.AuthToken, harness.Client);
        var current = await client.GetConfigurationAsync();
        var request = JsonSerializer.Deserialize("{\"changes\":{\"usageFooter\":\"tokens\"}}", ConfigurationJsonContext.Default.ConfigurationRequest)!;
        request.Revision = current.Revision;
        Assert.True((await client.PreviewConfigurationAsync(request)).Success);
        Assert.Equal("off", (await client.GetConfigurationAsync()).Values["usageFooter"].GetString());
        Assert.True((await client.ApplyConfigurationAsync(request)).Success);
        Assert.Equal("tokens", (await client.GetConfigurationAsync()).Values["usageFooter"].GetString());
        Assert.False((await client.ApplyConfigurationAsync(request)).Success);
    }
    [Fact]
    public async Task Configuration_CompanionManualEditor_SavesAndCancelsWithoutWebSocket()
    {
        await using var harness = await CreateHarnessAsync(true, config =>
        {
            config.Canvas.Enabled = false;
            config.Tooling.AllowShell = false;
            config.Tooling.AllowedReadRoots = [config.Memory.StoragePath];
            config.Tooling.AllowedWriteRoots = [config.Memory.StoragePath];
            config.Plugins.Mcp.Enabled = false;
            config.Plugins.DynamicNative.Enabled = false;
        });
        var vm = new OpenClaw.Companion.ViewModels.MainWindowViewModel(
            new OpenClaw.Companion.Services.SettingsStore(Path.Combine(harness.StoragePath, "desktop")),
            new OpenClaw.Companion.Services.GatewayWebSocketClient(),
            (_, _) => new OpenClawHttpClient(harness.Client.BaseAddress!.ToString(), harness.AuthToken, harness.Client));
        await vm.OpenConfigurationCommand.ExecuteAsync(null);
        Assert.Contains("sessionTimeoutMinutes", vm.ConfigurationKeys);
        vm.SelectedConfigurationKey = "sessionTimeoutMinutes";
        vm.AddConfigurationFieldCommand.Execute(null);
        vm.ConfigurationEdits.Single().Value = "45";
        await vm.ApplyConfigurationCommand.ExecuteAsync(null);
        Assert.Empty(vm.ConfigurationEdits);
        Assert.Contains("saved", vm.ConfigurationStatus);
        using var client = new OpenClawHttpClient(harness.Client.BaseAddress!.ToString(), harness.AuthToken, harness.Client);
        Assert.Equal(45, (await client.GetConfigurationAsync()).Values["sessionTimeoutMinutes"].GetInt32());
        vm.AddConfigurationFieldCommand.Execute(null);
        vm.ConfigurationEdits.Single().Value = "90";
        vm.CancelConfigurationCommand.Execute(null);
        Assert.Equal(45, (await client.GetConfigurationAsync()).Values["sessionTimeoutMinutes"].GetInt32());
        Assert.False(vm.IsConfigurationOpen);
    }

}
