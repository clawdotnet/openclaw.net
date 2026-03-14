using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Models;

namespace OpenClaw.Gateway;

internal sealed class ToolApprovalGrantStore
{
    private const string DirectoryName = "admin";
    private const string FileName = "tool-approval-policies.json";

    private readonly string _path;
    private readonly Lock _gate = new();
    private readonly ILogger<ToolApprovalGrantStore> _logger;
    private List<ToolApprovalGrant>? _cached;

    public ToolApprovalGrantStore(string storagePath, ILogger<ToolApprovalGrantStore> logger)
    {
        var rootedStoragePath = Path.IsPathRooted(storagePath)
            ? storagePath
            : Path.GetFullPath(storagePath);
        _path = Path.Combine(rootedStoragePath, DirectoryName, FileName);
        _logger = logger;
    }

    public IReadOnlyList<ToolApprovalGrant> List()
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            var items = LoadUnsafe();
            var changed = items.RemoveAll(item => item.ExpiresAtUtc is { } expiresAtUtc && expiresAtUtc <= now) > 0;
            if (changed)
                SaveUnsafe(items);
            return items.ToArray();
        }
    }

    public ToolApprovalGrant AddOrUpdate(ToolApprovalGrant grant)
    {
        lock (_gate)
        {
            var items = LoadUnsafe();
            items.RemoveAll(item => string.Equals(item.Id, grant.Id, StringComparison.Ordinal));
            items.Add(grant);
            SaveUnsafe(items);
            return grant;
        }
    }

    public bool Delete(string id)
    {
        lock (_gate)
        {
            var items = LoadUnsafe();
            var removed = items.RemoveAll(item => string.Equals(item.Id, id, StringComparison.Ordinal)) > 0;
            if (removed)
                SaveUnsafe(items);
            return removed;
        }
    }

    public ToolApprovalGrant? TryConsume(string sessionId, string channelId, string senderId, string toolName)
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            var items = LoadUnsafe();
            var changed = false;

            for (var i = items.Count - 1; i >= 0; i--)
            {
                var item = items[i];
                if (item.ExpiresAtUtc is { } expiresAtUtc && expiresAtUtc <= now)
                {
                    items.RemoveAt(i);
                    changed = true;
                    continue;
                }

                if (!string.Equals(item.ToolName, toolName, StringComparison.Ordinal))
                    continue;

                var scope = (item.Scope ?? "").Trim().ToLowerInvariant();
                var matched = scope switch
                {
                    "once" =>
                        string.Equals(item.ChannelId, channelId, StringComparison.Ordinal) &&
                        string.Equals(item.SenderId, senderId, StringComparison.Ordinal),
                    "session" =>
                        string.Equals(item.SessionId, sessionId, StringComparison.Ordinal),
                    "sender_tool_window" =>
                        string.Equals(item.ChannelId, channelId, StringComparison.Ordinal) &&
                        string.Equals(item.SenderId, senderId, StringComparison.Ordinal),
                    _ => false
                };

                if (!matched)
                    continue;

                if (scope == "once" || item.RemainingUses <= 1)
                {
                    items.RemoveAt(i);
                    SaveUnsafe(items);
                    return item;
                }

                var updated = new ToolApprovalGrant
                {
                    Id = item.Id,
                    Scope = item.Scope ?? scope,
                    ChannelId = item.ChannelId,
                    SenderId = item.SenderId,
                    SessionId = item.SessionId,
                    ToolName = item.ToolName,
                    CreatedAtUtc = item.CreatedAtUtc,
                    ExpiresAtUtc = item.ExpiresAtUtc,
                    GrantedBy = item.GrantedBy,
                    GrantSource = item.GrantSource,
                    RemainingUses = item.RemainingUses - 1
                };

                items[i] = updated;
                SaveUnsafe(items);
                return updated;
            }

            if (changed)
                SaveUnsafe(items);
            else
                _cached = items;

            return null;
        }
    }

    private List<ToolApprovalGrant> LoadUnsafe()
    {
        if (_cached is not null)
            return _cached;

        try
        {
            if (!File.Exists(_path))
            {
                _cached = [];
                return _cached;
            }

            var json = File.ReadAllText(_path);
            _cached = JsonSerializer.Deserialize(json, CoreJsonContext.Default.ListToolApprovalGrant) ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load tool approval grant store from {Path}", _path);
            _cached = [];
        }

        return _cached;
    }

    private void SaveUnsafe(List<ToolApprovalGrant> items)
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(items, CoreJsonContext.Default.ListToolApprovalGrant);
            File.WriteAllText(_path, json);
            _cached = items;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save tool approval grant store to {Path}", _path);
        }
    }
}
