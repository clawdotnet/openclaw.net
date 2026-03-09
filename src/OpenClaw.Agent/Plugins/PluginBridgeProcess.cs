using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using OpenClaw.Core.Models;
using OpenClaw.Core.Plugins;
using Microsoft.Extensions.Logging;

namespace OpenClaw.Agent.Plugins;

/// <summary>
/// Manages a Node.js child process that runs the plugin bridge script.
/// Communicates via newline-delimited JSON-RPC over stdin/stdout.
/// </summary>
public sealed class PluginBridgeProcess : IAsyncDisposable
{
    private Process? _process;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<BridgeResponse>> _pending = new();
    private int _nextId;
    private Task? _readLoop;
    private readonly string _bridgeScriptPath;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private string? _entryPath;
    private string? _pluginId;
    private JsonElement? _pluginConfig;
    private volatile bool _disposed;
    private volatile bool _intentionalShutdown;

    public PluginBridgeProcess(string bridgeScriptPath, ILogger logger)
    {
        _bridgeScriptPath = bridgeScriptPath;
        _logger = logger;
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

    /// <summary>
    /// Start the bridge process and initialize the plugin.
    /// Returns the list of tools the plugin registered.
    /// </summary>
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

    /// <summary>
    /// Execute a tool via the bridge process.
    /// </summary>
    public async Task<string> ExecuteToolAsync(string toolName, string argumentsJson, CancellationToken ct)
    {
        await EnsureProcessRunningAsync(ct);

        if (_process is null || _process.HasExited)
            return "Error: Plugin bridge process is not running.";

        using var argDoc = JsonDocument.Parse(argumentsJson);
        var execParams = new Dictionary<string, object?>
        {
            ["name"] = toolName,
            ["params"] = argDoc.RootElement.Clone()
        };

        var response = await SendRequestAsync("execute", execParams, ct);

        if (response.Error is not null)
            return $"Error: {response.Error.Message}";

        // Extract text from content array
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

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        _intentionalShutdown = true;

        if (_process is null || _process.HasExited)
            return;

        try
        {
            // Send shutdown command and wait for acknowledgment
            var shutdownParams = new Dictionary<string, object?>();
            await SendRequestAsync("shutdown", shutdownParams, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch
        {
            // Best effort — timeout or process already exited
        }

        // Wait briefly for graceful exit
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _process.WaitForExitAsync(cts.Token);
        }
        catch
        {
            // Force kill
            try { _process.Kill(entireProcessTree: true); } catch { /* best effort */ }
        }

        _process.Dispose();
        _process = null;
        CancelPendingRequests();
    }

    private async Task<BridgeResponse> SendRequestAsync(
        string method,
        Dictionary<string, object?> parameters,
        CancellationToken ct)
    {
        if (_process?.StandardInput is null)
            throw new InvalidOperationException("Plugin bridge process is not running.");

        var id = Interlocked.Increment(ref _nextId).ToString();
        var tcs = new TaskCompletionSource<BridgeResponse>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
        _pending[id] = tcs;

        try
        {
            // Build params as a JsonElement manually (AOT-safe)
            var paramsElement = BuildParamsElement(parameters);

            var request = new BridgeRequest
            {
                Method = method,
                Id = id,
                Params = paramsElement
            };
            var requestJson = JsonSerializer.Serialize(request, CoreJsonContext.Default.BridgeRequest);

            await _process.StandardInput.WriteLineAsync(requestJson.AsMemory(), ct);
            await _process.StandardInput.FlushAsync(ct);

            // Timeout after 60 seconds
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(60));

            return await tcs.Task.WaitAsync(timeoutCts.Token);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task ReadLoopAsync(Process process)
    {
        try
        {
            while (!process.HasExited)
            {
                var line = await process.StandardOutput.ReadLineAsync();
                if (line is null)
                    break;

                try
                {
                    var response = JsonSerializer.Deserialize(line, CoreJsonContext.Default.BridgeResponse);
                    if (response?.Id is not null && _pending.TryRemove(response.Id, out var tcs))
                    {
                        tcs.TrySetResult(response);
                    }
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Plugin bridge emitted malformed JSON: {Line}", Truncate(line, 200));
                }
                catch
                {
                    _logger.LogWarning("Plugin bridge emitted unreadable output: {Line}", Truncate(line, 200));
                }
            }
        }
        catch
        {
            // Process exited or stream closed
        }

        CancelPendingRequests();

        if (_disposed || _intentionalShutdown)
            return;

        _logger.LogWarning("Plugin bridge process for '{PluginId}' exited unexpectedly. Restarting.", _pluginId ?? "unknown");
        _ = Task.Run(() => RestartAsync(CancellationToken.None));
    }

    private async Task EnsureProcessRunningAsync(CancellationToken ct)
    {
        if (_process is not null && !_process.HasExited)
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

            if (_process is not null && !_process.HasExited)
                return;

            var delay = TimeSpan.FromSeconds(1);
            Exception? lastError = null;
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    CleanupProcess();
                    _intentionalShutdown = false;
                    await InitializeProcessAsync(ct);
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
        StartProcess(_entryPath!);

        var initParams = new Dictionary<string, object?>
        {
            ["entryPath"] = _entryPath,
            ["pluginId"] = _pluginId,
            ["config"] = _pluginConfig
        };

        return await SendRequestAsync("init", initParams, ct);
    }

    private void StartProcess(string entryPath)
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

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start Node.js plugin bridge process.");

        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                _logger.LogInformation("[Node] {Output}", e.Data);
        };
        process.BeginErrorReadLine();

        _process = process;
        _readLoop = Task.Run(() => ReadLoopAsync(process), CancellationToken.None);
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

    private void CancelPendingRequests()
    {
        foreach (var kvp in _pending)
            kvp.Value.TrySetCanceled();

        _pending.Clear();
    }

    private static string Truncate(string value, int maxChars)
        => value.Length <= maxChars ? value : value[..maxChars] + "...";

    private static string? FindNodeExecutable()
    {
        // 1. Check PATH via 'which' or 'where'
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

        // 2. Check common installation paths
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
                // Simple glob for NVM/n
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

    /// <summary>
    /// Builds a JsonElement from a Dictionary, handling string and JsonElement values.
    /// AOT-safe — no reflection-based serialization.
    /// </summary>
    private static JsonElement BuildParamsElement(Dictionary<string, object?> parameters)
    {
        using var ms = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            foreach (var (key, value) in parameters)
            {
                writer.WritePropertyName(key);
                switch (value)
                {
                    case null:
                        writer.WriteNullValue();
                        break;
                    case string s:
                        writer.WriteStringValue(s);
                        break;
                    case JsonElement el:
                        el.WriteTo(writer);
                        break;
                    case bool b:
                        writer.WriteBooleanValue(b);
                        break;
                    default:
                        writer.WriteStringValue(value.ToString());
                        break;
                }
            }
            writer.WriteEndObject();
        }
        ms.Position = 0;
        using var doc = JsonDocument.Parse(ms);
        return doc.RootElement.Clone();
    }
}

public sealed class PluginBridgeMemorySnapshot
{
    public int ProcessId { get; init; }
    public long WorkingSetBytes { get; init; }
    public long PrivateMemoryBytes { get; init; }
}
