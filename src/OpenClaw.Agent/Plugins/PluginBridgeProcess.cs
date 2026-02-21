using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using OpenClaw.Core.Models;
using OpenClaw.Core.Plugins;

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

    public PluginBridgeProcess(string bridgeScriptPath)
    {
        _bridgeScriptPath = bridgeScriptPath;
    }

    /// <summary>
    /// Start the bridge process and initialize the plugin.
    /// Returns the list of tools the plugin registered.
    /// </summary>
    public async Task<List<PluginToolRegistration>> StartAsync(
        string entryPath,
        string pluginId,
        JsonElement? pluginConfig,
        CancellationToken ct)
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

        // Set working directory to the plugin's directory for proper module resolution
        psi.WorkingDirectory = Path.GetDirectoryName(entryPath) ?? ".";

        _process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start Node.js plugin bridge process.");

        // Start reading stdout for responses
        _readLoop = Task.Run(() => ReadLoopAsync(_process), CancellationToken.None);

        // Send init request
        var initParams = new Dictionary<string, object?>
        {
            ["entryPath"] = entryPath,
            ["pluginId"] = pluginId,
            ["config"] = pluginConfig
        };

        var response = await SendRequestAsync("init", initParams, ct);

        if (response.Error is not null)
            throw new InvalidOperationException($"Plugin init failed: {response.Error.Message}");

        // Parse tool registrations from result
        var tools = new List<PluginToolRegistration>();

        if (response.Result is { } result && result.TryGetProperty("tools", out var toolsArray))
        {
            foreach (var toolEl in toolsArray.EnumerateArray())
            {
                var name = toolEl.GetProperty("name").GetString();
                var desc = toolEl.TryGetProperty("description", out var descEl) ? descEl.GetString() : "";
                var optional = toolEl.TryGetProperty("optional", out var optEl) && optEl.GetBoolean();
                var parameters = toolEl.TryGetProperty("parameters", out var paramEl)
                    ? paramEl.Clone()
                    : JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement;

                if (!string.IsNullOrEmpty(name))
                {
                    tools.Add(new PluginToolRegistration
                    {
                        Name = name,
                        Description = desc ?? "",
                        Parameters = parameters,
                        Optional = optional
                    });
                }
            }
        }

        return tools;
    }

    /// <summary>
    /// Execute a tool via the bridge process.
    /// </summary>
    public async Task<string> ExecuteToolAsync(string toolName, string argumentsJson, CancellationToken ct)
    {
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

        // Complete any pending requests with cancellation
        foreach (var kvp in _pending)
        {
            kvp.Value.TrySetCanceled();
        }
        _pending.Clear();
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
                catch
                {
                    // Ignore malformed output
                }
            }
        }
        catch
        {
            // Process exited or stream closed
        }

        // Cancel all pending requests
        foreach (var kvp in _pending)
        {
            kvp.Value.TrySetCanceled();
        }
    }

    private static string? FindNodeExecutable()
    {
        // Check common locations
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
            catch
            {
                continue;
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
