using OpenClaw.Core.Setup;

namespace OpenClaw.Gateway.Bootstrap;

internal sealed class LocalStartupStateStore
{
    public LocalStartupStateStore(string? path = null)
    {
        Path = string.IsNullOrWhiteSpace(path)
            ? GatewaySetupPaths.ResolveDefaultLocalStartupStatePath()
            : System.IO.Path.GetFullPath(GatewaySetupPaths.ExpandPath(path));
    }

    public string Path { get; }

    public LocalStartupState Load()
    {
        if (!AtomicJsonFileStore.TryLoad(Path, StartupJsonContext.Default.LocalStartupState, out LocalStartupState? state, out _))
            return new LocalStartupState();

        return state ?? new LocalStartupState();
    }

    public bool TrySave(LocalStartupState state, out string? error)
        => AtomicJsonFileStore.TryWriteAtomic(Path, state, StartupJsonContext.Default.LocalStartupState, out error);
}
