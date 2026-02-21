using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace OpenClaw.Core.Security;

/// <summary>
/// Manages Direct Message pairing flows (generating codes, approving senders).
/// Approved senders are persisted to disk to survive restarts.
/// </summary>
public sealed class PairingManager
{
    private readonly string _storageDir;
    private readonly string _approvedListPath;
    private readonly ILogger<PairingManager> _logger;
    private readonly ConcurrentDictionary<string, string> _pendingCodes = new();
    private readonly ConcurrentDictionary<string, byte> _approvedSenders = new();

    public PairingManager(string baseStoragePath, ILogger<PairingManager> logger)
    {
        _storageDir = Path.Combine(baseStoragePath, "pairing");
        _approvedListPath = Path.Combine(_storageDir, "approved.json");
        _logger = logger;

        LoadApprovedSenders();
    }

    /// <summary>
    /// Checks if a sender on a specific channel is already approved.
    /// </summary>
    public bool IsApproved(string channelId, string senderId)
    {
        var key = $"{channelId}:{senderId}";
        return _approvedSenders.ContainsKey(key);
    }

    /// <summary>
    /// Generates and returns a 6-digit pairing code for the unapproved sender.
    /// </summary>
    public string GeneratePairingCode(string channelId, string senderId)
    {
        var key = $"{channelId}:{senderId}";
        var code = new Random().Next(100000, 999999).ToString();
        _pendingCodes[key] = code;
        return code;
    }

    /// <summary>
    /// Approves a sender based on a code they submitted out-of-band to the gateway API.
    /// </summary>
    public bool TryApprove(string channelId, string senderId, string providedCode)
    {
        var key = $"{channelId}:{senderId}";
        if (_pendingCodes.TryGetValue(key, out var expectedCode) && expectedCode == providedCode)
        {
            _pendingCodes.TryRemove(key, out _); // Use it once
            _approvedSenders[key] = 1;
            PersistApprovedSenders();
            _logger.LogInformation("Sender {SenderKey} successfully paired and approved.", key);
            return true;
        }
        return false;
    }

    public void ApproveAdmin(string channelId, string senderId)
    {
        var key = $"{channelId}:{senderId}";
        _approvedSenders[key] = 1;
        PersistApprovedSenders();
        _logger.LogInformation("Sender {SenderKey} manually approved by admin.", key);
    }

    public void Revoke(string channelId, string senderId)
    {
        var key = $"{channelId}:{senderId}";
        if (_approvedSenders.TryRemove(key, out _))
        {
            PersistApprovedSenders();
            _logger.LogInformation("Sender {SenderKey} revoked.", key);
        }
    }

    public IEnumerable<string> GetApprovedList() => _approvedSenders.Keys;

    private void LoadApprovedSenders()
    {
        try
        {
            if (File.Exists(_approvedListPath))
            {
                var json = File.ReadAllText(_approvedListPath);
                var saved = JsonSerializer.Deserialize(json, OpenClaw.Core.Models.CoreJsonContext.Default.ListString) ?? [];
                foreach (var s in saved)
                {
                    _approvedSenders[s] = 1;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load approved pairing list from {Path}", _approvedListPath);
        }
    }

    private void PersistApprovedSenders()
    {
        try
        {
            Directory.CreateDirectory(_storageDir);
            var list = _approvedSenders.Keys.ToList();
            var json = JsonSerializer.Serialize(list, OpenClaw.Core.Models.CoreJsonContext.Default.ListString);
            File.WriteAllText(_approvedListPath, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist approved pairing list to {Path}", _approvedListPath);
        }
    }
}
