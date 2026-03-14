using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Models;

namespace OpenClaw.Gateway;

internal sealed class WebhookDeliveryStore
{
    private const string DirectoryName = "admin";
    private const string DeadLetterDirectoryName = "webhook-dead-letter";

    private readonly string _deadLetterPath;
    private readonly ILogger<WebhookDeliveryStore> _logger;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _seenDeliveries = new(StringComparer.Ordinal);

    public WebhookDeliveryStore(string storagePath, ILogger<WebhookDeliveryStore> logger)
    {
        var rootedStoragePath = Path.IsPathRooted(storagePath)
            ? storagePath
            : Path.GetFullPath(storagePath);
        _deadLetterPath = Path.Combine(rootedStoragePath, DirectoryName, DeadLetterDirectoryName);
        _logger = logger;
    }

    public bool TryBegin(string source, string deliveryKey, TimeSpan ttl)
    {
        CleanupExpired();
        var now = DateTimeOffset.UtcNow;
        var key = BuildSeenKey(source, deliveryKey);
        return _seenDeliveries.TryAdd(key, now.Add(ttl));
    }

    public void RecordDeadLetter(WebhookDeadLetterRecord record)
    {
        try
        {
            Directory.CreateDirectory(_deadLetterPath);
            var path = Path.Combine(_deadLetterPath, $"{record.Entry.Id}.json");
            var json = JsonSerializer.Serialize(record, CoreJsonContext.Default.WebhookDeadLetterRecord);
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record webhook dead-letter item {Id}", record.Entry.Id);
        }
    }

    public IReadOnlyList<WebhookDeadLetterEntry> List()
    {
        if (!Directory.Exists(_deadLetterPath))
            return [];

        var items = new List<WebhookDeadLetterEntry>();
        foreach (var file in Directory.EnumerateFiles(_deadLetterPath, "*.json").OrderByDescending(static path => path, StringComparer.Ordinal))
        {
            try
            {
                var json = File.ReadAllText(file);
                var item = JsonSerializer.Deserialize(json, CoreJsonContext.Default.WebhookDeadLetterRecord);
                if (item?.Entry is not null)
                    items.Add(item.Entry);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read webhook dead-letter file {File}", file);
            }
        }

        return items.OrderByDescending(static item => item.CreatedAtUtc).ToArray();
    }

    public WebhookDeadLetterRecord? Get(string id)
    {
        var path = Path.Combine(_deadLetterPath, $"{id}.json");
        if (!File.Exists(path))
            return null;

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize(json, CoreJsonContext.Default.WebhookDeadLetterRecord);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read webhook dead-letter file {Id}", id);
            return null;
        }
    }

    public bool MarkReplayed(string id)
    {
        var record = Get(id);
        if (record is null)
            return false;

        var updated = new WebhookDeadLetterRecord
        {
            Entry = new WebhookDeadLetterEntry
            {
                Id = record.Entry.Id,
                Source = record.Entry.Source,
                DeliveryKey = record.Entry.DeliveryKey,
                EndpointName = record.Entry.EndpointName,
                ChannelId = record.Entry.ChannelId,
                SenderId = record.Entry.SenderId,
                SessionId = record.Entry.SessionId,
                CreatedAtUtc = record.Entry.CreatedAtUtc,
                Error = record.Entry.Error,
                PayloadPreview = record.Entry.PayloadPreview,
                Discarded = record.Entry.Discarded,
                ReplayedAtUtc = DateTimeOffset.UtcNow
            },
            ReplayMessage = record.ReplayMessage
        };

        RecordDeadLetter(updated);
        return true;
    }

    public bool MarkDiscarded(string id)
    {
        var record = Get(id);
        if (record is null)
            return false;

        var updated = new WebhookDeadLetterRecord
        {
            Entry = new WebhookDeadLetterEntry
            {
                Id = record.Entry.Id,
                Source = record.Entry.Source,
                DeliveryKey = record.Entry.DeliveryKey,
                EndpointName = record.Entry.EndpointName,
                ChannelId = record.Entry.ChannelId,
                SenderId = record.Entry.SenderId,
                SessionId = record.Entry.SessionId,
                CreatedAtUtc = record.Entry.CreatedAtUtc,
                Error = record.Entry.Error,
                PayloadPreview = record.Entry.PayloadPreview,
                Discarded = true,
                ReplayedAtUtc = record.Entry.ReplayedAtUtc
            },
            ReplayMessage = record.ReplayMessage
        };

        RecordDeadLetter(updated);
        return true;
    }

    public static string HashDeliveryKey(string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
    }

    private void CleanupExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in _seenDeliveries)
        {
            if (entry.Value <= now)
                _seenDeliveries.TryRemove(entry.Key, out _);
        }
    }

    private static string BuildSeenKey(string source, string deliveryKey)
        => $"{source}:{deliveryKey}";
}
