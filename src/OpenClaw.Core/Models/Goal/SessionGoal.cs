using System.Text.Json.Serialization;

namespace OpenClaw.Core.Models.Goal;

/// <summary>
/// Represents a session-scoped Goal — a persistent objective that the agent
/// works toward across multiple turns. Auto-continuation fires when the model
/// stops before the goal is achieved.
/// </summary>
public sealed class SessionGoal
{
    /// <summary>The session this goal belongs to.</summary>
    public required string SessionId { get; init; }

    /// <summary>The objective text describing what to achieve.</summary>
    public required string Objective { get; init; }

    /// <summary>Maximum 4000 characters for the objective to limit prompt injection surface.</summary>
    public const int MaxObjectiveLength = 4000;

    /// <summary>Current goal status. Only Active is pursuable.</summary>
    public GoalStatus Status { get; set; } = GoalStatus.Active;

    /// <summary>When the goal was created (UTC).</summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>When the goal last changed status (UTC).</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Optional token budget. 0 means unlimited.</summary>
    public long TokenBudget { get; init; }

    /// <summary>Total completion tokens used since goal creation (from session baseline).</summary>
    public long TokensUsed { get; set; }

    /// <summary>Number of auto-continuation iterations in the current goal turn.</summary>
    public int ContinuationCount { get; set; }

    /// <summary>Maximum auto-continuations per goal turn before auto-pause.</summary>
    public const int MaxContinuationsPerTurn = 10;

    /// <summary>Normalized text hashes of recent turns for blocker detection.</summary>
    public List<string> RecentTurnHashes { get; set; } = new();

    /// <summary>
    /// Number of consecutive turns with the same blocker hash.
    /// Reset when the blocker text changes.
    /// </summary>
    public int ConsecutiveBlockerCount { get; set; }

    /// <summary>The last blocker hash (whitespace-normalized). Null if no blocker yet.</summary>
    public string? LastBlockerHash { get; set; }

    /// <summary>Optional note attached to the last status change (e.g., pause reason).</summary>
    public string? StatusNote { get; set; }

    /// <summary>Session token count when the goal was created (baseline for budget calc).</summary>
    public long TokensAtStart { get; init; }

    /// <summary>
    /// Returns true if the goal's token budget has been exceeded.
    /// Returns false if TokenBudget is 0 (unlimited).
    /// </summary>
    [JsonIgnore]
    public bool IsBudgetExceeded => TokenBudget > 0 && TokensUsed >= TokenBudget;

    /// <summary>
    /// Returns remaining tokens before budget is exceeded.
    /// Returns long.MaxValue if TokenBudget is 0 (unlimited).
    /// </summary>
    [JsonIgnore]
    public long RemainingBudget => TokenBudget > 0 ? Math.Max(0, TokenBudget - TokensUsed) : long.MaxValue;

    /// <summary>
    /// Normalizes text for blocker comparison: trims and collapses internal whitespace.
    /// </summary>
    public static string NormalizeForComparison(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var trimmed = text.Trim();
        var sb = new System.Text.StringBuilder(trimmed.Length);
        bool wasSpace = false;
        foreach (var ch in trimmed)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!wasSpace) sb.Append(' ');
                wasSpace = true;
            }
            else
            {
                sb.Append(ch);
                wasSpace = false;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Computes a deterministic hash of the normalized text for blocker comparison.
    /// Uses SHA-256 for low collision risk.
    /// </summary>
    public static string ComputeTurnHash(string normalizedText)
    {
        if (string.IsNullOrEmpty(normalizedText)) return string.Empty;
        var bytes = System.Text.Encoding.UTF8.GetBytes(normalizedText);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }
}
