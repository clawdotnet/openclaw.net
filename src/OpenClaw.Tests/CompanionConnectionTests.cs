using System.Net.WebSockets;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using OpenClaw.Companion.Models;
using OpenClaw.Companion.Services;
using OpenClaw.Companion.ViewModels;
using Xunit;

namespace OpenClaw.Tests;

public sealed class CompanionConnectionTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { }
        }
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
