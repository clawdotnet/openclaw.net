using Microsoft.Extensions.Logging;
using OpenClaw.Core.Observability;
using OpenClaw.Core.Plugins;

namespace OpenClaw.Agent.Plugins;

internal static class BridgeTransportFactory
{
    private const int UnixSocketPathBudget = 96;

    public static (IBridgeTransport Transport, BridgeTransportRuntimeConfig RuntimeConfig) Create(
        BridgeTransportConfig config,
        string pluginId,
        ILogger logger,
        string? runtimeRoot = null,
        RuntimeMetrics? metrics = null)
    {
        var mode = NormalizeMode(config.Mode);
        var socketOptions = mode == "stdio" ? null : ResolveSocketOptions(config.SocketPath, pluginId, runtimeRoot);

        return mode switch
        {
            "stdio" => (new StdioBridgeTransport(logger), new BridgeTransportRuntimeConfig { Mode = mode }),
            "socket" => (
                new SocketBridgeTransport(
                    socketOptions!.SocketPath,
                    socketOptions.SocketDirectory,
                    socketOptions.OwnsSocketDirectory,
                    socketOptions.AuthToken,
                    logger,
                    metrics),
                CreateRuntimeConfig(mode, socketOptions)),
            "hybrid" => (
                new HybridBridgeTransport(
                    socketOptions!.SocketPath,
                    socketOptions.SocketDirectory,
                    socketOptions.OwnsSocketDirectory,
                    socketOptions.AuthToken,
                    logger,
                    metrics),
                CreateRuntimeConfig(mode, socketOptions)),
            _ => throw new InvalidOperationException(
                $"Unsupported plugin bridge transport mode '{config.Mode}'. Supported modes: stdio, socket, hybrid.")
        };
    }

    private static BridgeTransportRuntimeConfig CreateRuntimeConfig(string mode, SocketTransportOptions socketOptions)
        => new()
        {
            Mode = mode,
            SocketPath = socketOptions.SocketPath,
            SocketDirectory = socketOptions.SocketDirectory,
            SocketAuthToken = socketOptions.AuthToken,
            SecurityMode = "hardened_local_ipc"
        };

    private static string NormalizeMode(string? mode)
        => string.IsNullOrWhiteSpace(mode) ? "stdio" : mode.Trim().ToLowerInvariant();

    private static SocketTransportOptions ResolveSocketOptions(string? configuredPath, string pluginId, string? runtimeRoot)
    {
        if (OperatingSystem.IsWindows())
        {
            var pipePath = !string.IsNullOrWhiteSpace(configuredPath)
                ? NormalizePipePath(configuredPath)
                : $@"\\.\pipe\openclaw-{Sanitize(pluginId)}-{Guid.NewGuid():N}";
            return new SocketTransportOptions(pipePath, null, false, CreateAuthToken());
        }

        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var socketPath = Path.GetFullPath(configuredPath);
            return new SocketTransportOptions(
                socketPath,
                Path.GetDirectoryName(socketPath),
                OwnsSocketDirectory: false,
                CreateAuthToken());
        }

        var socketDirectory = CreateUnixSocketDirectory(pluginId, runtimeRoot);
        Directory.CreateDirectory(socketDirectory);
        return new SocketTransportOptions(
            Path.Combine(socketDirectory, "s"),
            socketDirectory,
            OwnsSocketDirectory: true,
            CreateAuthToken());
    }

    private static string CreateUnixSocketDirectory(string pluginId, string? runtimeRoot)
    {
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{pluginId}:{Guid.NewGuid():N}")))[..16].ToLowerInvariant();
        var parent = ResolveUnixSocketParent(runtimeRoot);
        var socketDirectory = Path.Combine(parent, hash);
        if (socketDirectory.Length > UnixSocketPathBudget)
        {
            var shortenedParent = ResolveShortUnixSocketParent();
            socketDirectory = Path.Combine(shortenedParent, hash);
        }

        return socketDirectory;
    }

    private static string ResolveUnixSocketParent(string? runtimeRoot)
    {
        if (!string.IsNullOrWhiteSpace(runtimeRoot))
            return Path.Combine(Path.GetFullPath(runtimeRoot), "pb");

        return ResolveShortUnixSocketParent();
    }

    private static string ResolveShortUnixSocketParent()
    {
        var tempRoot = "/tmp";
        var userComponent = Sanitize(Environment.UserName);
        if (string.IsNullOrWhiteSpace(userComponent))
            userComponent = "user";

        return Path.Combine(tempRoot, $".openclaw-{userComponent}", "pb");
    }

    private static string NormalizePipePath(string configuredPath)
    {
        const string prefix = @"\\.\pipe\";
        return configuredPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? configuredPath
            : prefix + configuredPath.Trim('\\', '/');
    }

    private static string CreateAuthToken()
        => Convert.ToHexString(Guid.NewGuid().ToByteArray()) + Convert.ToHexString(Guid.NewGuid().ToByteArray());

    private static string Sanitize(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var ch in value)
            builder.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '-');
        return builder.ToString().Trim('-');
    }

    private sealed record SocketTransportOptions(
        string SocketPath,
        string? SocketDirectory,
        bool OwnsSocketDirectory,
        string AuthToken);
}
