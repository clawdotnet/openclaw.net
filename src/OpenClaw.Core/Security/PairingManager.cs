using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace OpenClaw.Core.Security;

/// <summary>
/// Manages Direct Message pairing flows (generating codes, approving senders).
/// Approved senders are persisted to disk to survive restarts.
/// </summary>
public sealed class PairingManager
{
    private static readonly TimeSpan CodeTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan FailedAttemptCooldown = TimeSpan.FromMinutes(5);
    private const int MaxFailedAttempts = 5;

    private readonly string _storageDir;
    private readonly string _approvedListPath;
    private readonly ILogger<PairingManager> _logger;
    private readonly ConcurrentDictionary<string, PendingPairing> _pendingCodes = new();
    private readonly ConcurrentDictionary<string, byte> _approvedSenders = new();

    private readonly record struct PendingPairing(
        string Code,
        DateTimeOffset ExpiresAt,
        int FailedAttempts,
        DateTimeOffset? LastFailedAt);

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
        var now = DateTimeOffset.UtcNow;

        CleanupExpiredPendingCodes(now);
        if (_pendingCodes.TryGetValue(key, out var existing) && existing.ExpiresAt > now)
            return existing.Code;

        var code = RandomNumberGenerator.GetInt32(100000, 1_000_000).ToString(CultureInfo.InvariantCulture);
        _pendingCodes[key] = new PendingPairing(code, now + CodeTtl, FailedAttempts: 0, LastFailedAt: null);
        return code;
    }

    /// <summary>
    /// Approves a sender based on a code they submitted out-of-band to the gateway API.
    /// </summary>
    public bool TryApprove(string channelId, string senderId, string providedCode)
        => TryApprove(channelId, senderId, providedCode, out _);

    public bool TryApprove(string channelId, string senderId, string providedCode, out string error)
    {
        error = "Invalid code or no pairing request found.";
        var key = $"{channelId}:{senderId}";

        var now = DateTimeOffset.UtcNow;
        if (!_pendingCodes.TryGetValue(key, out var pending))
        {
            error = "No pending pairing request found.";
            return false;
        }

        if (pending.ExpiresAt <= now)
        {
            _pendingCodes.TryRemove(key, out _);
            error = "Pairing code has expired. Request a new code.";
            return false;
        }

        if (pending.FailedAttempts >= MaxFailedAttempts &&
            pending.LastFailedAt is { } lastFailedAt &&
            now - lastFailedAt < FailedAttemptCooldown)
        {
            error = "Too many invalid attempts. Please wait and try again.";
            return false;
        }

        if (!FixedTimeCodeEquals(pending.Code, providedCode))
        {
            var updated = pending with
            {
                FailedAttempts = pending.FailedAttempts + 1,
                LastFailedAt = now
            };

            _pendingCodes[key] = updated;
            error = "Invalid pairing code.";
            return false;
        }

        if (_pendingCodes.TryRemove(key, out _))
        {
            _approvedSenders[key] = 1;
            PersistApprovedSenders();
            _logger.LogInformation("Sender {SenderKey} successfully paired and approved.", key);
            error = "";
            return true;
        }

        error = "Pairing code has already been used or expired.";
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

    private static bool FixedTimeCodeEquals(string expected, string provided)
    {
        if (string.IsNullOrWhiteSpace(provided))
            return false;

        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var providedBytes = Encoding.UTF8.GetBytes(provided.Trim());
        return CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes);
    }

    private void CleanupExpiredPendingCodes(DateTimeOffset now)
    {
        foreach (var kvp in _pendingCodes)
        {
            if (kvp.Value.ExpiresAt <= now)
                _pendingCodes.TryRemove(kvp.Key, out _);
        }
    }

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
