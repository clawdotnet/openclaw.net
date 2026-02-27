using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Models;

namespace OpenClaw.Core.Security;

public sealed record ChannelAllowlistFile
{
    public string[] AllowedFrom { get; init; } = [];
    public string[] AllowedTo { get; init; } = [];
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Persists per-channel allowlists to disk under {StoragePath}/allowlists/{channel}.json.
/// If a dynamic allowlist file exists, it takes precedence over static config allowlists.
/// </summary>
public sealed class AllowlistManager
{
    private readonly string _rootDir;
    private readonly ILogger<AllowlistManager> _logger;
    private readonly ConcurrentDictionary<string, object> _locks = new(StringComparer.Ordinal);

    public AllowlistManager(string baseStoragePath, ILogger<AllowlistManager> logger)
    {
        _rootDir = Path.Combine(baseStoragePath, "allowlists");
        _logger = logger;
        Directory.CreateDirectory(_rootDir);
    }

    public ChannelAllowlistFile? TryGetDynamic(string channelId)
    {
        var path = GetPath(channelId);
        if (!File.Exists(path))
            return null;

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize(json, CoreJsonContext.Default.ChannelAllowlistFile);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read allowlist file for channel={ChannelId}", channelId);
            return null;
        }
    }

    public ChannelAllowlistFile GetEffective(string channelId, ChannelAllowlistFile configAllowlist)
        => TryGetDynamic(channelId) ?? configAllowlist;

    public void UpsertDynamic(string channelId, Func<ChannelAllowlistFile?, ChannelAllowlistFile> update)
    {
        var gate = _locks.GetOrAdd(channelId, _ => new object());
        lock (gate)
        {
            var current = TryGetDynamic(channelId);
            var next = update(current) with { UpdatedAtUtc = DateTimeOffset.UtcNow };

            try
            {
                Directory.CreateDirectory(_rootDir);
                var json = JsonSerializer.Serialize(next, CoreJsonContext.Default.ChannelAllowlistFile);
                File.WriteAllText(GetPath(channelId), json);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to persist allowlist file for channel={ChannelId}", channelId);
            }
        }
    }

    public void AddAllowedFrom(string channelId, string senderId)
    {
        if (string.IsNullOrWhiteSpace(senderId))
            return;

        UpsertDynamic(channelId, cur =>
        {
            cur ??= new ChannelAllowlistFile();
            if (cur.AllowedFrom.Contains(senderId, StringComparer.Ordinal))
                return cur;

            var next = cur.AllowedFrom.Concat([senderId]).ToArray();
            return cur with { AllowedFrom = next };
        });
    }

    public void SetAllowedFrom(string channelId, IEnumerable<string> senderIds)
    {
        var list = senderIds
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        UpsertDynamic(channelId, cur =>
        {
            cur ??= new ChannelAllowlistFile();
            return cur with { AllowedFrom = list };
        });
    }

    private string GetPath(string channelId)
    {
        var safe = string.Concat(channelId.Where(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.'));
        if (string.IsNullOrWhiteSpace(safe))
            safe = "unknown";
        return Path.Combine(_rootDir, safe + ".json");
    }
}
