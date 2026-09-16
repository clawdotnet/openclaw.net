using Microsoft.Extensions.DependencyInjection;

namespace OpenClaw.Core.Security;

/// <summary>
/// Bridges the static <see cref="SecretResolver"/> facade to a DI-registered
/// <see cref="ISecretResolver"/>. Set once during bootstrap via <see cref="Use"/>.
/// </summary>
public static class ResolverAccessor
{
    private static IServiceProvider? _serviceProvider;
    private static ISecretResolver? _resolver;
    private static readonly object _lock = new();

    public static ISecretResolver? Current
    {
        get
        {
            lock (_lock) return _resolver;
        }
    }

    public static void Use(IServiceProvider serviceProvider)
    {
        lock (_lock)
        {
            _serviceProvider = serviceProvider;
            _resolver = serviceProvider.GetService<ISecretResolver>();
        }
    }

    internal static void Reset()
    {
        lock (_lock)
        {
            _serviceProvider = null;
            _resolver = null;
        }
    }
}
