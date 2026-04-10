using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using OpenClaw.Core.Plugins;

namespace OpenClaw.Agent.Plugins;

/// <summary>
/// Launches the first-party WhatsApp worker over the standard bridge RPC contract.
/// The gateway-side adapter stack stays identical to plugin-backed bridge channels.
/// </summary>
public sealed class FirstPartyWhatsAppWorkerHost : IAsyncDisposable
{
    private const string WorkerPluginId = "openclaw.first_party.whatsapp_worker";
    private const string WorkerEntryPath = "builtin://openclaw/whatsapp-worker";

    private readonly PluginBridgeProcess _bridge;
    private readonly ILogger _logger;
    private readonly List<BridgedChannelAdapter> _channels = [];

    public IReadOnlyList<BridgedChannelAdapter> ChannelAdapters => _channels;

    public FirstPartyWhatsAppWorkerHost(
        string bridgeScriptPath,
        BridgeProcessLaunchSpec launchSpec,
        ILogger logger,
        BridgeTransportConfig? transport = null,
        string? runtimeRoot = null,
        RuntimeMetrics? metrics = null)
    {
        _logger = logger;
        _bridge = new PluginBridgeProcess(bridgeScriptPath, logger, transport, launchSpec, runtimeRoot, metrics);
    }

    public async Task<IReadOnlyList<BridgedChannelAdapter>> LoadAsync(
        WhatsAppFirstPartyWorkerConfig config,
        CancellationToken ct)
    {
        if (_channels.Count > 0)
            return _channels;

        var initResult = await _bridge.StartAsync(
            WorkerEntryPath,
            WorkerPluginId,
            JsonSerializer.SerializeToElement(config, CoreJsonContext.Default.WhatsAppFirstPartyWorkerConfig),
            ct);

        if (!initResult.Compatible)
        {
            var message = initResult.Diagnostics.Length > 0
                ? string.Join(" | ", initResult.Diagnostics.Select(static d => d.Message))
                : "WhatsApp worker reported incompatible startup diagnostics.";
            throw new InvalidOperationException(message);
        }

        foreach (var registration in initResult.Channels)
            _channels.Add(new BridgedChannelAdapter(_bridge, registration.Id, _logger));

        _bridge.SetNotificationHandler(notification =>
        {
            if (notification.Params is not { } payload)
                return;

            var channelId = payload.TryGetProperty("channelId", out var channelIdProp)
                ? channelIdProp.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(channelId))
                return;

            var target = _channels.FirstOrDefault(channel => string.Equals(channel.ChannelId, channelId, StringComparison.Ordinal));
            if (target is null)
                return;

            switch (notification.Notification)
            {
                case "channel_message":
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await target.HandleInboundAsync(payload, CancellationToken.None);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to handle inbound worker message for '{ChannelId}'", channelId);
                        }
                    });
                    break;
                case "channel_auth_event":
                    try
                    {
                        target.HandleAuthEvent(payload);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to handle worker auth event for '{ChannelId}'", channelId);
                    }
                    break;
            }
        });

        return _channels;
    }

    public static BridgeProcessLaunchSpec ResolveLaunchSpec(WhatsAppFirstPartyWorkerConfig config)
    {
        return config.Driver?.ToLowerInvariant() switch
        {
            "baileys" => ResolveBaileysLaunchSpec(config),
            "baileys_csharp" => ResolveBaileysLaunchSpec(config),
            "whatsmeow" => ResolveWhatsmeowLaunchSpec(config),
            "simulated" => ResolveDotNetWorkerLaunchSpec(config),
            _ => throw new InvalidOperationException(
                $"Unsupported WhatsApp first-party worker driver '{config.Driver}'. Expected one of: baileys, whatsmeow, simulated.")
        };
    }

    private static BridgeProcessLaunchSpec ResolveBaileysLaunchSpec(WhatsAppFirstPartyWorkerConfig config)
    {
        var nodeExe = RuntimeDiscovery.FindNodeExecutable()
            ?? throw new InvalidOperationException(
                "Node.js is required for the Baileys WhatsApp driver but was not found. " +
                "Install Node.js 18+ or use Driver=whatsmeow instead.");

        var entryPath = config.ExecutablePath;
        if (string.IsNullOrWhiteSpace(entryPath))
        {
            var baseDirectory = AppContext.BaseDirectory;

            // Colocated deployment
            var colocated = Path.Combine(baseDirectory, "whatsapp-baileys-worker", "src", "index.mjs");
            if (File.Exists(colocated))
            {
                entryPath = colocated;
            }
            else
            {
                // Development: resolve from repo root
                var repoPath = Path.GetFullPath(Path.Combine(
                    baseDirectory, "..", "..", "..", "..",
                    "src", "whatsapp-baileys-worker", "src", "index.mjs"));
                if (File.Exists(repoPath))
                    entryPath = repoPath;
            }
        }

        if (string.IsNullOrWhiteSpace(entryPath) || !File.Exists(entryPath))
        {
            throw new InvalidOperationException(
                "Baileys WhatsApp worker script not found. " +
                "Run 'scripts/setup-whatsapp.sh' or set Channels.WhatsApp.FirstPartyWorker.ExecutablePath.");
        }

        var workerDir = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(entryPath)));
        var nodeModules = Path.Combine(workerDir!, "node_modules");
        if (!Directory.Exists(nodeModules))
        {
            throw new InvalidOperationException(
                $"Baileys worker dependencies not installed at '{workerDir}'. " +
                "Run 'npm install' in that directory or use 'scripts/setup-whatsapp.sh'.");
        }

        return new BridgeProcessLaunchSpec
        {
            FileName = nodeExe,
            Arguments = [Path.GetFullPath(entryPath), "--stdio"],
            WorkingDirectory = config.WorkingDirectory ?? workerDir
        };
    }

    private static BridgeProcessLaunchSpec ResolveWhatsmeowLaunchSpec(WhatsAppFirstPartyWorkerConfig config)
    {
        var candidate = config.ExecutablePath;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            var baseDirectory = AppContext.BaseDirectory;
            var binaryName = OperatingSystem.IsWindows()
                ? "whatsapp-whatsmeow-worker.exe"
                : "whatsapp-whatsmeow-worker";

            // Colocated deployment
            var colocated = Path.Combine(baseDirectory, binaryName);
            if (File.Exists(colocated))
            {
                candidate = colocated;
            }
            else
            {
                // Development: resolve from repo root
                var repoPath = Path.GetFullPath(Path.Combine(
                    baseDirectory, "..", "..", "..", "..",
                    "src", "whatsapp-whatsmeow-worker", binaryName));
                if (File.Exists(repoPath))
                    candidate = repoPath;
            }
        }

        if (string.IsNullOrWhiteSpace(candidate) || !File.Exists(candidate))
        {
            throw new InvalidOperationException(
                "whatsmeow WhatsApp worker binary not found. " +
                "Run 'scripts/setup-whatsapp.sh' or set Channels.WhatsApp.FirstPartyWorker.ExecutablePath.");
        }

        var fullPath = Path.GetFullPath(candidate);
        return new BridgeProcessLaunchSpec
        {
            FileName = fullPath,
            Arguments = ["--stdio"],
            WorkingDirectory = config.WorkingDirectory ?? Path.GetDirectoryName(fullPath)
        };
    }

    private static BridgeProcessLaunchSpec ResolveDotNetWorkerLaunchSpec(WhatsAppFirstPartyWorkerConfig config)
    {
        var candidate = config.ExecutablePath;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            var baseDirectory = AppContext.BaseDirectory;
            var executableName = OperatingSystem.IsWindows()
                ? "OpenClaw.WhatsApp.BaileysWorker.exe"
                : "OpenClaw.WhatsApp.BaileysWorker";
            var executablePath = Path.Combine(baseDirectory, executableName);
            if (File.Exists(executablePath))
                candidate = executablePath;
            else
            {
                var dllPath = Path.Combine(baseDirectory, "OpenClaw.WhatsApp.BaileysWorker.dll");
                if (File.Exists(dllPath))
                    candidate = dllPath;
            }

            if (string.IsNullOrWhiteSpace(candidate))
            {
                foreach (var configuration in new[] { "Debug", "Release" })
                {
                    var repoDllPath = Path.GetFullPath(Path.Combine(
                        baseDirectory, "..", "..", "..", "..",
                        "src", "OpenClaw.WhatsApp.BaileysWorker",
                        "bin", configuration, "net10.0",
                        "OpenClaw.WhatsApp.BaileysWorker.dll"));
                    if (File.Exists(repoDllPath))
                    {
                        candidate = repoDllPath;
                        break;
                    }
                }
            }
        }

        if (string.IsNullOrWhiteSpace(candidate))
        {
            throw new InvalidOperationException(
                "First-party WhatsApp worker executable was not found. " +
                "Set Channels.WhatsApp.FirstPartyWorker.ExecutablePath or deploy the worker binary beside the gateway.");
        }

        var fullPath = Path.GetFullPath(candidate);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"WhatsApp worker executable was not found at '{fullPath}'.", fullPath);

        if (fullPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            return new BridgeProcessLaunchSpec
            {
                FileName = "dotnet",
                Arguments = [fullPath, "--stdio"],
                WorkingDirectory = config.WorkingDirectory ?? Path.GetDirectoryName(fullPath)
            };
        }

        return new BridgeProcessLaunchSpec
        {
            FileName = fullPath,
            Arguments = ["--stdio"],
            WorkingDirectory = config.WorkingDirectory ?? Path.GetDirectoryName(fullPath)
        };
    }

    public ValueTask DisposeAsync() => _bridge.DisposeAsync();
}
