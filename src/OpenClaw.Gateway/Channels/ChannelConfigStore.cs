using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using OpenClaw.Core.Models;

namespace OpenClaw.Gateway.Channels;

/// <summary>
/// Persists channel configs to <c>{StoragePath}/channels/channel-{id}.json</c>.
/// This directory lives on the mounted data volume and survives container restarts —
/// unlike appsettings.json, which is baked into the image.
///
/// Usage:
///   Load at startup: <see cref="TryLoad{T}"/>
///   Save from admin endpoint: <see cref="Save{T}"/>
/// </summary>
internal sealed class ChannelConfigStore
{
    private const string ChannelsDirName = "channels";

    private readonly string _dir;
    private readonly ILogger<ChannelConfigStore> _logger;

    public ChannelConfigStore(string storagePath, ILogger<ChannelConfigStore> logger)
    {
        var root = Path.IsPathRooted(storagePath)
            ? storagePath
            : Path.GetFullPath(storagePath);
        _dir = Path.Combine(root, ChannelsDirName);
        _logger = logger;
    }

    /// <summary>
    /// Tries to read a persisted channel config.
    /// Returns <c>null</c> if the file does not exist or cannot be parsed.
    /// </summary>
    public T? TryLoad<T>(string channelId, JsonTypeInfo<T> typeInfo)
    {
        var path = FilePath(channelId);
        if (!File.Exists(path))
            return default;

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize(json, typeInfo);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load persisted config for channel '{ChannelId}' from '{Path}'.", channelId, path);
            return default;
        }
    }

    /// <summary>
    /// Persists a channel config to disk so it survives container restarts.
    /// Creates the channels directory if it doesn't exist yet.
    /// </summary>
    public void Save<T>(string channelId, T config, JsonTypeInfo<T> typeInfo)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            var path = FilePath(channelId);
            var json = JsonSerializer.Serialize(config, typeInfo);
            File.WriteAllText(path, json);
            _logger.LogInformation("Persisted config for channel '{ChannelId}' to '{Path}'.", channelId, path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist config for channel '{ChannelId}'.", channelId);
        }
    }

    /// <summary>Deletes the persisted config for a channel (revert to appsettings).</summary>
    public void Delete(string channelId)
    {
        var path = FilePath(channelId);
        if (!File.Exists(path))
            return;

        try
        {
            File.Delete(path);
            _logger.LogInformation("Deleted persisted config for channel '{ChannelId}'.", channelId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete persisted config for channel '{ChannelId}'.", channelId);
        }
    }

    private string FilePath(string channelId) =>
        Path.Combine(_dir, $"channel-{channelId}.json");
}
