using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;

namespace OpenClaw.Core.Observability;

/// <summary>
/// Thread-safe, append-only JSON-lines writer for per-turn token usage.
/// </summary>
public sealed class TurnTokenUsageAuditLog : ITurnTokenUsageObserver, IDisposable
{
    private const int DefaultAuditQueueCapacity = 4096;
    private readonly string? _filePath;
    private readonly ILogger<TurnTokenUsageAuditLog>? _logger;
    private readonly BlockingCollection<string>? _lineQueue;
    private readonly Task? _writerTask;
    private int _disposed;

    public TurnTokenUsageAuditLog(
        string? filePath,
        ILogger<TurnTokenUsageAuditLog>? logger = null,
        int auditQueueCapacity = DefaultAuditQueueCapacity)
    {
        _logger = logger;
        if (string.IsNullOrWhiteSpace(filePath))
            return;

        try
        {
            var fullPath = Path.GetFullPath(filePath);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            _filePath = fullPath;

            _lineQueue = new BlockingCollection<string>(
                new ConcurrentQueue<string>(),
                Math.Max(1, auditQueueCapacity));
            _writerTask = Task.Factory.StartNew(
                () => WriteLoop(fullPath, _lineQueue),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to initialize turn token usage audit path for {Path}; file logging will be disabled", filePath);
            _filePath = null;
        }
    }

    public void RecordTurn(TurnTokenUsageRecord record)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        var filePath = _filePath;
        var lineQueue = _lineQueue;
        if (string.IsNullOrWhiteSpace(filePath) || lineQueue is null)
            return;

        try
        {
            var json = JsonSerializer.Serialize(record, TurnTokenUsageJsonContext.Default.TurnTokenUsageRecord);
            if (!lineQueue.TryAdd(json))
                lineQueue.Add(json);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to append turn token usage entry to {Path}", filePath);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        if (_lineQueue is null)
            return;

        try
        {
            _lineQueue.CompleteAdding();
            _writerTask?.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to flush turn token usage audit log during disposal");
        }
        finally
        {
            _lineQueue.Dispose();
        }
    }

    private void WriteLoop(string filePath, BlockingCollection<string> queue)
    {
        using var stream = new FileStream(
            filePath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.None);
        using var writer = new StreamWriter(stream);

        foreach (var line in queue.GetConsumingEnumerable())
        {
            try
            {
                writer.WriteLine(line);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to append turn token usage entry to {Path}", filePath);
            }
        }

        writer.Flush();
    }
}

public sealed class CompositeTurnTokenUsageObserver : ITurnTokenUsageObserver
{
    private readonly IReadOnlyList<ITurnTokenUsageObserver> _observers;
    private readonly ILogger<CompositeTurnTokenUsageObserver>? _logger;

    public CompositeTurnTokenUsageObserver(
        IReadOnlyList<ITurnTokenUsageObserver> observers,
        ILogger<CompositeTurnTokenUsageObserver>? logger = null)
    {
        _observers = observers;
        _logger = logger;
    }

    public void RecordTurn(TurnTokenUsageRecord record)
    {
        foreach (var observer in _observers)
        {
            try
            {
                observer.RecordTurn(record);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(
                    ex,
                    "Turn token usage observer {ObserverType} failed while recording session {SessionId}",
                    observer.GetType().FullName,
                    record.SessionId);
            }
        }
    }
}

[JsonSerializable(typeof(TurnTokenUsageRecord))]
internal sealed partial class TurnTokenUsageJsonContext : JsonSerializerContext;
