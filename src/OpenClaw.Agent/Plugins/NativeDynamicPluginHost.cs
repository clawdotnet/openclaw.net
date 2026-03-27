using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Pipeline;
using OpenClaw.Core.Plugins;
using OpenClaw.PluginKit;

namespace OpenClaw.Agent.Plugins;

/// <summary>
/// Loads in-process .NET plugins behind an explicit JIT-only runtime mode boundary.
/// </summary>
public sealed class NativeDynamicPluginHost : IAsyncDisposable
{
    private const string ManifestFileName = "openclaw.native-plugin.json";

    private readonly NativeDynamicPluginsConfig _config;
    private readonly GatewayRuntimeState _runtimeState;
    private readonly ILogger _logger;
    private readonly HashSet<string> _blockedPluginIds;
    private readonly List<ITool> _tools = [];
    private readonly List<IChannelAdapter> _channelAdapters = [];
    private readonly List<(string PluginId, string ChannelId, IChannelAdapter Adapter)> _channelRegistrations = [];
    private readonly List<IToolHook> _toolHooks = [];
    private readonly List<(string PluginId, string Name, string Description, Func<string, CancellationToken, Task<string>> Handler)> _commands = [];
    private readonly List<(string ProviderId, string[] Models, IChatClient Client)> _providerRegistrations = [];
    private readonly List<(string PluginId, string ProviderId, string[] Models, IChatClient Client)> _providerRegistrationsDetailed = [];
    private readonly List<string> _skillRoots = [];
    private readonly List<PluginLoadReport> _reports = [];
    private readonly List<LoadedNativePlugin> _loadedPlugins = [];

    public NativeDynamicPluginHost(
        NativeDynamicPluginsConfig config,
        GatewayRuntimeState runtimeState,
        ILogger logger,
        IReadOnlyCollection<string>? blockedPluginIds = null)
    {
        _config = config;
        _runtimeState = runtimeState;
        _logger = logger;
        _blockedPluginIds = blockedPluginIds is { Count: > 0 }
            ? new HashSet<string>(blockedPluginIds, StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
    }

    public IReadOnlyList<ITool> Tools => _tools;
    public IReadOnlyList<IChannelAdapter> ChannelAdapters => _channelAdapters;
    public IReadOnlyList<(string PluginId, string ChannelId, IChannelAdapter Adapter)> ChannelRegistrations => _channelRegistrations;
    public IReadOnlyList<IToolHook> ToolHooks => _toolHooks;
    public IReadOnlyList<(string ProviderId, string[] Models, IChatClient Client)> ProviderRegistrations => _providerRegistrations;
    public IReadOnlyList<(string PluginId, string Name, string Description, Func<string, CancellationToken, Task<string>> Handler)> CommandRegistrations => _commands;
    public IReadOnlyList<(string PluginId, string ProviderId, string[] Models, IChatClient Client)> ProviderRegistrationsDetailed => _providerRegistrationsDetailed;
    public IReadOnlyList<string> SkillRoots => _skillRoots;
    public IReadOnlyList<PluginLoadReport> Reports => _reports;

    public async Task<IReadOnlyList<ITool>> LoadAsync(string? workspacePath, CancellationToken ct)
    {
        if (!_config.Enabled)
        {
            _logger.LogInformation("Dynamic native plugin system is disabled");
            return [];
        }

        _reports.Clear();
        _tools.Clear();
        _channelAdapters.Clear();
        _channelRegistrations.Clear();
        _toolHooks.Clear();
        _commands.Clear();
        _providerRegistrations.Clear();
        _providerRegistrationsDetailed.Clear();
        _skillRoots.Clear();

        var discovery = DiscoverWithDiagnostics(workspacePath);
        _reports.AddRange(discovery.Reports);

        var enabled = Filter(discovery.Plugins);
        if (_runtimeState.EffectiveMode == GatewayRuntimeMode.Aot && enabled.Count > 0)
        {
            foreach (var plugin in enabled)
            {
                var requestedCapabilities = DetermineRequestedCapabilities(plugin.Manifest.Capabilities, ResolveSkillDirectories(plugin));
                var blockedCapabilities = PluginCapabilityPolicy.GetBlockedCapabilities(
                    _runtimeState.EffectiveMode,
                    requestedCapabilities,
                    PluginCapabilityPolicy.ExecutionHostKind.NativeDynamic);
                var message =
                    $"Dynamic native plugin '{plugin.Manifest.Id}' requires JIT runtime mode for capabilities: {string.Join(", ", blockedCapabilities)}.";

                _reports.Add(new PluginLoadReport
                {
                    PluginId = plugin.Manifest.Id,
                    SourcePath = plugin.RootPath,
                    EntryPath = plugin.AssemblyPath,
                    Origin = "native_dynamic",
                    EffectiveRuntimeMode = _runtimeState.EffectiveModeName,
                    RequestedCapabilities = requestedCapabilities,
                    Loaded = false,
                    BlockedByRuntimeMode = true,
                    BlockedReason = message,
                    Error = message,
                    Diagnostics =
                    [
                        new PluginCompatibilityDiagnostic
                        {
                            Severity = "error",
                            Code = "jit_mode_required",
                            Message = message,
                            Surface = "runtime_mode",
                            Path = plugin.Manifest.Id
                        }
                    ]
                });
            }

            throw new InvalidOperationException(
                "Dynamic native plugins are enabled, but the effective runtime mode is AOT. " +
                $"Blocked plugin(s): {string.Join(", ", enabled.Select(p => p.Manifest.Id))}.");
        }

        foreach (var plugin in enabled)
        {
            ct.ThrowIfCancellationRequested();
            if (_blockedPluginIds.Contains(plugin.Manifest.Id))
            {
                var message = $"Dynamic native plugin '{plugin.Manifest.Id}' is disabled or quarantined by operator state.";
                _reports.Add(new PluginLoadReport
                {
                    PluginId = plugin.Manifest.Id,
                    SourcePath = plugin.RootPath,
                    EntryPath = plugin.AssemblyPath,
                    Origin = "native_dynamic",
                    EffectiveRuntimeMode = _runtimeState.EffectiveModeName,
                    Loaded = false,
                    BlockedReason = message,
                    Error = message,
                    Diagnostics =
                    [
                        new PluginCompatibilityDiagnostic
                        {
                            Severity = "warning",
                            Code = "operator_blocked",
                            Message = message,
                            Surface = "operator_state",
                            Path = plugin.Manifest.Id
                        }
                    ]
                });
                continue;
            }

            await LoadPluginAsync(plugin, ct);
        }

        return _tools;
    }

    public void RegisterCommandsWith(ChatCommandProcessor processor)
    {
        foreach (var (_, name, _, handler) in _commands)
            processor.RegisterDynamic(name, handler);
    }

    public bool TryGetRestartCount(string pluginId, out int restartCount)
    {
        restartCount = 0;
        return _loadedPlugins.Any(item => string.Equals(item.PluginId, pluginId, StringComparison.Ordinal));
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode", Justification = "Dynamic native plugins are JIT-only and blocked in AOT mode.")]
    [UnconditionalSuppressMessage("Trimming", "IL2072", Justification = "Dynamic native plugins are JIT-only and blocked in AOT mode.")]
    private async Task LoadPluginAsync(DiscoveredNativeDynamicPlugin plugin, CancellationToken ct)
    {
        var manifest = plugin.Manifest;
        var requestedCapabilities = DetermineRequestedCapabilities(manifest.Capabilities, ResolveSkillDirectories(plugin));
        var diagnostics = new List<PluginCompatibilityDiagnostic>();

        try
        {
            var loadContext = new NativeDynamicPluginLoadContext(plugin.AssemblyPath);
            var assembly = loadContext.LoadFromAssemblyPath(plugin.AssemblyPath);
            var type = assembly.GetType(manifest.TypeName, throwOnError: false);
            if (type is null)
                throw new InvalidOperationException($"Type '{manifest.TypeName}' was not found in assembly '{plugin.AssemblyPath}'.");
            if (!typeof(INativeDynamicPlugin).IsAssignableFrom(type))
                throw new InvalidOperationException($"Type '{manifest.TypeName}' does not implement {nameof(INativeDynamicPlugin)}.");

            var instance = Activator.CreateInstance(type) as INativeDynamicPlugin
                ?? throw new InvalidOperationException($"Failed to instantiate plugin type '{manifest.TypeName}'.");

            var registrationContext = new RegistrationContext(manifest.Id, GetPluginConfig(manifest.Id), _logger);
            instance.Register(registrationContext);

            foreach (var service in registrationContext.Services)
                await service.StartAsync(ct);

            foreach (var tool in registrationContext.Tools)
            {
                if (_tools.Any(existing => string.Equals(existing.Name, tool.Name, StringComparison.Ordinal)))
                {
                    diagnostics.Add(new PluginCompatibilityDiagnostic
                    {
                        Severity = "warning",
                        Code = "duplicate_tool_name",
                        Message = $"Tool '{tool.Name}' from dynamic native plugin '{manifest.Id}' was skipped because that tool name is already registered.",
                        Surface = "registerTool",
                        Path = tool.Name
                    });
                    continue;
                }

                _tools.Add(tool);
            }

            _channelAdapters.AddRange(registrationContext.Channels);
            _channelRegistrations.AddRange(registrationContext.Channels.Select(channel => (manifest.Id, channel.ChannelId, channel)));
            _toolHooks.AddRange(registrationContext.Hooks);
            _commands.AddRange(registrationContext.Commands.Select(cmd => (manifest.Id, cmd.Name, cmd.Description, cmd.Handler)));
            _providerRegistrations.AddRange(registrationContext.Providers);
            _providerRegistrationsDetailed.AddRange(registrationContext.Providers.Select(provider => (manifest.Id, provider.ProviderId, provider.Models, provider.Client)));

            var skillDirs = ResolveSkillDirectories(plugin).ToArray();
            foreach (var skillDir in skillDirs)
            {
                if (!_skillRoots.Contains(skillDir, StringComparer.Ordinal))
                    _skillRoots.Add(skillDir);
            }

            _loadedPlugins.Add(new LoadedNativePlugin(manifest.Id, loadContext, registrationContext.Services.ToArray()));

            _reports.Add(new PluginLoadReport
            {
                PluginId = manifest.Id,
                SourcePath = plugin.RootPath,
                EntryPath = plugin.AssemblyPath,
                Origin = "native_dynamic",
                EffectiveRuntimeMode = _runtimeState.EffectiveModeName,
                RequestedCapabilities = PluginCapabilityPolicy.Normalize(requestedCapabilities.Concat(registrationContext.Capabilities)),
                Loaded = true,
                ToolCount = registrationContext.Tools.Count,
                ChannelCount = registrationContext.Channels.Count,
                CommandCount = registrationContext.Commands.Count,
                EventSubscriptionCount = registrationContext.Hooks.Count,
                ProviderCount = registrationContext.Providers.Count,
                SkillDirectories = skillDirs,
                Diagnostics = [.. diagnostics]
            });
        }
        catch (Exception ex)
        {
            _reports.Add(new PluginLoadReport
            {
                PluginId = manifest.Id,
                SourcePath = plugin.RootPath,
                EntryPath = plugin.AssemblyPath,
                Origin = "native_dynamic",
                EffectiveRuntimeMode = _runtimeState.EffectiveModeName,
                RequestedCapabilities = requestedCapabilities,
                Loaded = false,
                Error = ex.Message
            });
            _logger.LogError(ex, "Failed to load dynamic native plugin '{PluginId}'", manifest.Id);
        }
    }

    private JsonElement? GetPluginConfig(string pluginId)
    {
        if (!_config.Entries.TryGetValue(pluginId, out var entry))
            return null;

        return NormalizePluginConfig(entry.Config);
    }

    private static JsonElement? NormalizePluginConfig(JsonElement? config)
    {
        if (!config.HasValue)
            return null;

        return config.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? null
            : config;
    }

    private IReadOnlyList<string> ResolveSkillDirectories(DiscoveredNativeDynamicPlugin plugin)
    {
        var result = new List<string>();
        foreach (var relativePath in plugin.Manifest.Skills ?? [])
        {
            if (string.IsNullOrWhiteSpace(relativePath))
                continue;

            var resolved = Path.GetFullPath(Path.Combine(plugin.RootPath, relativePath));
            if (!Directory.Exists(resolved))
            {
                _logger.LogWarning(
                    "Dynamic native plugin '{PluginId}' declared missing skill directory {Path}",
                    plugin.Manifest.Id,
                    resolved);
                continue;
            }

            result.Add(resolved);
        }

        return result;
    }

    private string[] DetermineRequestedCapabilities(IEnumerable<string> manifestCapabilities, IReadOnlyList<string> skillDirs)
    {
        var capabilities = new List<string>(manifestCapabilities)
        {
            PluginCapabilityPolicy.NativeDynamic
        };

        if (skillDirs.Count > 0)
            capabilities.Add(PluginCapabilityPolicy.Skills);

        return PluginCapabilityPolicy.Normalize(capabilities);
    }

    private List<DiscoveredNativeDynamicPlugin> Filter(List<DiscoveredNativeDynamicPlugin> discovered)
    {
        var result = new List<DiscoveredNativeDynamicPlugin>();
        foreach (var plugin in discovered)
        {
            var id = plugin.Manifest.Id;
            if (_config.Deny.Contains(id, StringComparer.Ordinal))
                continue;
            if (_config.Allow.Length > 0 && !_config.Allow.Contains(id, StringComparer.Ordinal))
                continue;
            if (_config.Entries.TryGetValue(id, out var entry) && !entry.Enabled)
                continue;
            result.Add(plugin);
        }

        return result;
    }

    private NativeDynamicDiscoveryResult DiscoverWithDiagnostics(string? workspacePath)
    {
        var result = new NativeDynamicDiscoveryResult();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var configPath in _config.Load.Paths)
        {
            var expanded = Environment.ExpandEnvironmentVariables(configPath);
            if (expanded.StartsWith('~'))
            {
                expanded = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    expanded[1..].TrimStart('/').TrimStart('\\'));
            }

            if (File.Exists(expanded) && Path.GetFileName(expanded).Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase))
                TryAddFromManifestFile(expanded, seen, result);
            else if (Directory.Exists(expanded))
                ScanDirectory(expanded, seen, result);
        }

        if (!string.IsNullOrWhiteSpace(workspacePath))
        {
            var wsDir = Path.Combine(workspacePath, ".openclaw", "native-plugins");
            if (Directory.Exists(wsDir))
                ScanDirectory(wsDir, seen, result);
        }

        var globalDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".openclaw",
            "native-plugins");
        if (Directory.Exists(globalDir))
            ScanDirectory(globalDir, seen, result);

        return result;
    }

    private void ScanDirectory(string root, HashSet<string> seen, NativeDynamicDiscoveryResult result)
    {
        var manifestPath = Path.Combine(root, ManifestFileName);
        if (File.Exists(manifestPath))
        {
            TryAddFromManifestFile(manifestPath, seen, result);
            return;
        }

        foreach (var subDir in Directory.EnumerateDirectories(root))
            ScanDirectory(subDir, seen, result);
    }

    private void TryAddFromManifestFile(string manifestPath, HashSet<string> seen, NativeDynamicDiscoveryResult result)
    {
        var rootPath = Path.GetDirectoryName(manifestPath);
        if (string.IsNullOrWhiteSpace(rootPath))
            return;

        NativeDynamicPluginManifest? manifest;
        try
        {
            using var stream = File.OpenRead(manifestPath);
            manifest = JsonSerializer.Deserialize(stream, CoreJsonContext.Default.NativeDynamicPluginManifest);
        }
        catch
        {
            result.Reports.Add(new PluginLoadReport
            {
                PluginId = Path.GetFileName(rootPath),
                SourcePath = Path.GetFullPath(rootPath),
                EntryPath = null,
                Origin = "native_dynamic",
                EffectiveRuntimeMode = _runtimeState.EffectiveModeName,
                Loaded = false,
                Diagnostics =
                [
                    new PluginCompatibilityDiagnostic
                    {
                        Code = "invalid_manifest",
                        Message = $"Failed to parse dynamic native plugin manifest '{manifestPath}'.",
                        Path = Path.GetFullPath(manifestPath)
                    }
                ]
            });
            return;
        }

        if (manifest is null)
            return;

        if (!seen.Add(manifest.Id))
        {
            result.Reports.Add(new PluginLoadReport
            {
                PluginId = manifest.Id,
                SourcePath = Path.GetFullPath(rootPath),
                EntryPath = null,
                Origin = "native_dynamic",
                EffectiveRuntimeMode = _runtimeState.EffectiveModeName,
                Loaded = false,
                Diagnostics =
                [
                    new PluginCompatibilityDiagnostic
                    {
                        Code = "duplicate_plugin_id",
                        Message = $"Dynamic native plugin id '{manifest.Id}' was discovered more than once. Later entries are skipped.",
                        Path = Path.GetFullPath(manifestPath)
                    }
                ]
            });
            return;
        }

        if (!PluginDiscovery.TryResolveContainedPath(rootPath, manifest.AssemblyPath, out var assemblyPath))
        {
            result.Reports.Add(new PluginLoadReport
            {
                PluginId = manifest.Id,
                SourcePath = Path.GetFullPath(rootPath),
                EntryPath = Path.GetFullPath(Path.Combine(rootPath, manifest.AssemblyPath)),
                Origin = "native_dynamic",
                EffectiveRuntimeMode = _runtimeState.EffectiveModeName,
                Loaded = false,
                Diagnostics =
                [
                    new PluginCompatibilityDiagnostic
                    {
                        Code = "assembly_outside_root",
                        Message = $"Dynamic native plugin assembly path for '{manifest.Id}' resolves outside the plugin root.",
                        Path = Path.GetFullPath(rootPath)
                    }
                ]
            });
            return;
        }

        if (!File.Exists(assemblyPath))
        {
            result.Reports.Add(new PluginLoadReport
            {
                PluginId = manifest.Id,
                SourcePath = Path.GetFullPath(rootPath),
                EntryPath = assemblyPath,
                Origin = "native_dynamic",
                EffectiveRuntimeMode = _runtimeState.EffectiveModeName,
                Loaded = false,
                Diagnostics =
                [
                    new PluginCompatibilityDiagnostic
                    {
                        Code = "assembly_not_found",
                        Message = $"Dynamic native plugin assembly was not found for '{manifest.Id}'.",
                        Path = assemblyPath
                    }
                ]
            });
            return;
        }

        result.Plugins.Add(new DiscoveredNativeDynamicPlugin
        {
            Manifest = manifest,
            RootPath = Path.GetFullPath(rootPath),
            ManifestPath = Path.GetFullPath(manifestPath),
            AssemblyPath = assemblyPath
        });
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var loadedPlugin in _loadedPlugins)
        {
            foreach (var service in loadedPlugin.Services)
            {
                try
                {
                    await service.StopAsync(CancellationToken.None);
                }
                catch
                {
                    // Best effort during shutdown.
                }
            }

            loadedPlugin.LoadContext.Unload();
        }

        foreach (var channel in _channelAdapters)
        {
            try
            {
                await channel.DisposeAsync();
            }
            catch
            {
                // Best effort during shutdown.
            }
        }

        _loadedPlugins.Clear();
        _tools.Clear();
        _channelAdapters.Clear();
        _channelRegistrations.Clear();
        _toolHooks.Clear();
        _commands.Clear();
        _providerRegistrations.Clear();
        _providerRegistrationsDetailed.Clear();
        _skillRoots.Clear();
        _reports.Clear();
    }

    private sealed class NativeDynamicDiscoveryResult
    {
        public List<DiscoveredNativeDynamicPlugin> Plugins { get; } = [];
        public List<PluginLoadReport> Reports { get; } = [];
    }

    private sealed class RegistrationContext(string pluginId, JsonElement? config, ILogger logger) : INativeDynamicPluginContext
    {
        public string PluginId { get; } = pluginId;
        public JsonElement? Config { get; } = config;
        public ILogger Logger { get; } = logger;
        public List<ITool> Tools { get; } = [];
        public List<IChannelAdapter> Channels { get; } = [];
        public List<IToolHook> Hooks { get; } = [];
        public List<INativeDynamicPluginService> Services { get; } = [];
        public List<(string Name, string Description, Func<string, CancellationToken, Task<string>> Handler)> Commands { get; } = [];
        public List<(string ProviderId, string[] Models, IChatClient Client)> Providers { get; } = [];
        public List<string> Capabilities { get; } = [];

        public void RegisterTool(ITool tool)
        {
            Tools.Add(tool);
            Capabilities.Add(PluginCapabilityPolicy.Tools);
        }

        public void RegisterChannel(IChannelAdapter adapter)
        {
            Channels.Add(adapter);
            Capabilities.Add(PluginCapabilityPolicy.Channels);
        }

        public void RegisterCommand(string name, string description, Func<string, CancellationToken, Task<string>> handler)
        {
            Commands.Add((name, description, handler));
            Capabilities.Add(PluginCapabilityPolicy.Commands);
        }

        public void RegisterProvider(string providerId, string[] models, IChatClient client)
        {
            Providers.Add((providerId, models, client));
            Capabilities.Add(PluginCapabilityPolicy.Providers);
        }

        public void RegisterHook(IToolHook hook)
        {
            Hooks.Add(hook);
            Capabilities.Add(PluginCapabilityPolicy.Hooks);
        }

        public void RegisterService(INativeDynamicPluginService service)
        {
            Services.Add(service);
            Capabilities.Add(PluginCapabilityPolicy.Services);
        }
    }

    private sealed record LoadedNativePlugin(
        string PluginId,
        NativeDynamicPluginLoadContext LoadContext,
        INativeDynamicPluginService[] Services);

    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode", Justification = "Dynamic native plugins are JIT-only and blocked in AOT mode.")]
    private sealed class NativeDynamicPluginLoadContext(string mainAssemblyPath) : AssemblyLoadContext(isCollectible: true)
    {
        private readonly AssemblyDependencyResolver _resolver = new(mainAssemblyPath);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var name = assemblyName.Name;
            if (string.IsNullOrWhiteSpace(name))
                return null;

            if (name.StartsWith("System.", StringComparison.Ordinal) ||
                name.Equals("System", StringComparison.Ordinal) ||
                name.StartsWith("Microsoft.", StringComparison.Ordinal) ||
                name.Equals("netstandard", StringComparison.Ordinal) ||
                name.Equals("OpenClaw.Core", StringComparison.Ordinal) ||
                name.Equals("OpenClaw.PluginKit", StringComparison.Ordinal))
            {
                return AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(assembly => AssemblyName.ReferenceMatchesDefinition(assembly.GetName(), assemblyName));
            }

            var path = _resolver.ResolveAssemblyToPath(assemblyName);
            return path is null ? null : LoadFromAssemblyPath(path);
        }
    }
}
