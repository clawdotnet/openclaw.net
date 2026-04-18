using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace OpenClaw.Core.Observability;

/// <summary>
/// A single audit record for a tool execution. Append-only, written to a JSON-lines file.
/// </summary>
public sealed record ToolAuditEntry
{
    public required DateTimeOffset TimestampUtc { get; init; }
    public required string ToolName { get; init; }
    public required string SessionId { get; init; }
    public required string ChannelId { get; init; }
    public required string SenderId { get; init; }
    public required string CorrelationId { get; init; }
    public required double DurationMs { get; init; }
    public required bool Failed { get; init; }
    public required bool TimedOut { get; init; }
    public string? ApprovalId { get; init; }
    public int ArgumentsBytes { get; init; }
    public int ResultBytes { get; init; }
}

/// <summary>
/// Thread-safe, append-only JSON-lines writer for tool execution audit entries.
/// Intentionally omits raw arguments and result payloads by default to avoid leaking secrets;
/// only byte counts are recorded. Callers can log redacted summaries via the logger separately.
/// </summary>
public sealed class ToolAuditLog : IDisposable
{
    private readonly string? _filePath;
    private readonly ILogger<ToolAuditLog>? _logger;
    private readonly Lock _gate = new();
    private readonly List<ToolAuditEntry> _recent = new(capacity: 256);
    private const int RecentBufferCapacity = 256;

    public ToolAuditLog(string? filePath, ILogger<ToolAuditLog>? logger = null)
    {
        _logger = logger;

        var configuredFilePath = string.IsNullOrWhiteSpace(filePath) ? null : filePath;
        if (configuredFilePath is not null)
        {
            try
            {
                var dir = Path.GetDirectoryName(configuredFilePath);
                if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                _logger?.LogWarning(ex,
                    "Failed to initialize audit log directory for {Path}; file logging will be disabled",
                    configuredFilePath);
                configuredFilePath = null;
            }
        }

        _filePath = configuredFilePath;
    }

    public void Record(ToolAuditEntry entry)
    {
        string? filePath;
        lock (_gate)
        {
            if (_recent.Count >= RecentBufferCapacity)
                _recent.RemoveAt(0);
            _recent.Add(entry);

            filePath = _filePath;
        }

        if (filePath is null) return;

        try
        {
            var json = JsonSerializer.Serialize(entry, ToolAuditJsonContext.Default.ToolAuditEntry);
            File.AppendAllText(filePath, json + "\n");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            _logger?.LogWarning(ex, "Failed to append tool audit entry to {Path}", filePath);
        }
    }

    public IReadOnlyList<ToolAuditEntry> SnapshotRecent(int limit = 100)
    {
        lock (_gate)
        {
            var count = Math.Min(limit, _recent.Count);
            if (count <= 0) return [];
            var result = new ToolAuditEntry[count];
            for (var i = 0; i < count; i++)
                result[i] = _recent[_recent.Count - count + i];
            return result;
        }
    }

    public void Dispose()
    {
        // No resources to release - file is opened/closed per Record call.
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(ToolAuditEntry))]
[JsonSerializable(typeof(IReadOnlyList<ToolAuditEntry>))]
[JsonSerializable(typeof(List<ToolAuditEntry>))]
[JsonSerializable(typeof(ToolAuditEntry[]))]
internal sealed partial class ToolAuditJsonContext : JsonSerializerContext;
