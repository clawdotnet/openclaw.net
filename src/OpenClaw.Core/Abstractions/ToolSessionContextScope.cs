using OpenClaw.Core.Models;

namespace OpenClaw.Core.Abstractions;

public static class ToolSessionContextScope
{
    private static readonly AsyncLocal<Session?> CurrentValue = new();

    public static Session? Current => CurrentValue.Value;

    public static IDisposable Push(Session session)
    {
        var prior = CurrentValue.Value;
        CurrentValue.Value = session;
        return new RestoreScope(prior);
    }

    private sealed class RestoreScope(Session? prior) : IDisposable
    {
        public void Dispose() => CurrentValue.Value = prior;
    }
}