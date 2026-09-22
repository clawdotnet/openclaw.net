using System.Net;
using System.Text;
using System.Text.Json;
using OpenClaw.Client;
using OpenClaw.Companion.Services;
using OpenClaw.Companion.ViewModels;
using OpenClaw.Core.Models;
using Xunit;

namespace OpenClaw.Tests;

public sealed class ConfigurationClientReviewTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(reply(request));
    }
    private static HttpResponseMessage Json(HttpStatusCode status, string text) => new(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "")]
    [InlineData(HttpStatusCode.BadRequest, "{\"success\":false,\"error\":\"Invalid input\"}")]
    [InlineData(HttpStatusCode.TooManyRequests, "Slow down")]
    public async Task NonConfigurationErrors_ProduceUsefulHttpExceptions(HttpStatusCode status, string body)
    {
        using var http = new HttpClient(new Handler(_ => Json(status, body)));
        using var client = new OpenClawHttpClient("http://localhost", "token", http);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.PreviewConfigurationAsync(new()));
        Assert.Equal(status, error.StatusCode);
        Assert.Contains(((int)status).ToString(), error.Message);
        if (body.Length > 0) Assert.Contains(body, error.Message);
    }

    [Fact]
    public async Task FollowupProposal_PreservesEdits_AndSettingsAndOneShotDoNotEnableChatMode()
    {
        var root = Path.Combine(Path.GetTempPath(), "config-editor-review-" + Guid.NewGuid().ToString("N"));
        try
        {
            var state = "{\"success\":true,\"revision\":\"r1\",\"message\":\"Loaded\",\"values\":{\"sessionTimeoutMinutes\":30,\"readOnlyMode\":false}}";
            using var http = new HttpClient(new Handler(request => Json(HttpStatusCode.OK,
                request.RequestUri!.AbsolutePath.EndsWith("preview")
                    ? "{\"success\":true,\"revision\":\"r1\",\"message\":\"Review\",\"changes\":{\"readOnlyMode\":true}}" : state)));
            var vm = new MainWindowViewModel(new SettingsStore(root), new GatewayWebSocketClient(), (_, _) => new OpenClawHttpClient("http://localhost", "token", http));
            await vm.OpenConfigurationCommand.ExecuteAsync(null);
            Assert.False(vm.IsConfigurationMode);
            vm.SelectedConfigurationKey = "sessionTimeoutMinutes";
            vm.AddConfigurationFieldCommand.Execute(null);
            vm.ConfigurationEdits.Single().Value = "45";
            vm.InputText = "/configure also enable read-only mode";
            await vm.SendCommand.ExecuteAsync(null);
            Assert.False(vm.IsConfigurationMode);
            Assert.Equal("45", vm.ConfigurationEdits.Single(e => e.Key == "sessionTimeoutMinutes").Value);
            Assert.Equal("true", vm.ConfigurationEdits.Single(e => e.Key == "readOnlyMode").Value);
            vm.IsConfigurationMode = true;
            await vm.ApplyConfigurationCommand.ExecuteAsync(null);
            Assert.False(vm.IsConfigurationMode);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void InstructionOnlyRequest_InitializesEmptyChanges()
    {
        var request = JsonSerializer.Deserialize("{\"instruction\":\"enable read-only mode\",\"revision\":\"r1\"}", ConfigurationJsonContext.Default.ConfigurationRequest);
        Assert.NotNull(request!.Changes);
        Assert.Empty(request.Changes);
    }
}
