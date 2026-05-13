using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Core.Http;
using OpenClaw.Core.Models;
using OpenClaw.Core.Setup;

namespace OpenClaw.Gateway;

internal sealed record LocalInferenceEndpoint(Uri BaseUrl, LocalModelPackageDefinition Package, string ModelPath);

internal sealed class LocalInferenceStatus
{
    public bool Running { get; init; }
    public int? ProcessId { get; init; }
    public Uri? BaseUrl { get; init; }
    public string? PackageId { get; init; }
    public string? ModelPath { get; init; }
    public int RestartAttempts { get; init; }
    public string? LastError { get; init; }
}

internal class LocalInferenceSupervisor : IAsyncDisposable, IDisposable
{
    private readonly LocalInferenceConfig _config;
    private readonly ILogger _logger;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private LocalInferenceEndpoint? _endpoint;
    private int _restartAttempts;
    private bool _disposed;
    private string? _lastError;

    public LocalInferenceSupervisor(LocalInferenceConfig config, ILogger? logger = null, HttpClient? httpClient = null)
    {
        _config = config;
        _logger = logger ?? NullLogger.Instance;
        _httpClient = httpClient ?? HttpClientFactory.Create(allowAutoRedirect: false);
    }

    public LocalInferenceStatus GetStatus()
    {
        var process = _process;
        var running = IsProcessRunning(process);
        return new LocalInferenceStatus
        {
            Running = running,
            ProcessId = running ? process!.Id : null,
            BaseUrl = _endpoint?.BaseUrl,
            PackageId = _endpoint?.Package.Id,
            ModelPath = _endpoint?.ModelPath,
            RestartAttempts = _restartAttempts,
            LastError = _lastError
        };
    }

    public virtual async Task<LocalInferenceEndpoint> EnsureRunningAsync(string modelId, CancellationToken ct)
    {
        if (!_config.Enabled)
            throw new InvalidOperationException("Embedded local inference is disabled. Set OpenClaw:LocalInference:Enabled=true.");

        if (!TryResolvePackage(modelId, out var package) || package is null)
            throw new InvalidOperationException($"No embedded local model package is registered for '{modelId}'.");

        await _gate.WaitAsync(ct);
        try
        {
            ThrowIfDisposed();
            if (_endpoint is not null &&
                string.Equals(_endpoint.Package.Id, package.Id, StringComparison.OrdinalIgnoreCase) &&
                IsProcessRunning(_process) &&
                await IsHealthyAsync(_endpoint.BaseUrl, ct))
            {
                return _endpoint;
            }

            var status = await VerifyPackageAsync(package, _config.ModelsRoot, ct);
            if (!status.Installed || !status.Verified || string.IsNullOrWhiteSpace(status.ModelPath))
            {
                throw new InvalidOperationException(
                    $"Embedded model package '{package.Id}' is not installed and verified. " +
                    $"Run: openclaw models install {package.Id} --accept-license --path <model.gguf>");
            }

            if (!_config.AutoStart)
            {
                var port = _config.Port > 0
                    ? _config.Port
                    : throw new InvalidOperationException("LocalInference.AutoStart=false requires a fixed LocalInference.Port.");
                var externalEndpoint = new LocalInferenceEndpoint(BuildBaseUrl(_config.Host, port), package, status.ModelPath);
                if (!await IsHealthyAsync(externalEndpoint.BaseUrl, ct))
                    throw new InvalidOperationException($"Embedded sidecar is not healthy at {externalEndpoint.BaseUrl}.");

                _endpoint = externalEndpoint;
                return externalEndpoint;
            }

            Exception? lastError = null;
            var attempts = Math.Max(0, _config.MaxRestartAttempts);
            for (var attempt = 0; attempt <= attempts; attempt++)
            {
                await StopProcessAsync();
                try
                {
                    _endpoint = await StartProcessAsync(package, status.ModelPath, ct);
                    return _endpoint;
                }
                catch (Exception ex) when (attempt < attempts)
                {
                    lastError = ex;
                    await Task.Delay(500, ct);
                }
            }

            throw lastError ?? new InvalidOperationException("Embedded local inference sidecar failed to start.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await StopProcessAsync();
            _endpoint = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LocalInferenceEndpoint> RestartAsync(string modelId, CancellationToken ct)
    {
        await StopAsync(ct);
        return await EnsureRunningAsync(modelId, ct);
    }

    protected virtual bool TryResolvePackage(string modelId, out LocalModelPackageDefinition? package)
        => LocalModelPackageCatalog.TryGet(modelId, out package);

    protected virtual Task<LocalModelPackageStatus> VerifyPackageAsync(
        LocalModelPackageDefinition package,
        string? modelsRoot,
        CancellationToken ct)
        => LocalModelCache.VerifyAsync(package, modelsRoot, ct);

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        await StopProcessAsync();
        _httpClient.Dispose();
        _gate.Dispose();
    }

    public void Dispose()
    {
        _disposed = true;
        StopProcessAsync().GetAwaiter().GetResult();
        _httpClient.Dispose();
        _gate.Dispose();
    }

    private async Task<LocalInferenceEndpoint> StartProcessAsync(
        LocalModelPackageDefinition package,
        string modelPath,
        CancellationToken ct)
    {
        var port = _config.Port > 0 ? _config.Port : ReserveLoopbackPort();
        var baseUrl = BuildBaseUrl(_config.Host, port);
        var runtimePath = ResolveRuntimePath(package);
        var startInfo = CreateStartInfo(runtimePath, package, modelPath, port);
        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };

        process.OutputDataReceived += (_, args) => AppendLog(package.Id, args.Data);
        process.ErrorDataReceived += (_, args) => AppendLog(package.Id, args.Data);
        process.Exited += (_, _) =>
        {
            _lastError = process.ExitCode == 0
                ? null
                : $"Local inference sidecar exited with code {process.ExitCode}.";
        };

        try
        {
            if (!process.Start())
                throw new InvalidOperationException("Process.Start returned false.");

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _process = process;
            await WaitForHealthyAsync(baseUrl, process, ct);
            _lastError = null;
            return new LocalInferenceEndpoint(baseUrl, package, modelPath);
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            _restartAttempts++;
            TryKill(process);
            if (_restartAttempts > Math.Max(0, _config.MaxRestartAttempts))
                throw;

            _logger.LogWarning(ex, "Embedded local inference sidecar failed to start for package {PackageId}.", package.Id);
            throw;
        }
    }

    private ProcessStartInfo CreateStartInfo(
        string runtimePath,
        LocalModelPackageDefinition package,
        string modelPath,
        int port)
    {
        var backend = package.Runtime.Backend?.ToLowerInvariant() ?? "llama.cpp";
        if (backend == "litert")
            return CreateLiteRtStartInfo(runtimePath, package, modelPath, port);

        var args = new List<string>
        {
            "-m", modelPath,
            "--host", _config.Host,
            "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-c", ResolveContextSize(package).ToString(System.Globalization.CultureInfo.InvariantCulture)
        };

        var threads = ResolveValue(_config.Threads, package.Runtime.Threads);
        if (!string.Equals(threads, "auto", StringComparison.OrdinalIgnoreCase))
            args.AddRange(["--threads", threads]);

        var gpuLayers = ResolveValue(_config.GpuLayers, package.Runtime.GpuLayers);
        if (!string.Equals(gpuLayers, "auto", StringComparison.OrdinalIgnoreCase))
            args.AddRange(["--n-gpu-layers", gpuLayers]);

        if (_config.EnableJinja || package.Runtime.EnableJinja || package.Capabilities.SupportsTools)
            args.Add("--jinja");

        var chatTemplateFile = ResolveRuntimeFilePath(
            _config.ChatTemplateFilePath,
            package,
            package.Runtime.ChatTemplateFileName,
            requireExists: false);
        if (!string.IsNullOrWhiteSpace(chatTemplateFile))
            args.AddRange(["--chat-template-file", chatTemplateFile]);
        else
        {
            var chatTemplate = ResolveValue(_config.ChatTemplate, package.Runtime.ChatTemplate);
            if (!string.IsNullOrWhiteSpace(chatTemplate))
                args.AddRange(["--chat-template", chatTemplate]);
        }

        var mmprojPath = ResolveRuntimeFilePath(
            _config.MultimodalProjectorPath,
            package,
            package.Runtime.MultimodalProjectorFileName,
            requireExists: true);
        if (!string.IsNullOrWhiteSpace(mmprojPath))
            args.AddRange(["--mmproj", mmprojPath]);

        if (!string.IsNullOrWhiteSpace(_config.MediaPath))
            args.AddRange(["--media-path", LocalModelCache.ResolveConfiguredPath(_config.MediaPath)]);

        var reasoningMode = ResolveValue(_config.ReasoningMode, package.Runtime.ReasoningMode);
        if (!string.IsNullOrWhiteSpace(reasoningMode))
            args.AddRange(["-rea", reasoningMode]);

        var reasoningBudget = _config.ReasoningBudget ?? package.Runtime.ReasoningBudget;
        if (reasoningBudget.HasValue)
            args.AddRange(["--reasoning-budget", reasoningBudget.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)]);

        var draftModelPath = ResolveRuntimeFilePath(
            _config.DraftModelPath,
            package,
            package.Runtime.DraftModelFileName,
            requireExists: true);
        if (!string.IsNullOrWhiteSpace(draftModelPath))
        {
            args.AddRange(["-md", draftModelPath]);

            var draftGpuLayers = ResolveValue(_config.DraftModelGpuLayers, "auto");
            if (!string.Equals(draftGpuLayers, "auto", StringComparison.OrdinalIgnoreCase))
                args.AddRange(["--n-gpu-layers-draft", draftGpuLayers]);
        }

        return new ProcessStartInfo
        {
            FileName = runtimePath,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        }.WithArguments(args);
    }

    private ProcessStartInfo CreateLiteRtStartInfo(
        string runtimePath,
        LocalModelPackageDefinition package,
        string modelPath,
        int port)
    {
        var args = new List<string>
        {
            "--model", modelPath,
            "--host", _config.Host,
            "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--context-size", ResolveContextSize(package).ToString(System.Globalization.CultureInfo.InvariantCulture)
        };

        var threads = ResolveValue(_config.Threads, package.Runtime.Threads);
        if (!string.Equals(threads, "auto", StringComparison.OrdinalIgnoreCase))
            args.AddRange(["--threads", threads]);

        if (!string.IsNullOrWhiteSpace(_config.LiteRtMediaPipeGraphPath))
            args.AddRange(["--experimental-mediapipe-graph", LocalModelCache.ResolveConfiguredPath(_config.LiteRtMediaPipeGraphPath)]);

        return new ProcessStartInfo
        {
            FileName = runtimePath,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        }.WithArguments(args);
    }

    private async Task WaitForHealthyAsync(Uri baseUrl, Process process, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _config.StartupTimeoutSeconds)));
        var token = timeoutCts.Token;
        Exception? last = null;
        while (!token.IsCancellationRequested)
        {
            if (!IsProcessRunning(process))
                throw new InvalidOperationException(_lastError ?? "Local inference sidecar exited before becoming healthy.");

            try
            {
                if (await IsHealthyAsync(baseUrl, token))
                    return;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
            {
                last = ex;
            }

            await Task.Delay(250, token);
        }

        throw new TimeoutException($"Local inference sidecar did not become healthy at {baseUrl}: {last?.Message}");
    }

    private async Task<bool> IsHealthyAsync(Uri baseUrl, CancellationToken ct)
    {
        if (await IsSuccessAsync(new Uri(baseUrl, "health"), ct))
            return true;

        return await IsSuccessAsync(new Uri(baseUrl, "v1/models"), ct);
    }

    private async Task<bool> IsSuccessAsync(Uri uri, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await _httpClient.SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return false;
        }
    }

    private async Task StopProcessAsync()
    {
        var process = _process;
        _process = null;
        if (process is null)
            return;

        try
        {
            if (!process.HasExited)
            {
                try
                {
                    _ = process.CloseMainWindow();
                    using var gracefulCts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    await process.WaitForExitAsync(gracefulCts.Token);
                }
                catch (OperationCanceledException ex)
                {
                    LogGracefulShutdownFailure(ex);
                }
                catch (InvalidOperationException ex)
                {
                    LogGracefulShutdownFailure(ex);
                }
                catch (System.ComponentModel.Win32Exception ex)
                {
                    LogGracefulShutdownFailure(ex);
                }

                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await process.WaitForExitAsync(cts.Token);
                }
            }
        }
        catch (InvalidOperationException)
        {
            TryKill(process);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            TryKill(process);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
        }
        catch (SystemException)
        {
            TryKill(process);
        }
        finally
        {
            process.Dispose();
        }
    }

    private string ResolveRuntimePath(LocalModelPackageDefinition package)
    {
        var backend = package.Runtime.Backend?.ToLowerInvariant() ?? "llama.cpp";
        if (backend == "litert")
        {
            if (!string.IsNullOrWhiteSpace(_config.LiteRtRuntimePath))
                return LocalModelCache.ResolveConfiguredPath(_config.LiteRtRuntimePath);

            throw new InvalidOperationException(
                "LiteRT local inference requires OpenClaw:LocalInference:LiteRtRuntimePath to point to an OpenClaw-compatible LiteRT adapter binary. " +
                "OpenClaw does not assume a generic litert-server executable.");
        }

        if (!string.IsNullOrWhiteSpace(_config.RuntimePath))
            return LocalModelCache.ResolveConfiguredPath(_config.RuntimePath);

        return OperatingSystem.IsWindows() ? "llama-server.exe" : "llama-server";
    }

    private int ResolveContextSize(LocalModelPackageDefinition package)
        => _config.ContextSize > 0 ? _config.ContextSize : package.Runtime.ContextSize;

    private static string ResolveValue(string? configured, string? packageDefault)
        => string.IsNullOrWhiteSpace(configured) ? packageDefault ?? "" : configured.Trim();

    private string? ResolveRuntimeFilePath(
        string? configuredPath,
        LocalModelPackageDefinition package,
        string? packageFileName,
        bool requireExists)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var path = LocalModelCache.ResolveConfiguredPath(configuredPath);
            if (!requireExists || File.Exists(path))
                return path;

            throw new InvalidOperationException($"Configured local inference runtime file was not found: {path}");
        }

        if (string.IsNullOrWhiteSpace(packageFileName))
            return null;

        var packagePath = Path.Combine(LocalModelCache.GetPackageDirectory(package, _config.ModelsRoot), packageFileName);
        return !requireExists || File.Exists(packagePath) ? packagePath : null;
    }

    private static Uri BuildBaseUrl(string host, int port)
    {
        if (!IsLoopbackHost(host))
            throw new InvalidOperationException("Embedded local inference host must be loopback.");

        var authorityHost = host.Contains(':', StringComparison.Ordinal) ? $"[{host}]" : host;
        return new Uri($"http://{authorityHost}:{port}/");
    }

    private static bool IsLoopbackHost(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            return true;

        return IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
    }

    private static int ReserveLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static bool IsProcessRunning(Process? process)
    {
        try
        {
            return process is not null && !process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private void AppendLog(string packageId, string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        try
        {
            var logsPath = string.IsNullOrWhiteSpace(_config.LogsPath)
                ? Path.Combine(Path.GetDirectoryName(LocalModelCache.ResolveModelsRoot(_config.ModelsRoot)) ?? ".", "logs")
                : LocalModelCache.ResolveConfiguredPath(_config.LogsPath);
            Directory.CreateDirectory(logsPath);
            File.AppendAllText(Path.Combine(logsPath, $"{packageId}.localinfer.log"), line + Environment.NewLine);
        }
        catch (DirectoryNotFoundException)
        {
            // Logging must not break local inference.
        }
        catch (PathTooLongException)
        {
            // Logging must not break local inference.
        }
        catch (IOException)
        {
            // Logging must not break local inference.
        }
        catch (UnauthorizedAccessException)
        {
            // Logging must not break local inference.
        }
        catch (NotSupportedException)
        {
            // Logging must not break local inference.
        }
        catch (ArgumentException)
        {
            // Logging must not break local inference.
        }
    }

    private void LogGracefulShutdownFailure(Exception ex)
        => _logger.LogDebug(ex, "Graceful local inference sidecar shutdown failed; falling back to force kill if needed.");

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(LocalInferenceSupervisor));
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
        catch (NotSupportedException)
        {
        }
    }
}

internal static class ProcessStartInfoExtensions
{
    public static ProcessStartInfo WithArguments(this ProcessStartInfo startInfo, IEnumerable<string> arguments)
    {
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        return startInfo;
    }
}
