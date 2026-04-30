using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;

namespace OpenClaw.Companion.Services;

public sealed class ManagedGatewayService : IAsyncDisposable, IDisposable
{
    private readonly HttpClient _httpClient;
    private Process? _gatewayProcess;

    public ManagedGatewayService(
        string? baseDirectory = null,
        HttpClient? httpClient = null,
        string? configPath = null,
        string? workspacePath = null)
    {
        BaseDirectory = Path.GetFullPath(baseDirectory ?? AppContext.BaseDirectory);
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
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
        get
        {
            var builder = new UriBuilder(BaseUrl.TrimEnd('/') + "/ws")
            {
                Scheme = BaseUrl.StartsWith("https:", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws"
            };
            return builder.Uri.ToString();
        }
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
            args.Add(apiKey);
        }

        var result = await RunProcessToCompletionAsync(CliExecutable, args, TimeSpan.FromMinutes(2), ct);
        return result.ExitCode == 0
            ? ManagedGatewaySetupResult.Success(result.Output)
            : ManagedGatewaySetupResult.Fail(string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error);
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

        var startInfo = CreateStartInfo(GatewayExecutable, ["--config", ConfigPath]);
        startInfo.RedirectStandardOutput = false;
        startInfo.RedirectStandardError = false;

        _gatewayProcess = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            _gatewayProcess.Start();
        }
        catch (ObjectDisposedException ex)
        {
            return ManagedGatewayStartResult.Fail($"Gateway launch failed: {ex.Message}");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return ManagedGatewayStartResult.Fail($"Gateway launch failed: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
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
        catch (ObjectDisposedException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
        catch (InvalidOperationException)
        {
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
        catch
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
        await StopAsync(CancellationToken.None);
        _httpClient.Dispose();
    }

    public void Dispose()
    {
        StopAsync(CancellationToken.None).GetAwaiter().GetResult();
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

        try
        {
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token);
            return new ProcessRunResult(process.ExitCode, await stdout, await stderr);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            return new ProcessRunResult(124, "", "Command timed out.");
        }
        catch (Exception ex)
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
            var host = string.IsNullOrWhiteSpace(bind) || bind is "0.0.0.0" or "::"
                ? "127.0.0.1"
                : bind;
            return $"http://{host}:{port}";
        }
        catch
        {
            return null;
        }
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
        catch
        {
        }
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
