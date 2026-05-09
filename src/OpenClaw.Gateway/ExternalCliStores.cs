using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.ExternalCli;
using OpenClaw.Core.Models;

namespace OpenClaw.Gateway;

internal sealed class ExternalCliAuditStore : IExternalCliAuditSink
{
    private const string DirectoryName = "admin";
    private const string FileName = "external-cli-audit.jsonl";
    private readonly string _path;
    private readonly object _gate = new();
    private readonly ILogger<ExternalCliAuditStore> _logger;

    public ExternalCliAuditStore(string storagePath, ILogger<ExternalCliAuditStore> logger)
    {
        var rootedStoragePath = System.IO.Path.IsPathRooted(storagePath)
            ? storagePath
            : System.IO.Path.GetFullPath(storagePath);
        _path = System.IO.Path.Join(rootedStoragePath, DirectoryName, FileName);
        _logger = logger;
    }

    public string Path => _path;

    public void Record(ExternalCliAuditEntry entry)
    {
        var line = JsonSerializer.Serialize(entry, CoreJsonContext.Default.ExternalCliAuditEntry);
        if (!AtomicJsonFileStore.TryAppendLine(_path, line, _gate, out var error))
            _logger.LogWarning("Failed to append external CLI audit entry to {Path}: {Error}", _path, error);
    }
}

internal sealed class ExternalCliRuntimeEventSink : IExternalCliEventSink
{
    private readonly RuntimeEventStore _events;

    public ExternalCliRuntimeEventSink(RuntimeEventStore events)
    {
        _events = events;
    }

    public void Record(ExternalCliRuntimeEvent entry)
    {
        _events.Append(new RuntimeEventEntry
        {
            Id = $"evt_{Guid.NewGuid():N}"[..20],
            SessionId = entry.SessionId,
            ChannelId = entry.ChannelId,
            SenderId = entry.SenderId,
            Component = "external_cli",
            Action = entry.Action,
            Severity = string.IsNullOrWhiteSpace(entry.Severity) ? "info" : entry.Severity,
            Summary = entry.Summary,
            Metadata = entry.Metadata
        });
    }
}
