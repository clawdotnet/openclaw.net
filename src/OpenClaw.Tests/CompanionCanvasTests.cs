using System.Reflection;
using System.Text;
using System.Text.Json;
using OpenClaw.Companion.Services;
using OpenClaw.Companion.ViewModels;
using OpenClaw.Core.Models;
using Xunit;

namespace OpenClaw.Tests;

public sealed class CompanionCanvasTests : IDisposable
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

    [Fact]
    public async Task ApplyCanvasEnvelope_PushRendersNativeFramesAndAcks()
    {
        var (viewModel, ws) = CreateViewModel();

        await ApplyCanvasEnvelopeAsync(viewModel, new WsServerEnvelope
        {
            Type = "a2ui_push",
            RequestId = "req1",
            SessionId = "sess",
            SurfaceId = "main",
            Frames = """
                {"type":"button","id":"save","label":"Save"}
                {"type":"input","id":"name","label":"Name","value":"Ada"}
                """
        });

        Assert.True(viewModel.IsCanvasVisible);
        Assert.True(viewModel.HasCanvasFrames);
        Assert.Equal(2, viewModel.CanvasFrames.Count);
        Assert.Equal("Ada", viewModel.CanvasFrames.Single(frame => frame.Id == "name").ValueText);

        var ack = LastSentEnvelope(ws);
        Assert.Equal("canvas_ack", ack.Type);
        Assert.Equal("req1", ack.RequestId);
        Assert.True(ack.Success);
    }

    [Fact]
    public async Task ApplyCanvasEnvelope_ButtonEventSendsA2UiEvent()
    {
        var (viewModel, ws) = CreateViewModel();
        await ApplyCanvasEnvelopeAsync(viewModel, new WsServerEnvelope
        {
            Type = "a2ui_push",
            RequestId = "req1",
            SessionId = "sess",
            SurfaceId = "main",
            Frames = """{"type":"button","id":"save","label":"Save"}"""
        });

        await viewModel.CanvasFrames.Single().ActivateCommand.ExecuteAsync(null);

        var evt = LastSentEnvelope(ws);
        Assert.Equal("a2ui_event", evt.Type);
        Assert.Equal("sess", evt.SessionId);
        Assert.Equal("save", evt.ComponentId);
        Assert.Equal("click", evt.Event);
        Assert.Equal("true", evt.ValueJson);
        Assert.Equal(1, evt.Sequence);
    }

    [Fact]
    public async Task ApplyCanvasEnvelope_SnapshotReturnsStateJson()
    {
        var (viewModel, ws) = CreateViewModel();
        await ApplyCanvasEnvelopeAsync(viewModel, new WsServerEnvelope
        {
            Type = "a2ui_push",
            RequestId = "req1",
            SessionId = "sess",
            SurfaceId = "main",
            Frames = """{"type":"text","id":"summary","text":"Ready"}"""
        });

        await ApplyCanvasEnvelopeAsync(viewModel, new WsServerEnvelope
        {
            Type = "canvas_snapshot",
            RequestId = "snap1",
            SessionId = "sess",
            SurfaceId = "main"
        });

        var snapshot = LastSentEnvelope(ws);
        Assert.Equal("canvas_snapshot_result", snapshot.Type);
        Assert.Equal("snap1", snapshot.RequestId);
        Assert.NotNull(snapshot.SnapshotJson);

        using var doc = JsonDocument.Parse(snapshot.SnapshotJson!);
        Assert.Equal(1, doc.RootElement.GetProperty("frameCount").GetInt32());
        Assert.True(doc.RootElement.GetProperty("visible").GetBoolean());
    }

    [Fact]
    public void A2UiFrameItem_ClampsNegativeProgressToZero()
    {
        using var doc = JsonDocument.Parse("""{"type":"progress","id":"p","value":-0.25}""");

        var item = A2UiFrameItem.FromJson("main", doc.RootElement, (_, _, _) => Task.CompletedTask);

        Assert.Equal(0, item.ProgressValue);
    }

    [Fact]
    public async Task ApplyCanvasEnvelope_SnapshotBoundsReturnedFrames()
    {
        var (viewModel, ws) = CreateViewModel();
        var frames = string.Join('\n', Enumerable.Range(0, 105)
            .Select(i => $$"""{"type":"text","id":"f{{i}}","text":"{{new string('x', 2000)}}"}"""));
        await ApplyCanvasEnvelopeAsync(viewModel, new WsServerEnvelope
        {
            Type = "a2ui_push",
            RequestId = "req1",
            SessionId = "sess",
            SurfaceId = "main",
            Frames = frames
        });

        await ApplyCanvasEnvelopeAsync(viewModel, new WsServerEnvelope
        {
            Type = "canvas_snapshot",
            RequestId = "snap1",
            SessionId = "sess",
            SurfaceId = "main"
        });

        var snapshot = LastSentEnvelope(ws);
        using var doc = JsonDocument.Parse(snapshot.SnapshotJson!);

        Assert.Equal(105, doc.RootElement.GetProperty("frameCount").GetInt32());
        Assert.True(doc.RootElement.GetProperty("truncated").GetBoolean());
        Assert.True(snapshot.SnapshotJson!.Length <= 128 * 1024);
    }

    private (MainWindowViewModel ViewModel, TestWebSocket Socket) CreateViewModel()
    {
        var dir = Path.Combine(Path.GetTempPath(), "openclaw-companion-canvas-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);

        var client = new GatewayWebSocketClient();
        var ws = new TestWebSocket();
        client.SetConnectedSocketForTest(ws);
        return (new MainWindowViewModel(new SettingsStore(dir), client), ws);
    }

    private static async Task ApplyCanvasEnvelopeAsync(MainWindowViewModel viewModel, WsServerEnvelope envelope)
    {
        var method = typeof(MainWindowViewModel).GetMethod(
            "ApplyCanvasEnvelopeAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var task = (Task?)method!.Invoke(viewModel, [envelope]);
        Assert.NotNull(task);
        await task!;
    }

    private static WsClientEnvelope LastSentEnvelope(TestWebSocket ws)
    {
        var payload = Encoding.UTF8.GetString(ws.Sent.Last());
        return JsonSerializer.Deserialize(payload, CoreJsonContext.Default.WsClientEnvelope)
            ?? throw new InvalidOperationException("Sent payload was not a websocket envelope.");
    }
}
