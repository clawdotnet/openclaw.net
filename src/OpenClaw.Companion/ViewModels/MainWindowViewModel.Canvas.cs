using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenClaw.Core.Canvas;
using OpenClaw.Core.Models;

namespace OpenClaw.Companion.ViewModels;

public sealed partial class MainWindowViewModel
{
    private string? _canvasSessionId;
    private long _canvasEventSequence;

    [ObservableProperty]
    private bool _isCanvasVisible;

    [ObservableProperty]
    private string _canvasStatus = "Canvas idle.";

    [ObservableProperty]
    private string _canvasHtmlStatus = "";

    public ObservableCollection<A2UiFrameItem> CanvasFrames { get; } = new();

    public bool HasCanvasFrames => CanvasFrames.Count > 0;

    private async Task SendCanvasReadyAsync()
    {
        if (!_client.IsConnected)
            return;

        await _client.SendEnvelopeAsync(new WsClientEnvelope
        {
            Type = "canvas_ready",
            Capabilities =
            [
                "a2ui.v0_8",
                "canvas.present",
                "canvas.hide",
                "snapshot.state"
            ]
        }, CancellationToken.None);
    }

    private void HandleCanvasEnvelope(WsServerEnvelope envelope)
    {
        if (!IsCanvasServerEnvelope(envelope.Type))
            return;

        Dispatcher.UIThread.Post(() => _ = ApplyCanvasEnvelopeAsync(envelope));
    }

    private async Task ApplyCanvasEnvelopeAsync(WsServerEnvelope envelope)
    {
        _canvasSessionId = string.IsNullOrWhiteSpace(envelope.SessionId) ? _canvasSessionId : envelope.SessionId;
        var surfaceId = string.IsNullOrWhiteSpace(envelope.SurfaceId) ? "main" : envelope.SurfaceId!;

        try
        {
            switch (envelope.Type)
            {
                case "canvas_present":
                    IsCanvasVisible = true;
                    CanvasStatus = "Canvas visible.";
                    await SendCanvasAckAsync(envelope, success: true, error: null);
                    return;

                case "canvas_hide":
                    IsCanvasVisible = false;
                    CanvasStatus = "Canvas hidden.";
                    await SendCanvasAckAsync(envelope, success: true, error: null);
                    return;

                case "canvas_navigate":
                    await ApplyCanvasNavigateAsync(envelope);
                    return;

                case "a2ui_reset":
                    CanvasFrames.Clear();
                    OnPropertyChanged(nameof(HasCanvasFrames));
                    CanvasStatus = "Canvas reset.";
                    await SendCanvasAckAsync(envelope, success: true, error: null);
                    return;

                case "a2ui_push":
                    await ApplyA2UiPushAsync(envelope, surfaceId);
                    return;

                case "canvas_snapshot":
                    await SendCanvasSnapshotResultAsync(envelope);
                    return;

                case "a2ui_eval":
                    await SendCanvasEvalResultAsync(envelope, success: false, valueJson: null, error: "Companion native Canvas does not support A2UI eval.");
                    return;
            }
        }
        catch (Exception ex)
        {
            await SendCanvasAckAsync(envelope, success: false, error: ex.Message);
        }
    }

    private async Task ApplyCanvasNavigateAsync(WsServerEnvelope envelope)
    {
        if (string.Equals(envelope.Url, "about:blank", StringComparison.OrdinalIgnoreCase))
        {
            CanvasFrames.Clear();
            CanvasHtmlStatus = "";
            CanvasStatus = "Canvas navigated to blank.";
            OnPropertyChanged(nameof(HasCanvasFrames));
            await SendCanvasAckAsync(envelope, success: true, error: null);
            return;
        }

        var error = !string.IsNullOrWhiteSpace(envelope.Html)
            ? "Companion native Canvas does not support local HTML navigation without a WebView."
            : "Companion native Canvas only supports A2UI frames and about:blank navigation.";
        CanvasHtmlStatus = error;
        CanvasStatus = error;
        await SendCanvasAckAsync(envelope, success: false, error: error);
    }

    private async Task ApplyA2UiPushAsync(WsServerEnvelope envelope, string surfaceId)
    {
        var validation = A2UiFrameValidator.ValidateJsonl(envelope.Frames, maxFrames: 1_000, maxBytes: 512 * 1024);
        if (!validation.IsValid)
        {
            await SendCanvasAckAsync(envelope, success: false, error: validation.Error);
            return;
        }

        foreach (var line in envelope.Frames!.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            using var doc = JsonDocument.Parse(line);
            CanvasFrames.Add(A2UiFrameItem.FromJson(surfaceId, doc.RootElement, SendA2UiEventAsync));
        }

        IsCanvasVisible = true;
        CanvasStatus = $"Rendered {validation.FrameCount} A2UI frame(s).";
        OnPropertyChanged(nameof(HasCanvasFrames));
        await SendCanvasAckAsync(envelope, success: true, error: null);
    }

    private async Task SendCanvasAckAsync(WsServerEnvelope envelope, bool success, string? error)
    {
        await _client.SendEnvelopeAsync(new WsClientEnvelope
        {
            Type = "canvas_ack",
            RequestId = envelope.RequestId,
            SessionId = _canvasSessionId,
            SurfaceId = envelope.SurfaceId,
            Success = success,
            Error = error
        }, CancellationToken.None);
    }

    private async Task SendCanvasEvalResultAsync(WsServerEnvelope envelope, bool success, string? valueJson, string? error)
    {
        await _client.SendEnvelopeAsync(new WsClientEnvelope
        {
            Type = "canvas_eval_result",
            RequestId = envelope.RequestId,
            SessionId = _canvasSessionId,
            SurfaceId = envelope.SurfaceId,
            Success = success,
            ValueJson = valueJson,
            Error = error
        }, CancellationToken.None);
    }

    private async Task SendCanvasSnapshotResultAsync(WsServerEnvelope envelope)
    {
        var snapshot = BuildCanvasSnapshotJson(envelope.SurfaceId);
        await _client.SendEnvelopeAsync(new WsClientEnvelope
        {
            Type = "canvas_snapshot_result",
            RequestId = envelope.RequestId,
            SessionId = _canvasSessionId,
            SurfaceId = envelope.SurfaceId,
            Success = true,
            SnapshotMode = envelope.SnapshotMode ?? "state",
            SnapshotJson = snapshot
        }, CancellationToken.None);
    }

    private async Task SendA2UiEventAsync(A2UiFrameItem frame, string eventName, string valueJson)
    {
        if (!_client.IsConnected)
            return;

        await _client.SendEnvelopeAsync(new WsClientEnvelope
        {
            Type = "a2ui_event",
            SessionId = _canvasSessionId,
            SurfaceId = frame.SurfaceId,
            ComponentId = frame.Id,
            Event = eventName,
            ValueJson = valueJson,
            Sequence = Interlocked.Increment(ref _canvasEventSequence)
        }, CancellationToken.None);
    }

    private string BuildCanvasSnapshotJson(string? surfaceId)
    {
        var frames = CanvasFrames.Select(frame => new
        {
            frame.SurfaceId,
            frame.Id,
            frame.Type,
            frame.Title,
            frame.Text,
            frame.Label,
            frame.ValueText,
            frame.SelectedValue
        }).ToArray();

        return JsonSerializer.Serialize(new
        {
            type = "canvas_snapshot",
            surfaceId = string.IsNullOrWhiteSpace(surfaceId) ? "main" : surfaceId,
            visible = IsCanvasVisible,
            frameCount = frames.Length,
            frames,
            diagnostics = string.IsNullOrWhiteSpace(CanvasHtmlStatus) ? Array.Empty<string>() : new[] { CanvasHtmlStatus }
        });
    }

    [RelayCommand]
    private void ClearCanvas()
    {
        CanvasFrames.Clear();
        CanvasHtmlStatus = "";
        CanvasStatus = "Canvas cleared.";
        OnPropertyChanged(nameof(HasCanvasFrames));
    }

    private static bool IsCanvasServerEnvelope(string type)
        => type is "canvas_present" or "canvas_hide" or "canvas_navigate" or "canvas_snapshot" or "a2ui_push" or "a2ui_reset" or "a2ui_eval";
}
