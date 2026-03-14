using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
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
        "/help"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private readonly SessionManager _sessionManager;
    private readonly ConcurrentDictionary<string, Func<string, CancellationToken, Task<string>>> _dynamicCommands = new(StringComparer.OrdinalIgnoreCase);

    public ChatCommandProcessor(SessionManager sessionManager)
    {
        _sessionManager = sessionManager;
    }

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
                return (true, $"Session info:\n- Active Model: {activeModel}\n- Turn Count: {session.History.Count}\n- Token Usage: {session.TotalInputTokens} in / {session.TotalOutputTokens} out");

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
                return (true, $"Total Token Usage in this session:\n- Input: {session.TotalInputTokens}\n- Output: {session.TotalOutputTokens}\n- Sum: {session.TotalInputTokens + session.TotalOutputTokens}");

            case "/help":
                return (true, "Available commands:\n/status - Show session details\n/new (or /reset) - Clear conversation history\n/model <name> - Override the LLM model for this session\n/model reset - Clear model override\n/usage - Show token counts\n/help - Show this message");

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
}
