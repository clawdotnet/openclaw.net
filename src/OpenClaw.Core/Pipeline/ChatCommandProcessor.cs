using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using OpenClaw.Core.Sessions;

namespace OpenClaw.Core.Pipeline;

public enum DynamicCommandRegistrationResult
{
    Registered,
    ReservedBuiltIn,
    Duplicate
}

public sealed class ChatCommandProcessor
{
    private static readonly FrozenSet<string> BuiltInCommands = new[]
    {
        "/status",
        "/new",
        "/reset",
        "/model",
        "/usage",
        "/think",
        "/compact",
        "/concise",
        "/verbose",
        "/help"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private readonly SessionManager _sessionManager;
    private readonly ProviderUsageTracker? _providerUsage;
    private readonly ConcurrentDictionary<string, Func<string, CancellationToken, Task<string>>> _dynamicCommands = new(StringComparer.OrdinalIgnoreCase);
    private Func<Session, CancellationToken, Task<int>>? _compactCallback;

    public ChatCommandProcessor(SessionManager sessionManager, ProviderUsageTracker? providerUsage = null)
    {
        _sessionManager = sessionManager;
        _providerUsage = providerUsage;
    }

    /// <summary>
    /// Sets the callback for LLM-powered history compaction (injected from gateway setup).
    /// </summary>
    public void SetCompactCallback(Func<Session, CancellationToken, Task<int>> callback)
        => _compactCallback = callback;

    /// <summary>
    /// Registers a dynamic command handler (e.g. from a plugin).
    /// </summary>
    public DynamicCommandRegistrationResult RegisterDynamic(string command, Func<string, CancellationToken, Task<string>> handler)
    {
        var key = command.StartsWith('/') ? command : "/" + command;
        if (BuiltInCommands.Contains(key))
            return DynamicCommandRegistrationResult.ReservedBuiltIn;

        return _dynamicCommands.TryAdd(key, handler)
            ? DynamicCommandRegistrationResult.Registered
            : DynamicCommandRegistrationResult.Duplicate;
    }

    /// <summary>
    /// Processes chat commands (starting with /).
    /// Returns true if a command was handled (and thus the pipeline should short-circuit the LLM).
    /// </summary>
    public async Task<(bool Handled, string? Response)> TryProcessCommandAsync(
        Session session, string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text) || !text.StartsWith('/'))
            return (false, null);

        var parts = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var command = parts[0].ToLowerInvariant();
        var args = parts.Length > 1 ? parts[1].Trim() : "";

        switch (command)
        {
            case "/status":
                var activeModel = session.ModelOverride ?? "default";
                var (statusCacheRead, statusCacheWrite) = GetCacheTotals(session);
                return (true, $"Session info:\n- Active Model: {activeModel}\n- Turn Count: {session.History.Count}\n- Token Usage: {session.TotalInputTokens} in / {session.TotalOutputTokens} out\n- Prompt Cache: {statusCacheRead} read / {statusCacheWrite} write");

            case "/new":
            case "/reset":
                session.History.Clear();
                session.TotalInputTokens = 0;
                session.TotalOutputTokens = 0;
                await _sessionManager.PersistAsync(session, ct);
                return (true, "Session history has been reset. Starting fresh!");

            case "/model":
                if (string.IsNullOrWhiteSpace(args))
                    return (true, $"Current model override: {session.ModelOverride ?? "none (using default)"}\nUsage: /model <model-name> or /model reset");

                if (args.Equals("reset", StringComparison.OrdinalIgnoreCase) || args.Equals("clear", StringComparison.OrdinalIgnoreCase))
                {
                    session.ModelOverride = null;
                    await _sessionManager.PersistAsync(session, ct);
                    return (true, "Model override cleared. Back to default.");
                }

                session.ModelOverride = args;
                await _sessionManager.PersistAsync(session, ct);
                return (true, $"Model override set to: {args}");

            case "/usage":
                var (usageCacheRead, usageCacheWrite) = GetCacheTotals(session);
                return (true, $"Total Token Usage in this session:\n- Input: {session.TotalInputTokens}\n- Output: {session.TotalOutputTokens}\n- Sum: {session.GetTotalTokens()}\n- Prompt Cache Read: {usageCacheRead}\n- Prompt Cache Write: {usageCacheWrite}");

            case "/think":
                if (string.IsNullOrWhiteSpace(args))
                    return (true, $"Current reasoning effort: {session.ReasoningEffort ?? "default"}\nUsage: /think off|low|medium|high");

                var level = args.ToLowerInvariant();
                if (level is "off" or "low" or "medium" or "high")
                {
                    session.ReasoningEffort = level == "off" ? null : level;
                    await _sessionManager.PersistAsync(session, ct);
                    return (true, level == "off"
                        ? "Extended thinking disabled."
                        : $"Reasoning effort set to: {level}");
                }
                return (true, "Invalid level. Use: /think off|low|medium|high");

            case "/compact":
                if (session.History.Count <= 2)
                    return (true, "Nothing to compact — session has 2 or fewer turns.");

                var turnsBefore = session.History.Count;
                if (_compactCallback is not null)
                {
                    var remainingTurns = await _compactCallback(session, ct);
                    await _sessionManager.PersistAsync(session, ct);
                    return (true, $"Compacted: {turnsBefore} turns → {remainingTurns} turns remaining.");
                }

                // Fallback: simple trim keeping last 10 turns
                var keepRecent = Math.Min(10, session.History.Count);
                var removeCount = session.History.Count - keepRecent;
                if (removeCount > 0)
                    session.History.RemoveRange(0, removeCount);
                await _sessionManager.PersistAsync(session, ct);
                return (true, $"Trimmed: {turnsBefore} turns → {session.History.Count} turns (kept last {keepRecent}).");

            case "/verbose":
                if (string.IsNullOrWhiteSpace(args))
                    return (true, $"Verbose mode: {(session.VerboseMode ? "on" : "off")}\nUsage: /verbose on|off");

                if (args.Equals("on", StringComparison.OrdinalIgnoreCase))
                {
                    session.VerboseMode = true;
                    await _sessionManager.PersistAsync(session, ct);
                    return (true, "Verbose mode enabled. Tool calls and token counts will be shown.");
                }
                if (args.Equals("off", StringComparison.OrdinalIgnoreCase))
                {
                    session.VerboseMode = false;
                    await _sessionManager.PersistAsync(session, ct);
                    return (true, "Verbose mode disabled.");
                }
                return (true, "Usage: /verbose on|off");

            case "/concise":
                if (string.IsNullOrWhiteSpace(args))
                {
                    var currentMode = session.ResponseMode switch
                    {
                        SessionResponseModes.ConciseOps => "on",
                        SessionResponseModes.Full => "off",
                        _ => "auto"
                    };
                    return (true, $"Concise mode: {currentMode}\nUsage: /concise on|off|auto");
                }

                if (args.Equals("on", StringComparison.OrdinalIgnoreCase))
                {
                    session.ResponseMode = SessionResponseModes.ConciseOps;
                    await _sessionManager.PersistAsync(session, ct);
                    return (true, "Concise operational mode enabled.");
                }
                if (args.Equals("off", StringComparison.OrdinalIgnoreCase))
                {
                    session.ResponseMode = SessionResponseModes.Full;
                    await _sessionManager.PersistAsync(session, ct);
                    return (true, "Concise operational mode disabled for this session.");
                }
                if (args.Equals("auto", StringComparison.OrdinalIgnoreCase))
                {
                    session.ResponseMode = SessionResponseModes.Default;
                    await _sessionManager.PersistAsync(session, ct);
                    return (true, "Concise mode reset to automatic behavior.");
                }
                return (true, "Usage: /concise on|off|auto");

            case "/help":
                return (true, "Available commands:\n/status - Show session details\n/new (or /reset) - Clear conversation history\n/model <name> - Override the LLM model for this session\n/model reset - Clear model override\n/usage - Show token counts\n/think <level> - Set reasoning effort (off/low/medium/high)\n/compact - Compact conversation history\n/concise on|off|auto - Control concise operational responses\n/verbose on|off - Toggle verbose output\n/help - Show this message");

            default:
                if (_dynamicCommands.TryGetValue(command, out var dynamicHandler))
                {
                    var dynamicResult = await dynamicHandler(args, ct);
                    return (true, dynamicResult);
                }

                // Not a recognized command — assume it might be normal user text that just starts with a slash
                return (false, null);
        }
    }

    private (long CacheReadTokens, long CacheWriteTokens) GetCacheTotals(Session session)
    {
        if (session.TotalCacheReadTokens > 0 || session.TotalCacheWriteTokens > 0)
            return (session.TotalCacheReadTokens, session.TotalCacheWriteTokens);

        return _providerUsage?.GetLatestSessionCacheTotals(session.Id) ?? (0, 0);
    }
}
