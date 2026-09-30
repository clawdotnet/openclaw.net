using System.Net;
using System.Net.WebSockets;
using System.Text;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using OpenClaw.Companion.Models;
using OpenClaw.Companion.Services;
using OpenClaw.Client;
using OpenClaw.Companion.ViewModels;
using Xunit;

namespace OpenClaw.Tests;

public sealed class CompanionConnectionTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (var dir in _tempDirs.Where(Directory.Exists))
            Directory.Delete(dir, recursive: true);
    }

    [AvaloniaFact]
    public async Task ServerClose_WhenPolicyViolation_ShouldDisconnectAndExplainOperatorRole()
    {
        var (vm, client) = CreateConnectedViewModel();
        var ws = new TestWebSocket();
        ws.QueueClose(WebSocketCloseStatus.PolicyViolation, "This action requires the operator role.");

        await client.RunReceiveLoopForTest(ws, CancellationToken.None);
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.IsConnected);
        Assert.Equal("Disconnected", vm.Status);
        var message = Assert.Single(vm.Messages, m => m.Role == ChatRole.System);
        Assert.Contains("operator role", message.Text, StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    public async Task ServerClose_WhenNormalClosure_ShouldDisconnectWithoutRoleHint()
    {
        var (vm, client) = CreateConnectedViewModel();
        var ws = new TestWebSocket();
        ws.QueueClose(WebSocketCloseStatus.NormalClosure, "shutting down");

        await client.RunReceiveLoopForTest(ws, CancellationToken.None);
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.IsConnected);
        var message = Assert.Single(vm.Messages, m => m.Role == ChatRole.System);
        Assert.DoesNotContain("operator", message.Text, StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    public async Task ServerClose_WhenClientReconnectsBeforeUiDispatch_ShouldKeepConnected()
    {
        var (vm, client) = CreateConnectedViewModel();
        var closedSocket = new TestWebSocket();
        closedSocket.QueueClose(WebSocketCloseStatus.PolicyViolation, "This action requires the operator role.");

        await client.RunReceiveLoopForTest(closedSocket, CancellationToken.None);
        using var reconnectedSocket = new TestWebSocket();
        client.SetConnectedSocketForTest(reconnectedSocket);
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.IsConnected);
        Assert.Equal("Connected", vm.Status);
        Assert.DoesNotContain(vm.Messages, message => message.Role == ChatRole.System);
    }

    [AvaloniaFact]
    public async Task Connect_WhenGatewayReportsAgentExecutionDenied_ShouldExplainWithoutOpeningChat()
    {
        var vm = CreateViewModelWithAuthSession("""{"authMode":"account_token","role":"viewer","username":"reader","canExecuteAgent":false}""");
        vm.AuthToken = "viewer-token";

        await vm.ConnectCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.IsConnected);
        Assert.Equal("Disconnected", vm.Status);
        Assert.Contains(vm.Messages, m => m.Text.Contains("operator role", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(vm.Messages, m => m.Text.StartsWith("Connect failed", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task Connect_WhenGatewayAllowsOperatorAgentExecution_ShouldAttemptChat()
    {
        var vm = CreateViewModelWithAuthSession("""{"authMode":"account_token","role":"operator","username":"runner","canExecuteAgent":true}""");
        vm.AuthToken = "operator-token";

        await vm.ConnectCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(vm.Messages, m => m.Text.StartsWith("Connect failed", StringComparison.Ordinal));
        Assert.DoesNotContain(vm.Messages, m => m.Text.Contains("operator role", StringComparison.OrdinalIgnoreCase));
    }

    [AvaloniaFact]
    public async Task Connect_WhenGatewayDoesNotReportAgentExecution_ShouldAttemptChat()
    {
        // An older gateway reports only the role; it decides at connect time, so Companion must not guess.
        var vm = CreateViewModelWithAuthSession("""{"authMode":"account_token","role":"viewer","username":"reader"}""");
        vm.AuthToken = "viewer-token";

        await vm.ConnectCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(vm.Messages, m => m.Text.StartsWith("Connect failed", StringComparison.Ordinal));
        Assert.DoesNotContain(vm.Messages, m => m.Text.Contains("operator role", StringComparison.OrdinalIgnoreCase));
    }

    [AvaloniaFact]
    public async Task Connect_WhenNoTokenLoaded_ShouldStillAttemptChat()
    {
        // Without a token the viewer role is only a placeholder, not something the gateway reported.
        var vm = CreateViewModelWithAuthSession("""{"authMode":"account_token","role":"viewer"}""");

        await vm.ConnectCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(vm.Messages, m => m.Text.StartsWith("Connect failed", StringComparison.Ordinal));
        Assert.DoesNotContain(vm.Messages, m => m.Text.Contains("operator role", StringComparison.OrdinalIgnoreCase));
    }

    private MainWindowViewModel CreateViewModelWithAuthSession(string authSessionJson)
    {
        var dir = Path.Combine(Path.GetTempPath(), "openclaw-companion-connection-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        var vm = new MainWindowViewModel(
            new SettingsStore(dir),
            new GatewayWebSocketClient(),
            (baseUrl, authToken) => new OpenClawHttpClient(baseUrl, authToken, new HttpClient(new CallbackHandler(request =>
                request.RequestUri!.AbsolutePath == "/auth/session"
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(authSessionJson, Encoding.UTF8, "application/json") }
                    : new HttpResponseMessage(HttpStatusCode.NotFound)))));
        // Nothing listens here, so an attempted chat connection fails fast and visibly.
        vm.ServerUrl = "ws://127.0.0.1:9/ws";
        return vm;
    }

    private sealed class CallbackHandler(Func<HttpRequestMessage, HttpResponseMessage> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(callback(request));
    }

    private (MainWindowViewModel ViewModel, GatewayWebSocketClient Client) CreateConnectedViewModel()
    {
        var dir = Path.Combine(Path.GetTempPath(), "openclaw-companion-connection-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        var client = new GatewayWebSocketClient();
        var vm = new MainWindowViewModel(new SettingsStore(dir), client)
        {
            IsConnected = true,
            Status = "Connected"
        };
        return (vm, client);
    }
}
