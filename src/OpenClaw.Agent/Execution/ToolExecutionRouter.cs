using Microsoft.Extensions.Logging;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;

namespace OpenClaw.Agent.Execution;

public sealed class ToolExecutionRouter
{
    public sealed record ExecutionRouteResolution(
        string BackendName,
        string? FallbackBackend,
        string? Template,
        bool RequireWorkspace,
        ToolSandboxMode SandboxMode);

    private readonly GatewayConfig _config;
    private readonly IReadOnlyDictionary<string, IExecutionBackend> _backends;
    private readonly ILogger? _logger;

    public ToolExecutionRouter(
        GatewayConfig config,
        IToolSandbox? toolSandbox,
        ILogger? logger = null)
    {
        _config = config;
        _logger = logger;

        var backends = new Dictionary<string, IExecutionBackend>(StringComparer.OrdinalIgnoreCase)
        {
            ["local"] = new LocalExecutionBackend(config.Execution.Profiles.TryGetValue("local", out var localProfile)
                ? localProfile
                : new ExecutionBackendProfileConfig { Type = ExecutionBackendType.Local },
                config.Tooling.WorkspaceRoot)
        };

        foreach (var (name, profile) in config.Execution.Profiles)
        {
            if (!profile.Enabled)
                continue;

            if (profile.Type.Equals(ExecutionBackendType.Local, StringComparison.OrdinalIgnoreCase))
            {
                backends[name] = new LocalExecutionBackend(profile, config.Tooling.WorkspaceRoot);
            }
            else if (profile.Type.Equals(ExecutionBackendType.Docker, StringComparison.OrdinalIgnoreCase))
            {
                backends[name] = new DockerExecutionBackend(name, profile);
            }
            else if (profile.Type.Equals(ExecutionBackendType.Ssh, StringComparison.OrdinalIgnoreCase))
            {
                backends[name] = new SshExecutionBackend(name, profile);
            }
            else if (profile.Type.Equals(ExecutionBackendType.OpenSandbox, StringComparison.OrdinalIgnoreCase) && toolSandbox is not null)
            {
                backends[name] = new OpenSandboxExecutionBackend(name, toolSandbox, profile.TimeoutSeconds);
            }
        }

        if (toolSandbox is not null)
            backends["opensandbox"] = new OpenSandboxExecutionBackend("opensandbox", toolSandbox);

        _backends = backends;
    }

    public bool TryResolveRoute(
        ITool tool,
        out ExecutionToolRouteConfig? route,
        out string? template,
        out bool legacySandboxRoute,
        out ToolSandboxMode sandboxMode)
    {
        legacySandboxRoute = false;
        template = null;
        sandboxMode = ToolSandboxMode.None;

        if (TryResolveConfiguredRoute(tool.Name, out var configuredRoute, out template))
        {
            route = configuredRoute;
            return true;
        }

        route = null;
        if (tool is not ISandboxCapableTool sandboxCapableTool)
            return false;

        var sandboxResolution = ToolSandboxPolicy.ResolveModeDetailed(_config, tool.Name, sandboxCapableTool.DefaultSandboxMode);
        sandboxMode = sandboxResolution.EffectiveMode;
        _logger?.LogInformation(
            "Sandbox mode resolved for tool {Tool}: provider={Provider} source={Source} default={DefaultMode} configured={ConfiguredMode} effective={EffectiveMode} reason={Reason}",
            tool.Name,
            sandboxResolution.Provider,
            sandboxResolution.ModeSource,
            sandboxResolution.DefaultMode,
            sandboxResolution.ConfiguredMode?.ToString() ?? "",
            sandboxResolution.EffectiveMode,
            sandboxResolution.Reason);
        if (sandboxMode == ToolSandboxMode.None)
            return false;

        if (!ToolSandboxPolicy.IsOpenSandboxProviderConfigured(_config))
            return true;

        template = ToolSandboxPolicy.ResolveTemplate(_config, tool.Name);
        route = new ExecutionToolRouteConfig
        {
            Backend = "opensandbox",
            FallbackBackend = null,
            RequireWorkspace = false
        };
        legacySandboxRoute = true;
        return true;
    }

    public async Task<ExecutionResult> ExecuteAsync(
        ExecutionRequest request,
        string? fallbackBackend,
        CancellationToken ct)
    {
        if (_backends.TryGetValue(request.BackendName, out var backend))
        {
            try
            {
                return await backend.ExecuteAsync(request, ct);
            }
            catch when (!string.IsNullOrWhiteSpace(fallbackBackend) &&
                         _backends.TryGetValue(fallbackBackend, out var fallback) &&
                         (request.AllowLocalFallback || !string.Equals(fallbackBackend, "local", StringComparison.OrdinalIgnoreCase)))
            {
                var fallbackResult = await fallback.ExecuteAsync(new ExecutionRequest
                {
                    ToolName = request.ToolName,
                    BackendName = fallbackBackend!,
                    Command = request.Command,
                    Arguments = request.Arguments,
                    LeaseKey = request.LeaseKey,
                    WorkingDirectory = request.WorkingDirectory,
                    Environment = new Dictionary<string, string>(request.Environment, StringComparer.Ordinal),
                    Template = request.Template,
                    TimeToLiveSeconds = request.TimeToLiveSeconds,
                    RequireWorkspace = request.RequireWorkspace,
                    AllowLocalFallback = request.AllowLocalFallback
                }, ct);
                return new ExecutionResult
                {
                    BackendName = fallbackResult.BackendName,
                    ExitCode = fallbackResult.ExitCode,
                    Stdout = fallbackResult.Stdout,
                    Stderr = fallbackResult.Stderr,
                    TimedOut = fallbackResult.TimedOut,
                    FallbackUsed = true,
                    DurationMs = fallbackResult.DurationMs
                };
            }
        }

        throw new InvalidOperationException($"Execution backend '{request.BackendName}' is not configured.");
    }

    public bool RequiresWorkspace(string backendName)
        => _config.Execution.Profiles.TryGetValue(backendName, out var profile)
           && (profile.Type.Equals(ExecutionBackendType.Docker, StringComparison.OrdinalIgnoreCase)
               || profile.Type.Equals(ExecutionBackendType.Ssh, StringComparison.OrdinalIgnoreCase));

    public ExecutionRouteResolution ResolveBackendForProcess()
    {
        if (TryResolveConfiguredRoute("process", out var processRoute, out var processTemplate))
        {
            return new ExecutionRouteResolution(
                processRoute!.Backend,
                processRoute.FallbackBackend,
                processTemplate,
                processRoute.RequireWorkspace,
                ToolSandboxMode.None);
        }

        if (TryResolveConfiguredRoute("shell", out var shellRoute, out var shellTemplate))
        {
            return new ExecutionRouteResolution(
                shellRoute!.Backend,
                shellRoute.FallbackBackend,
                shellTemplate,
                shellRoute.RequireWorkspace,
                ToolSandboxMode.None);
        }

        var sandboxMode = ToolSandboxPolicy.ResolveMode(_config, "process", ToolSandboxMode.Prefer);
        if (sandboxMode != ToolSandboxMode.None && ToolSandboxPolicy.IsOpenSandboxProviderConfigured(_config))
        {
            return new ExecutionRouteResolution(
                "opensandbox",
                null,
                ToolSandboxPolicy.ResolveTemplate(_config, "process"),
                RequireWorkspace: false,
                sandboxMode);
        }

        return new ExecutionRouteResolution(
            _config.Execution.DefaultBackend,
            null,
            ResolveTemplate(_config.Execution.DefaultBackend),
            RequiresWorkspace(_config.Execution.DefaultBackend),
            sandboxMode);
    }

    internal bool TryGetProcessBackend(string backendName, out IExecutionProcessBackend? backend)
    {
        backend = null;
        if (!_backends.TryGetValue(backendName, out var executionBackend))
            return false;

        backend = executionBackend as IExecutionProcessBackend;
        return backend is not null;
    }

    internal bool IsIsolatedProcessBackend(string backendName)
    {
        if (string.IsNullOrWhiteSpace(backendName) ||
            string.Equals(backendName, "opensandbox", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!_config.Execution.Profiles.TryGetValue(backendName, out var profile) || !profile.Enabled)
            return false;

        if (profile.Type.Equals(ExecutionBackendType.Local, StringComparison.OrdinalIgnoreCase))
            return false;

        return _backends.TryGetValue(backendName, out var executionBackend)
               && executionBackend is IExecutionProcessBackend;
    }

    private string? ResolveTemplate(string backendName)
        => _config.Execution.Profiles.TryGetValue(backendName, out var profile) ? profile.Image : null;

    private bool TryResolveConfiguredRoute(string toolName, out ExecutionToolRouteConfig? route, out string? template)
    {
        template = null;
        if (_config.Execution.Enabled &&
            _config.Execution.Tools.TryGetValue(toolName, out var configuredRoute) &&
            !string.IsNullOrWhiteSpace(configuredRoute.Backend))
        {
            route = configuredRoute;
            if (_config.Execution.Profiles.TryGetValue(configuredRoute.Backend, out var profile))
                template = profile.Image;
            return true;
        }

        route = null;
        return false;
    }
}
