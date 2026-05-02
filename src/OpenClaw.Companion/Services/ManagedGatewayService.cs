using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using OpenClaw.Core.Setup;

namespace OpenClaw.Companion.Services;

public sealed class ManagedGatewayService : IAsyncDisposable, IDisposable
{
    private const string ProviderApiKeyEnvironmentVariable = "OPENCLAW_MODEL_PROVIDER_KEY";
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private Process? _gatewayProcess;
    private string? _providerApiKey;

    public ManagedGatewayService(
        string? baseDirectory = null,
        HttpClient? httpClient = null,
        string? configPath = null,
        string? workspacePath = null)
    {
        BaseDirectory = Path.GetFullPath(baseDirectory ?? AppContext.BaseDirectory);
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        _ownsHttpClient = httpClient is null;
        ConfigPath = configPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".openclaw",
            "config",
            "openclaw.settings.json");
        WorkspacePath = workspacePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".openclaw",
            "workspace");
        GatewayExecutable = ResolveGatewayExecutable(BaseDirectory);
        CliExecutable = ResolveCliExecutable(BaseDirectory);
    }

    public string BaseDirectory { get; }
    public string ConfigPath { get; }
    public string WorkspacePath { get; }
    public ManagedExecutable? GatewayExecutable { get; }
    public ManagedExecutable? CliExecutable { get; }

    public bool HasConfig => File.Exists(ConfigPath);
    public bool CanStartGateway => GatewayExecutable is not null;
    public bool CanRunSetup => CliExecutable is not null;

    public string BaseUrl => ReadBaseUrlFromConfig() ?? "http://127.0.0.1:18789";

    public string WebSocketUrl
    {
        get => BuildWebSocketUrl(BaseUrl);
    }

    public void SetProviderApiKey(string? apiKey)
    {
        _providerApiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
    }

    public async Task<ManagedGatewaySetupResult> RunSetupAsync(ManagedGatewaySetupRequest request, CancellationToken ct)
    {
        if (CliExecutable is null)
            return ManagedGatewaySetupResult.Fail("The bundled OpenClaw CLI was not found.");

        var provider = string.IsNullOrWhiteSpace(request.Provider) ? "openai" : request.Provider.Trim();
        var model = string.IsNullOrWhiteSpace(request.Model) ? "gpt-4o" : request.Model.Trim();
        var workspace = string.IsNullOrWhiteSpace(request.WorkspacePath) ? WorkspacePath : request.WorkspacePath.Trim();
        var config = string.IsNullOrWhiteSpace(request.ConfigPath) ? ConfigPath : request.ConfigPath.Trim();
        var apiKey = request.ApiKey?.Trim();

        if (!provider.Equals("ollama", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(apiKey))
            return ManagedGatewaySetupResult.Fail("A provider API key is required unless you choose Ollama.");

        var args = new List<string>
        {
            "setup",
            "--non-interactive",
            "--profile", "local",
            "--workspace", workspace,
            "--provider", provider,
            "--model", model,
            "--config", config
        };

        if (!string.IsNullOrWhiteSpace(request.ModelPresetId))
        {
            args.Add("--model-preset");
            args.Add(request.ModelPresetId.Trim());
        }

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            args.Add("--api-key");
            args.Add($"env:{ProviderApiKeyEnvironmentVariable}");
        }

        var environment = string.IsNullOrWhiteSpace(apiKey)
            ? null
            : new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ProviderApiKeyEnvironmentVariable] = apiKey
            };

        var result = await RunProcessToCompletionAsync(CliExecutable, args, environment, TimeSpan.FromMinutes(2), ct);
        if (result.ExitCode != 0)
            return ManagedGatewaySetupResult.Fail(string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error);

        SetProviderApiKey(apiKey);
        return ManagedGatewaySetupResult.Success(result.Output);
    }

    public async Task<ManagedGatewayStartResult> StartAsync(string? authToken, CancellationToken ct)
    {
        if (GatewayExecutable is null)
            return ManagedGatewayStartResult.Fail("The bundled OpenClaw gateway was not found.");
        if (!HasConfig)
            return ManagedGatewayStartResult.Fail("Local setup has not been completed yet.");
        if (await IsHealthyAsync(authToken, ct))
            return ManagedGatewayStartResult.Success("Gateway is already running.");

        if (_gatewayProcess is { HasExited: false })
            return await WaitForReadyAsync(authToken, "Gateway is starting.", ct);
        DisposeExitedGatewayProcess();

        var startInfo = CreateStartInfo(GatewayExecutable, ["--config", ConfigPath]);
        startInfo.RedirectStandardOutput = false;
        startInfo.RedirectStandardError = false;
        ApplyProviderApiKeyEnvironment(startInfo);

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _gatewayProcess = process;
        try
        {
            process.Start();
        }
        catch (ObjectDisposedException ex)
        {
            ClearFailedStartProcess(process);
            return ManagedGatewayStartResult.Fail($"Gateway launch failed: {ex.Message}");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            ClearFailedStartProcess(process);
            return ManagedGatewayStartResult.Fail($"Gateway launch failed: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            ClearFailedStartProcess(process);
            return ManagedGatewayStartResult.Fail($"Gateway launch failed: {ex.Message}");
        }

        return await WaitForReadyAsync(authToken, "Gateway started.", ct);
    }

    public async Task StopAsync(CancellationToken ct)
    {
        var process = _gatewayProcess;
        if (process is null)
            return;

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(ct);
            }
        }
        catch (ObjectDisposedException ex)
        {
            TraceIgnoredProcessException("stop", ex);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            TraceIgnoredProcessException("stop", ex);
        }
        catch (InvalidOperationException ex)
        {
            TraceIgnoredProcessException("stop", ex);
        }
        finally
        {
            process.Dispose();
            if (ReferenceEquals(_gatewayProcess, process))
                _gatewayProcess = null;
        }
    }

    public async Task<bool> IsHealthyAsync(string? authToken, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl.TrimEnd('/')}/health");
            if (!string.IsNullOrWhiteSpace(authToken))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authToken);
            using var response = await _httpClient.SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (UriFormatException)
        {
            return false;
        }
    }

    public string DescribeAvailability()
    {
        var gateway = GatewayExecutable is null ? "gateway missing" : $"gateway: {GatewayExecutable.DisplayPath}";
        var cli = CliExecutable is null ? "CLI missing" : $"CLI: {CliExecutable.DisplayPath}";
        var config = HasConfig ? $"config: {ConfigPath}" : $"config missing: {ConfigPath}";
        return $"{gateway}; {cli}; {config}";
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }

    public void Dispose()
    {
        StopForDispose();
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }

    internal static ManagedExecutable? ResolveGatewayExecutable(string baseDirectory)
        => ResolveExecutable(
            baseDirectory,
            "OpenClaw.Gateway",
            "src/OpenClaw.Gateway/OpenClaw.Gateway.csproj",
            [
                ["gateway"],
                ["..", "gateway"],
                ["Resources", "gateway"],
                ["..", "Resources", "gateway"],
                []
            ]);

    internal static ManagedExecutable? ResolveCliExecutable(string baseDirectory)
        => ResolveExecutable(
            baseDirectory,
            "openclaw",
            "src/OpenClaw.Cli/OpenClaw.Cli.csproj",
            [
                ["cli"],
                ["..", "cli"],
                ["Resources", "cli"],
                ["..", "Resources", "cli"],
                []
            ]);

    private async Task<ManagedGatewayStartResult> WaitForReadyAsync(string? authToken, string successMessage, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(45);
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (_gatewayProcess is { HasExited: true })
                return ManagedGatewayStartResult.Fail($"Gateway exited with code {_gatewayProcess.ExitCode}.");
            if (await IsHealthyAsync(authToken, ct))
                return ManagedGatewayStartResult.Success(successMessage);
            await Task.Delay(750, ct);
        }

        return ManagedGatewayStartResult.Fail("Gateway did not become ready before the startup timeout.");
    }

    private async Task<ProcessRunResult> RunProcessToCompletionAsync(
        ManagedExecutable executable,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string>? environment,
        TimeSpan timeout,
        CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        using var process = new Process
        {
            StartInfo = CreateStartInfo(executable, args),
            EnableRaisingEvents = true
        };
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        if (environment is not null)
        {
            foreach (var (key, value) in environment)
                process.StartInfo.Environment[key] = value;
        }

        try
        {
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token);
            return new ProcessRunResult(process.ExitCode, await stdout, await stderr);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TryKill(process);
            return new ProcessRunResult(124, "", "Command timed out.");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        catch (ObjectDisposedException ex)
        {
            return new ProcessRunResult(1, "", ex.Message);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return new ProcessRunResult(1, "", ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return new ProcessRunResult(1, "", ex.Message);
        }
        catch (IOException ex)
        {
            return new ProcessRunResult(1, "", ex.Message);
        }
    }

    private static ProcessStartInfo CreateStartInfo(ManagedExecutable executable, IReadOnlyList<string> args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable.FileName,
            WorkingDirectory = executable.WorkingDirectory,
            UseShellExecute = false
        };

        foreach (var prefix in executable.ArgumentPrefix)
            startInfo.ArgumentList.Add(prefix);
        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);

        return startInfo;
    }

    private void ApplyProviderApiKeyEnvironment(ProcessStartInfo startInfo)
    {
        if (!string.IsNullOrWhiteSpace(_providerApiKey))
            startInfo.Environment[ProviderApiKeyEnvironmentVariable] = _providerApiKey;
    }

    private string? ReadBaseUrlFromConfig()
    {
        if (!File.Exists(ConfigPath))
            return null;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(ConfigPath));
            if (!document.RootElement.TryGetProperty("OpenClaw", out var openClaw))
                return null;

            var bind = openClaw.TryGetProperty("BindAddress", out var bindProperty) && bindProperty.ValueKind == JsonValueKind.String
                ? bindProperty.GetString()
                : "127.0.0.1";
            var port = openClaw.TryGetProperty("Port", out var portProperty) && portProperty.TryGetInt32(out var parsedPort)
                ? parsedPort
                : 18789;
            return GatewaySetupArtifacts.BuildReachableBaseUrl(
                string.IsNullOrWhiteSpace(bind) ? "127.0.0.1" : bind,
                port);
        }
        catch (JsonException ex)
        {
            Trace.TraceWarning("Managed gateway config read ignored JSON error for '{0}': {1}", ConfigPath, ex.Message);
            return null;
        }
        catch (IOException ex)
        {
            Trace.TraceWarning("Managed gateway config read ignored IO error for '{0}': {1}", ConfigPath, ex.Message);
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            Trace.TraceWarning("Managed gateway config read ignored access error for '{0}': {1}", ConfigPath, ex.Message);
            return null;
        }
        catch (InvalidOperationException ex)
        {
            Trace.TraceWarning("Managed gateway config read ignored invalid state for '{0}': {1}", ConfigPath, ex.Message);
            return null;
        }
    }

    internal static string BuildWebSocketUrl(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
            return baseUrl;

        var scheme = uri.Scheme.ToLowerInvariant() switch
        {
            "https" => "wss",
            "http" => "ws",
            "wss" => "wss",
            "ws" => "ws",
            _ => "ws"
        };

        var normalizedPath = uri.AbsolutePath.TrimEnd('/');
        string path;
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            path = "/ws";
        }
        else if (normalizedPath.EndsWith("/ws", StringComparison.OrdinalIgnoreCase))
        {
            path = normalizedPath;
        }
        else
        {
            path = normalizedPath + "/ws";
        }

        var builder = new UriBuilder(uri)
        {
            Scheme = scheme,
            Path = path
        };
        return builder.Uri.ToString();
    }

    private static ManagedExecutable? ResolveExecutable(
        string baseDirectory,
        string binaryBaseName,
        string projectRelativePath,
        IReadOnlyList<string[]> binaryRelativeDirectories)
    {
        foreach (var relativeDirectory in binaryRelativeDirectories)
        {
            var directory = Path.GetFullPath(Path.Combine([baseDirectory, .. relativeDirectory]));
            var binary = FindBinary(directory, binaryBaseName);
            if (binary is not null)
                return new ManagedExecutable(binary, [], directory, binary);
        }

        var repoRoot = FindRepoRoot(baseDirectory, projectRelativePath);
        if (repoRoot is null)
            return null;

        var projectPath = Path.Combine(repoRoot, projectRelativePath);
        return new ManagedExecutable(
            "dotnet",
            ["run", "--project", projectPath, "-c", "Release", "--"],
            repoRoot,
            projectPath);
    }

    private static string? FindBinary(string directory, string binaryBaseName)
    {
        var plain = Path.Combine(directory, binaryBaseName);
        if (File.Exists(plain))
            return plain;

        var windows = Path.Combine(directory, binaryBaseName + ".exe");
        if (File.Exists(windows))
            return windows;

        return null;
    }

    private static string? FindRepoRoot(string startDirectory, string projectRelativePath)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(startDirectory));
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, projectRelativePath)))
                return directory.FullName;
            directory = directory.Parent;
        }

        return null;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (ObjectDisposedException ex)
        {
            TraceIgnoredProcessException("kill", ex);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            TraceIgnoredProcessException("kill", ex);
        }
        catch (InvalidOperationException ex)
        {
            TraceIgnoredProcessException("kill", ex);
        }
    }

    private void DisposeExitedGatewayProcess()
    {
        var process = _gatewayProcess;
        if (process is null)
            return;

        try
        {
            if (!process.HasExited)
                return;
        }
        catch (ObjectDisposedException ex)
        {
            TraceIgnoredProcessException("dispose exited process state check", ex);
        }
        catch (InvalidOperationException ex)
        {
            TraceIgnoredProcessException("dispose exited process state check", ex);
        }

        if (ReferenceEquals(_gatewayProcess, process))
            _gatewayProcess = null;
        process.Dispose();
    }

    private void ClearFailedStartProcess(Process process)
    {
        if (ReferenceEquals(_gatewayProcess, process))
            _gatewayProcess = null;
        process.Dispose();
    }

    private void StopForDispose()
    {
        var process = _gatewayProcess;
        _gatewayProcess = null;
        if (process is null)
            return;

        using (process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
            }
            catch (ObjectDisposedException ex)
            {
                TraceIgnoredProcessException("dispose stop", ex);
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                TraceIgnoredProcessException("dispose stop", ex);
            }
            catch (InvalidOperationException ex)
            {
                TraceIgnoredProcessException("dispose stop", ex);
            }
        }
    }

    private static void TraceIgnoredProcessException(string operation, Exception ex)
    {
        Trace.TraceWarning(
            "Managed gateway process cleanup ignored {0} during {1}: {2}",
            ex.GetType().Name,
            operation,
            ex.Message);
    }

    private sealed record ProcessRunResult(int ExitCode, string Output, string Error);
}

public sealed record ManagedExecutable(
    string FileName,
    IReadOnlyList<string> ArgumentPrefix,
    string WorkingDirectory,
    string DisplayPath);

public sealed record ManagedGatewaySetupRequest(
    string Provider,
    string Model,
    string? ApiKey,
    string? ModelPresetId,
    string? WorkspacePath,
    string? ConfigPath);

public sealed record ManagedGatewaySetupResult(bool IsSuccess, string Message)
{
    public static ManagedGatewaySetupResult Success(string message) => new(true, message);
    public static ManagedGatewaySetupResult Fail(string message) => new(false, message);
}

public sealed record ManagedGatewayStartResult(bool IsSuccess, string Message)
{
    public static ManagedGatewayStartResult Success(string message) => new(true, message);
    public static ManagedGatewayStartResult Fail(string message) => new(false, message);
}
