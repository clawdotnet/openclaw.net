using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using OpenClaw.Channels;
using OpenClaw.Core.Models;

namespace OpenClaw.Gateway;

internal sealed record CanvasCommandResult(
    bool Success,
    string RequestId,
    string? Error = null,
    string? SnapshotJson = null,
    string? ValueJson = null);

internal sealed class CanvasCommandBroker
{
    private readonly GatewayConfig _config;
    private readonly WebSocketChannel _webSocketChannel;
    private readonly RuntimeEventStore _runtimeEvents;
    private readonly ConcurrentDictionary<string, PendingCommand> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string[]> _clientCapabilities = new(StringComparer.Ordinal);

    public CanvasCommandBroker(GatewayConfig config, WebSocketChannel webSocketChannel, RuntimeEventStore runtimeEvents)
    {
        _config = config;
        _webSocketChannel = webSocketChannel;
        _runtimeEvents = runtimeEvents;
        _webSocketChannel.OnCanvasClientEnvelopeReceived += HandleClientEnvelopeAsync;
    }

    public IReadOnlyList<string> GetClientCapabilities(string senderId)
        => _clientCapabilities.TryGetValue(senderId, out var capabilities) ? capabilities : [];

    public async Task<CanvasCommandResult> SendCommandAsync(
        Session session,
        WsServerEnvelope command,
        string expectedResponseType,
        string? requiredCapability,
        CancellationToken ct)
    {
        if (!_config.Canvas.Enabled)
            return Fail(command.RequestId, "Canvas is disabled.");

        if (!string.Equals(session.ChannelId, "websocket", StringComparison.Ordinal))
            return Fail(command.RequestId, "Canvas commands require an active websocket session.");

        if (!_webSocketChannel.IsClientConnected(session.SenderId) || !_webSocketChannel.IsClientUsingEnvelopes(session.SenderId))
            return Fail(command.RequestId, "Canvas client is not connected in websocket envelope mode.");

        if (!string.IsNullOrWhiteSpace(requiredCapability) && !HasCapability(session.SenderId, requiredCapability))
        {
            var advertised = GetClientCapabilities(session.SenderId);
            return Fail(
                command.RequestId,
                advertised.Count == 0
                    ? "Canvas client has not advertised capabilities yet."
                    : $"Canvas client does not support capability '{requiredCapability}'.");
        }

        var requestId = string.IsNullOrWhiteSpace(command.RequestId)
            ? $"canvas_{Guid.NewGuid():N}"[..24]
            : command.RequestId!;

        var outbound = command with
        {
            RequestId = requestId,
            SessionId = session.Id,
            SurfaceId = string.IsNullOrWhiteSpace(command.SurfaceId) ? "main" : command.SurfaceId
        };

        var bytes = JsonSerializer.SerializeToUtf8Bytes(outbound, CoreJsonContext.Default.WsServerEnvelope);
        if (bytes.Length > Math.Max(1, _config.Canvas.MaxCommandBytes))
            return Fail(requestId, $"Canvas command exceeds {_config.Canvas.MaxCommandBytes} bytes.");

        var pending = new PendingCommand(requestId, session.Id, session.SenderId, expectedResponseType);
        if (!_pending.TryAdd(requestId, pending))
            return Fail(requestId, "Canvas command request id collision.");

        AppendRuntimeEvent(session, outbound.Type, requestId, "sent", "info", $"Sent Canvas command '{outbound.Type}'.");

        try
        {
            await _webSocketChannel.SendEnvelopeAsync(session.SenderId, outbound, ct);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_config.Canvas.CommandTimeoutSeconds, 1, 300)));
            var response = await pending.Task.WaitAsync(timeoutCts.Token);

            if (!string.Equals(response.Type, expectedResponseType, StringComparison.Ordinal))
                return Fail(requestId, $"Unexpected Canvas response '{response.Type}'.");

            if (!string.IsNullOrWhiteSpace(response.SnapshotJson) &&
                Encoding.UTF8.GetByteCount(response.SnapshotJson) > Math.Max(1, _config.Canvas.MaxSnapshotBytes))
            {
                return Fail(requestId, $"Canvas snapshot exceeds {_config.Canvas.MaxSnapshotBytes} bytes.");
            }

            if (response.Success == false || !string.IsNullOrWhiteSpace(response.Error))
            {
                var error = string.IsNullOrWhiteSpace(response.Error) ? "Canvas command failed." : response.Error!;
                AppendRuntimeEvent(session, outbound.Type, requestId, "failed", "warning", error);
                return Fail(requestId, error);
            }

            AppendRuntimeEvent(session, outbound.Type, requestId, "completed", "info", $"Canvas command '{outbound.Type}' completed.");
            return new CanvasCommandResult(true, requestId, SnapshotJson: response.SnapshotJson, ValueJson: response.ValueJson);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            AppendRuntimeEvent(session, outbound.Type, requestId, "timed_out", "warning", $"Canvas command '{outbound.Type}' timed out.");
            return Fail(requestId, "Canvas command timed out.");
        }
        finally
        {
            _pending.TryRemove(requestId, out _);
        }
    }

    internal ValueTask HandleClientEnvelopeAsync(string clientId, WsClientEnvelope envelope, CancellationToken ct)
    {
        if (string.Equals(envelope.Type, "canvas_ready", StringComparison.Ordinal))
        {
            _clientCapabilities[clientId] = NormalizeCapabilities(envelope.Capabilities);
            return ValueTask.CompletedTask;
        }

        if (envelope.Capabilities is { Length: > 0 })
            _clientCapabilities[clientId] = NormalizeCapabilities(envelope.Capabilities);

        if (!string.IsNullOrWhiteSpace(envelope.RequestId) &&
            _pending.TryGetValue(envelope.RequestId, out var pending) &&
            string.Equals(pending.SenderId, clientId, StringComparison.Ordinal))
        {
            if (!string.IsNullOrWhiteSpace(envelope.SessionId) &&
                !string.Equals(envelope.SessionId, pending.SessionId, StringComparison.Ordinal))
            {
                pending.TrySetResult(envelope with
                {
                    Success = false,
                    Error = "Canvas response session id did not match the pending command."
                });
                return ValueTask.CompletedTask;
            }

            pending.TrySetResult(envelope);
        }

        return ValueTask.CompletedTask;
    }

    private bool HasCapability(string senderId, string capability)
        => _clientCapabilities.TryGetValue(senderId, out var capabilities) &&
           capabilities.Contains(capability, StringComparer.OrdinalIgnoreCase);

    private static string[] NormalizeCapabilities(string[]? capabilities)
        => capabilities is null
            ? []
            : capabilities
                .Where(static item => !string.IsNullOrWhiteSpace(item))
                .Select(static item => item.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

    private void AppendRuntimeEvent(Session session, string action, string requestId, string state, string severity, string summary)
    {
        _runtimeEvents.Append(new RuntimeEventEntry
        {
            Id = $"evt_{Guid.NewGuid():N}"[..20],
            SessionId = session.Id,
            ChannelId = session.ChannelId,
            SenderId = session.SenderId,
            Component = "canvas",
            Action = state,
            Severity = severity,
            Summary = summary,
            Metadata = new Dictionary<string, string>
            {
                ["requestId"] = requestId,
                ["command"] = action
            }
        });
    }

    private static CanvasCommandResult Fail(string? requestId, string error)
        => new(false, string.IsNullOrWhiteSpace(requestId) ? "" : requestId!, error);

    private sealed class PendingCommand
    {
        private readonly TaskCompletionSource<WsClientEnvelope> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public PendingCommand(string requestId, string sessionId, string senderId, string expectedResponseType)
        {
            RequestId = requestId;
            SessionId = sessionId;
            SenderId = senderId;
            ExpectedResponseType = expectedResponseType;
        }

        public string RequestId { get; }
        public string SessionId { get; }
        public string SenderId { get; }
        public string ExpectedResponseType { get; }
        public Task<WsClientEnvelope> Task => _tcs.Task;

        public void TrySetResult(WsClientEnvelope envelope)
            => _tcs.TrySetResult(envelope);
    }
}
