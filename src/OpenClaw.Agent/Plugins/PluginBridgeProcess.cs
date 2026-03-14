using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Models;
using OpenClaw.Core.Plugins;

namespace OpenClaw.Agent.Plugins;

/// <summary>
/// Manages a Node.js child process that runs the plugin bridge script.
/// Process lifecycle and restart logic live here; request/response transport is delegated.
/// </summary>
public sealed class PluginBridgeProcess : IAsyncDisposable
{
    private Process? _process;
    private Task? _exitMonitor;
    private readonly string _bridgeScriptPath;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly BridgeTransportConfig _transportConfig;
    private IBridgeTransport? _transport;
    private BridgeTransportRuntimeConfig _runtimeTransport = new();
    private string? _entryPath;
    private string? _pluginId;
    private JsonElement? _pluginConfig;
    private volatile bool _disposed;
    private volatile bool _intentionalShutdown;
    private Action<BridgeNotification>? _notificationHandler;

    public int RestartCount { get; private set; }

    /// <summary>
    /// Sets a handler for unsolicited notifications from the plugin process.
    /// </summary>
    public void SetNotificationHandler(Action<BridgeNotification> handler)
    {
        _notificationHandler = handler;
        _transport?.SetNotificationHandler(handler);
    }

    public PluginBridgeProcess(string bridgeScriptPath, ILogger logger, BridgeTransportConfig? transportConfig = null)
    {
        _bridgeScriptPath = bridgeScriptPath;
        _logger = logger;
        _transportConfig = transportConfig ?? new BridgeTransportConfig();
    }

    /// <summary>
    /// Returns a best-effort memory snapshot for the current Node.js bridge process.
    /// </summary>
    public PluginBridgeMemorySnapshot? GetMemorySnapshot()
    {
        var process = _process;
        if (process is null)
            return null;

        try
        {
            if (process.HasExited)
                return null;

            process.Refresh();
            return new PluginBridgeMemorySnapshot
            {
                ProcessId = process.Id,
                WorkingSetBytes = process.WorkingSet64,
                PrivateMemoryBytes = process.PrivateMemorySize64
            };
        }
        catch
        {
            return null;
        }
    }

    public async Task<BridgeInitResult> StartAsync(
        string entryPath,
        string pluginId,
        JsonElement? pluginConfig,
        CancellationToken ct)
    {
        _entryPath = entryPath;
        _pluginId = pluginId;
        _pluginConfig = pluginConfig;
        _intentionalShutdown = false;

        var response = await InitializeProcessAsync(ct);

        if (response.Error is not null)
            throw new InvalidOperationException($"Plugin init failed: {response.Error.Message}");

        if (response.Result is null)
            return new BridgeInitResult();

        var init = JsonSerializer.Deserialize(response.Result.Value.GetRawText(), CoreJsonContext.Default.BridgeInitResult);
        return init ?? new BridgeInitResult();
    }

    public async Task<string> ExecuteToolAsync(string toolName, string argumentsJson, CancellationToken ct)
    {
        await EnsureProcessRunningAsync(ct);

        if (_process is null || _process.HasExited)
            return "Error: Plugin bridge process is not running.";

        using var argDoc = JsonDocument.Parse(argumentsJson);
        var execRequest = new BridgeExecuteRequest
        {
            Name = toolName,
            Params = argDoc.RootElement.Clone()
        };

        var response = await SendAndWaitAsync("execute", execRequest, CoreJsonContext.Default.BridgeExecuteRequest, ct);

        if (response.Error is not null)
            return $"Error: {response.Error.Message}";

        if (response.Result is { } result && result.TryGetProperty("content", out var contentArray))
        {
            var sb = new StringBuilder();
            foreach (var item in contentArray.EnumerateArray())
            {
                if (item.TryGetProperty("text", out var textEl))
                {
                    if (sb.Length > 0)
                        sb.Append('\n');
                    sb.Append(textEl.GetString());
                }
            }
            return sb.ToString();
        }

        return response.Result?.GetRawText() ?? "";
    }

    public Task SendRequestAsync(string method, JsonElement? parameters, CancellationToken ct)
        => SendAndWaitAsync(method, parameters, ct);

    public async Task SendRequestAsync<T>(string method, T parameters, JsonTypeInfo<T> typeInfo, CancellationToken ct)
        => await SendAndWaitAsync(method, parameters, typeInfo, ct);

    public async Task<BridgeResponse> SendAndWaitAsync(string method, JsonElement? parameters, CancellationToken ct)
    {
        await EnsureProcessRunningAsync(ct);

        if (_transport is null)
            throw new InvalidOperationException("Plugin bridge transport is not running.");

        return await _transport.SendAndWaitAsync(method, parameters, ct);
    }

    public async Task<BridgeResponse> SendAndWaitAsync<T>(string method, T parameters, JsonTypeInfo<T> typeInfo, CancellationToken ct)
        => await SendAndWaitAsync(method, Serialize(parameters, typeInfo), ct);

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        _intentionalShutdown = true;

        var process = _process;
        if (process is null || process.HasExited)
        {
            await DisposeTransportAsync();
            return;
        }

        try
        {
            await SendAndWaitAsync("shutdown", (JsonElement?)null, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch
        {
            // Best effort — timeout or process already exited.
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await process.WaitForExitAsync(cts.Token);
        }
        catch
        {
            try { process.Kill(entireProcessTree: true); } catch { }
        }

        await DisposeTransportAsync();
        CleanupProcess();
    }

    private async Task EnsureProcessRunningAsync(CancellationToken ct)
    {
        if (_process is not null && !_process.HasExited && _transport is not null)
            return;

        await RestartAsync(ct);
    }

    private async Task RestartAsync(CancellationToken ct)
    {
        if (_disposed)
            return;

        if (string.IsNullOrWhiteSpace(_entryPath) || string.IsNullOrWhiteSpace(_pluginId))
            return;

        await _lifecycleGate.WaitAsync(ct);
        try
        {
            if (_disposed)
                return;

            if (_process is not null && !_process.HasExited && _transport is not null)
                return;

            var delay = TimeSpan.FromSeconds(1);
            Exception? lastError = null;
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    await DisposeTransportAsync();
                    CleanupProcess();
                    _intentionalShutdown = false;
                    await InitializeProcessAsync(ct);
                    RestartCount++;
                    _logger.LogInformation("Plugin bridge for '{PluginId}' restarted on attempt {Attempt}.", _pluginId, attempt);
                    return;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    _logger.LogWarning(ex, "Failed to restart plugin bridge for '{PluginId}' on attempt {Attempt}.", _pluginId, attempt);
                    await DisposeTransportAsync();
                    CleanupProcess();
                    if (attempt < 3)
                        await Task.Delay(delay, ct);
                    delay = TimeSpan.FromSeconds(delay.TotalSeconds * 2);
                }
            }

            _logger.LogError(lastError, "Plugin bridge for '{PluginId}' could not be restarted.", _pluginId);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task<BridgeResponse> InitializeProcessAsync(CancellationToken ct)
    {
        var (transport, runtimeTransport) = BridgeTransportFactory.Create(_transportConfig, _pluginId!, _logger);
        if (_notificationHandler is not null)
            transport.SetNotificationHandler(_notificationHandler);

        await transport.PrepareAsync(ct);
        var process = StartProcess(_entryPath!, runtimeTransport);
        await transport.StartAsync(process, ct);

        _process = process;
        _transport = transport;
        _runtimeTransport = runtimeTransport;
        _exitMonitor = Task.Run(() => MonitorProcessAsync(process), CancellationToken.None);

        try
        {
            var initRequest = new BridgeInitRequest
            {
                EntryPath = _entryPath!,
                PluginId = _pluginId!,
                Config = _pluginConfig,
                Transport = runtimeTransport
            };

            var response = await transport.SendAndWaitAsync(
                "init",
                Serialize(initRequest, CoreJsonContext.Default.BridgeInitRequest),
                ct);

            if (transport is HybridBridgeTransport hybrid)
                hybrid.UseSocketTransport();

            return response;
        }
        catch
        {
            await transport.DisposeAsync();
            _transport = null;
            CleanupProcess();
            throw;
        }
    }

    private Process StartProcess(string entryPath, BridgeTransportRuntimeConfig transport)
    {
        var nodeExe = FindNodeExecutable()
            ?? throw new InvalidOperationException(
                "Node.js is required for OpenClaw plugin support but was not found. " +
                "Install Node.js 18+ and ensure 'node' is on your PATH.");

        var psi = new ProcessStartInfo
        {
            FileName = nodeExe,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add("--experimental-vm-modules");
        psi.ArgumentList.Add(_bridgeScriptPath);
        psi.WorkingDirectory = Path.GetDirectoryName(entryPath) ?? ".";
        psi.Environment["OPENCLAW_BRIDGE_TRANSPORT_MODE"] = transport.Mode;
        if (!string.IsNullOrWhiteSpace(transport.SocketPath))
            psi.Environment["OPENCLAW_BRIDGE_SOCKET_PATH"] = transport.SocketPath;

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start Node.js plugin bridge process.");

        process.EnableRaisingEvents = true;
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                _logger.LogInformation("[Node] {Output}", e.Data);
        };
        process.BeginErrorReadLine();
        return process;
    }

    private async Task MonitorProcessAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync();
        }
        catch
        {
            return;
        }

        await DisposeTransportAsync();

        if (_disposed || _intentionalShutdown)
            return;

        _logger.LogWarning("Plugin bridge process for '{PluginId}' exited unexpectedly. Restarting.", _pluginId ?? "unknown");
        _ = Task.Run(() => RestartAsync(CancellationToken.None));
    }

    private async Task DisposeTransportAsync()
    {
        if (_transport is null)
            return;

        try
        {
            await _transport.DisposeAsync();
        }
        catch
        {
            // Best effort during teardown / restart.
        }
        finally
        {
            _transport = null;
        }
    }

    private void CleanupProcess()
    {
        if (_process is null)
            return;

        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }
        catch
        {
        }

        try
        {
            _process.Dispose();
        }
        catch
        {
        }

        _process = null;
    }

    private static JsonElement Serialize<T>(T value, JsonTypeInfo<T> typeInfo)
        => JsonSerializer.SerializeToElement(value, typeInfo);

    private static string? FindNodeExecutable()
    {
        string[] candidates = OperatingSystem.IsWindows()
            ? ["node.exe"]
            : ["node"];

        foreach (var candidate in candidates)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = OperatingSystem.IsWindows() ? "where" : "which",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add(candidate);

                using var proc = Process.Start(psi);
                if (proc is null) continue;
                var output = proc.StandardOutput.ReadToEnd().Trim();
                proc.WaitForExit();

                if (proc.ExitCode == 0 && !string.IsNullOrEmpty(output))
                    return output.Split('\n', '\r')[0].Trim();
            }
            catch { }
        }

        string[] commonPaths = OperatingSystem.IsWindows()
            ? [
                @"C:\Program Files\nodejs\node.exe",
                @"C:\Program Files (x86)\nodejs\node.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @"AppData\Roaming\nvm\v* \node.exe")
              ]
            : [
                "/usr/local/bin/node",
                "/usr/bin/node",
                "/opt/homebrew/bin/node",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nvm/versions/node/v*/bin/node")
              ];

        foreach (var path in commonPaths)
        {
            if (path.Contains('*'))
            {
                var dir = Path.GetDirectoryName(path);
                if (dir is null) continue;

                var pattern = Path.GetFileName(path);
                var parent = Path.GetDirectoryName(dir);
                var subDirPattern = Path.GetFileName(dir);

                if (parent is not null && subDirPattern is not null && Directory.Exists(parent))
                {
                    foreach (var subDir in Directory.GetDirectories(parent, subDirPattern))
                    {
                        var fullPath = Path.Combine(subDir, pattern);
                        if (File.Exists(fullPath)) return fullPath;
                    }
                }
            }
            else if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }
}

public sealed class PluginBridgeMemorySnapshot
{
    public int ProcessId { get; init; }
    public long WorkingSetBytes { get; init; }
    public long PrivateMemoryBytes { get; init; }
}
