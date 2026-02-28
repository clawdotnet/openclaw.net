using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Models;

namespace OpenClaw.Core.Pipeline;

public sealed record RecentSenderEntry
{
    public required string SenderId { get; init; }
    public string? SenderName { get; init; }
    public DateTimeOffset LastSeenUtc { get; init; }
}

public sealed record RecentSendersFile
{
    public List<RecentSenderEntry> Senders { get; init; } = [];
}

/// <summary>
/// Tracks recent inbound senders per channel to support onboarding ("add latest sender to allowlist").
/// Backed by JSON files under {StoragePath}/recent_senders/{channel}.json.
/// </summary>
public sealed class RecentSendersStore
{
    private readonly string _rootDir;
    private readonly ILogger<RecentSendersStore> _logger;
    private readonly ConcurrentDictionary<string, object> _locks = new(StringComparer.Ordinal);
    private readonly int _maxEntries;

    public RecentSendersStore(string baseStoragePath, ILogger<RecentSendersStore> logger, int maxEntries = 50)
    {
        _rootDir = Path.Combine(baseStoragePath, "recent_senders");
        _logger = logger;
        _maxEntries = Math.Clamp(maxEntries, 5, 500);
    }

    public async Task RecordAsync(string channelId, string senderId, string? senderName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(channelId) || string.IsNullOrWhiteSpace(senderId))
            return;

        var gate = _locks.GetOrAdd(channelId, _ => new object());
        lock (gate)
        {
            Directory.CreateDirectory(_rootDir);
        }

        try
        {
            var path = GetPath(channelId);
            RecentSendersFile file;

            lock (gate)
            {
                file = TryReadUnlocked(path);

                var now = DateTimeOffset.UtcNow;
                var existingIdx = file.Senders.FindIndex(s => string.Equals(s.SenderId, senderId, StringComparison.Ordinal));
                if (existingIdx >= 0)
                {
                    var existing = file.Senders[existingIdx];
                    file.Senders.RemoveAt(existingIdx);
                    file.Senders.Insert(0, existing with { SenderName = senderName ?? existing.SenderName, LastSeenUtc = now });
                }
                else
                {
                    file.Senders.Insert(0, new RecentSenderEntry
                    {
                        SenderId = senderId,
                        SenderName = senderName,
                        LastSeenUtc = now
                    });
                }

                if (file.Senders.Count > _maxEntries)
                    file.Senders.RemoveRange(_maxEntries, file.Senders.Count - _maxEntries);

                var json = JsonSerializer.Serialize(file, CoreJsonContext.Default.RecentSendersFile);
                File.WriteAllText(path, json);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record recent sender for channel={ChannelId}", channelId);
        }

        await Task.CompletedTask;
    }

    public RecentSenderEntry? TryGetLatest(string channelId)
    {
        if (string.IsNullOrWhiteSpace(channelId))
            return null;

        try
        {
            var path = GetPath(channelId);
            if (!File.Exists(path))
                return null;

            var file = JsonSerializer.Deserialize(File.ReadAllText(path), CoreJsonContext.Default.RecentSendersFile);
            return file?.Senders.Count > 0 ? file.Senders[0] : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read recent senders for channel={ChannelId}", channelId);
            return null;
        }
    }

    public RecentSendersFile GetSnapshot(string channelId)
    {
        try
        {
            var path = GetPath(channelId);
            if (!File.Exists(path))
                return new RecentSendersFile();

            return JsonSerializer.Deserialize(File.ReadAllText(path), CoreJsonContext.Default.RecentSendersFile)
                   ?? new RecentSendersFile();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read recent senders snapshot for channel={ChannelId}", channelId);
            return new RecentSendersFile();
        }
    }

    private string GetPath(string channelId)
    {
        var safe = string.Concat(channelId.Where(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.'));
        if (string.IsNullOrWhiteSpace(safe))
            safe = "unknown";
        return Path.Combine(_rootDir, safe + ".json");
    }

    private static RecentSendersFile TryReadUnlocked(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new RecentSendersFile();

            var text = File.ReadAllText(path);
            return JsonSerializer.Deserialize(text, CoreJsonContext.Default.RecentSendersFile)
                   ?? new RecentSendersFile();
        }
        catch
        {
            return new RecentSendersFile();
        }
    }
}
