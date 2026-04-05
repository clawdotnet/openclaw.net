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
                : new ExecutionBackendProfileConfig { Type = ExecutionBackendType.Local })
        };

        foreach (var (name, profile) in config.Execution.Profiles)
        {
            if (!profile.Enabled)
                continue;

            if (profile.Type.Equals(ExecutionBackendType.Local, StringComparison.OrdinalIgnoreCase))
            {
                backends[name] = new LocalExecutionBackend(profile);
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

        if (_config.Execution.Enabled &&
            _config.Execution.Tools.TryGetValue(tool.Name, out var configuredRoute) &&
            !string.IsNullOrWhiteSpace(configuredRoute.Backend))
        {
            route = configuredRoute;
            if (_config.Execution.Profiles.TryGetValue(configuredRoute.Backend, out var profile))
                template = profile.Image;
            return true;
        }

        route = null;
        if (tool is not ISandboxCapableTool sandboxCapableTool)
            return false;

        sandboxMode = ToolSandboxPolicy.ResolveMode(_config, tool.Name, sandboxCapableTool.DefaultSandboxMode);
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
                         _backends.TryGetValue(fallbackBackend, out var fallback))
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
                    RequireWorkspace = request.RequireWorkspace
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
        if (_config.Execution.Enabled)
        {
            if (_config.Execution.Tools.TryGetValue("process", out var processRoute) &&
                !string.IsNullOrWhiteSpace(processRoute.Backend))
            {
                return new ExecutionRouteResolution(
                    processRoute.Backend,
                    processRoute.FallbackBackend,
                    ResolveTemplate(processRoute.Backend),
                    processRoute.RequireWorkspace,
                    ToolSandboxMode.None);
            }

            if (_config.Execution.Tools.TryGetValue("shell", out var shellRoute) &&
                !string.IsNullOrWhiteSpace(shellRoute.Backend))
            {
                return new ExecutionRouteResolution(
                    shellRoute.Backend,
                    shellRoute.FallbackBackend,
                    ResolveTemplate(shellRoute.Backend),
                    shellRoute.RequireWorkspace,
                    ToolSandboxMode.None);
            }
        }

        return new ExecutionRouteResolution(
            _config.Execution.DefaultBackend,
            null,
            ResolveTemplate(_config.Execution.DefaultBackend),
            RequiresWorkspace(_config.Execution.DefaultBackend),
            ToolSandboxMode.None);
    }

    internal bool TryGetProcessBackend(string backendName, out IExecutionProcessBackend? backend)
    {
        backend = null;
        if (!_backends.TryGetValue(backendName, out var executionBackend))
            return false;

        backend = executionBackend as IExecutionProcessBackend;
        return backend is not null;
    }

    private string? ResolveTemplate(string backendName)
        => _config.Execution.Profiles.TryGetValue(backendName, out var profile) ? profile.Image : null;
}
