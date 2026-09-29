using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Models;
using OpenClaw.Core.Plugins;

namespace OpenClaw.Agent.Plugins;

public abstract class BridgeTransportBase : IBridgeTransport
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<BridgeResponse>> _pending = new();
    private readonly ILogger _logger;
    private int _nextId;
    private TextReader? _reader;
    private TextWriter? _writer;
    private Task? _readLoop;
    private Action<BridgeNotification>? _notificationHandler;
    private volatile bool _disposed;

    protected BridgeTransportBase(ILogger logger)
    {
        _logger = logger;
    }

    public virtual Task PrepareAsync(CancellationToken ct) => Task.CompletedTask;

    public void SetNotificationHandler(Action<BridgeNotification> handler)
        => _notificationHandler = handler;

    public abstract Task StartAsync(Process process, CancellationToken ct);

    protected void AttachReaderWriter(TextReader reader, TextWriter writer)
    {
        _reader = reader;
        _writer = writer;
        _readLoop = Task.Run(ReadLoopAsync, CancellationToken.None);
    }

    public Task SendRequestAsync(string method, JsonElement? parameters, CancellationToken ct)
        => SendAndWaitAsync(method, parameters, ct);

    public async Task<BridgeResponse> SendAndWaitAsync(string method, JsonElement? parameters, CancellationToken ct)
    {
        if (_writer is null)
            throw new InvalidOperationException("Bridge transport is not ready.");

        var id = Interlocked.Increment(ref _nextId).ToString();
        var tcs = new TaskCompletionSource<BridgeResponse>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
        _pending[id] = tcs;

        try
        {
            var request = new BridgeRequest
            {
                Method = method,
                Id = id,
                Params = parameters
            };

            var requestJson = JsonSerializer.Serialize(request, CoreJsonContext.Default.BridgeRequest);
            await _writer.WriteLineAsync(requestJson.AsMemory(), ct);
            await _writer.FlushAsync();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(60));
            return await tcs.Task.WaitAsync(timeoutCts.Token);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        FailPendingRequests();
        await DisposeCoreAsync();
        if (_readLoop is not null)
        {
            try { await _readLoop.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch { /* read loop may throw on stream closure — expected */ }
        }
    }

    protected virtual ValueTask DisposeCoreAsync() => ValueTask.CompletedTask;

    private async Task ReadLoopAsync()
    {
        if (_reader is null)
            return;

        try
        {
            while (!_disposed)
            {
                var line = await _reader.ReadLineAsync();
                if (line is null)
                    break;

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.TryGetProperty("notification", out _))
                    {
                        var notification = JsonSerializer.Deserialize(line, CoreJsonContext.Default.BridgeNotification);
                        if (notification is not null)
                            _notificationHandler?.Invoke(notification);
                    }
                    else
                    {
                        var response = JsonSerializer.Deserialize(line, CoreJsonContext.Default.BridgeResponse);
                        if (response?.Id is not null && _pending.TryRemove(response.Id, out var tcs))
                            tcs.TrySetResult(response);
                    }
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Plugin bridge emitted malformed JSON: {Line}", Truncate(line, 200));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Plugin bridge emitted unreadable output: {Line}", Truncate(line, 200));
                }
            }
        }
        catch
        {
            // Stream closed while process exited or transport disposed.
        }

        FailPendingRequests();
    }

    // Fail rather than cancel: the caller did not cancel, and a cancellation would be reported as a tool timeout.
    // Not an IOException either, so the hybrid transport does not resend a request the child may already have run.
    protected void FailPendingRequests()
    {
        foreach (var kvp in _pending)
            kvp.Value.TrySetException(new InvalidOperationException("The plugin bridge connection closed before the request completed."));

        _pending.Clear();
    }

    private static string Truncate(string value, int maxChars)
        => value.Length <= maxChars ? value : value[..maxChars] + "...";
}
