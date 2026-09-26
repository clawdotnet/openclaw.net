using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NacosLiveAcceptance;

internal static class AcceptanceRunner
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(90);

    public static async Task<int> RunAsync(AcceptanceOptions options, CancellationToken cancellationToken)
    {
        var outputDirectory = Path.GetFullPath(options.OutputDirectory);
        if (Directory.Exists(outputDirectory) || File.Exists(outputDirectory))
        {
            throw new ArgumentException($"Output path already exists: {outputDirectory}");
        }

        Directory.CreateDirectory(outputDirectory);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                outputDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var workDirectory = Path.Combine(Path.GetTempPath(), "openclaw-nacos-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDirectory);
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        NacosDeployment? deployment = null;
        OwnedProcess? fixture = null;
        OwnedProcess? router = null;
        IAsyncDisposable? registration = null;

        try
        {
            Console.WriteLine("Starting isolated Nacos...");
            deployment = await NacosServer.StartAsync(client, workDirectory, cancellationToken);
            Console.WriteLine("Nacos is ready.");
            Console.WriteLine("Preparing embedding model assets...");
            var modelDirectory = await ModelAssets.PrepareAsync(
                client,
                Path.Combine(workDirectory, "model"),
                ModelSource.FromEnvironment(),
                cancellationToken);
            Console.WriteLine("Embedding model assets are ready.");

            fixture = OwnedProcess.Start(
                "dotnet",
                [
                    Path.Combine(AppContext.BaseDirectory, typeof(Program).Assembly.GetName().Name + ".dll"),
                    "--weather-fixture",
                    deployment.FixturePort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ],
                workDirectory,
                new Dictionary<string, string?>(),
                Path.Combine(outputDirectory, "fixture.log"));
            await WaitForFixtureAsync(client, deployment.FixturePort, fixture, cancellationToken);

            registration = await McpRegistration.RegisterAsync(
                deployment,
                "127.0.0.1",
                deployment.FixturePort,
                cancellationToken);

            var routerEnvironment = RouterConfiguration.BuildRouterEnvironment(
                deployment,
                deployment.RouterPort,
                modelDirectory,
                Path.Combine(workDirectory, "router-data"));
            router = OwnedProcess.Start(
                "nacos-mcp-router",
                [],
                workDirectory,
                routerEnvironment,
                Path.Combine(outputDirectory, "router.log"));
            await WaitForListeningAsync(router, deployment.RouterPort, cancellationToken);

            var routerUrl = $"http://127.0.0.1:{deployment.RouterPort}/mcp";
            var smokeEnvironment = RouterConfiguration.BuildSmokeEnvironment(deployment, routerUrl);
            await RunSmokeAsync("managed", ["dotnet", options.ManagedPath], options, outputDirectory, smokeEnvironment, cancellationToken);
            await RunSmokeAsync("native", [options.NativePath], options, outputDirectory, smokeEnvironment, cancellationToken);
            await RunRuntimeTestsAsync(options.TestDllPath, outputDirectory, smokeEnvironment, cancellationToken);

            if (router.ReadLogText().Contains("vector search failed", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Router embedding search failed and silently fell back; see router.log.");
            }

            var evidence = new AcceptanceEvidence
            {
                Nacos = "3.2.4",
                Router = "1.0.0",
                Authentication = true,
                Weather = "labelled fixture",
                DualRuntimeTests = true,
                Passed = true,
            };
            var json = JsonSerializer.Serialize(evidence, AcceptanceJsonContext.Default.AcceptanceEvidence);
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, "acceptance.json"), json, cancellationToken);
            Console.WriteLine($"NACOS_LIVE_ACCEPTANCE_PASS {outputDirectory}");
            return 0;
        }
        finally
        {
            await CleanupAsync("MCP registration", registration is null ? null : registration.DisposeAsync);
            await CleanupAsync("Router", router is null ? null : router.DisposeAsync);
            await CleanupAsync("weather fixture", fixture is null ? null : fixture.DisposeAsync);
            await CleanupAsync("Nacos", deployment is null ? null : deployment.Process.DisposeAsync);
            CopyNacosLog(workDirectory, outputDirectory);
            try
            {
                Directory.Delete(workDirectory, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"Temporary directory cleanup failed: {exception.Message}");
            }
        }
    }

    private static async Task RunSmokeAsync(
        string name,
        string[] command,
        AcceptanceOptions options,
        string outputDirectory,
        Dictionary<string, string?> environment,
        CancellationToken cancellationToken)
    {
        var reportPath = Path.Combine(outputDirectory, name + ".json");
        environment["OPENCLAW_NACOS_REPORT"] = reportPath;
        await using var process = OwnedProcess.Start(
            command[0],
            command.Skip(1),
            FindRepositoryRoot(),
            environment,
            Path.Combine(outputDirectory, name + ".log"));
        await process.WaitForExitAsync(TimeSpan.FromSeconds(210), cancellationToken);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{name} acceptance failed; see {name}.log.");
        }

        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath, cancellationToken));
        var nativeAot = report.RootElement.GetProperty("nativeAot").GetBoolean();
        var jsonReflection = report.RootElement.GetProperty("jsonReflection").GetBoolean();
        if (nativeAot != (name == "native") || jsonReflection)
        {
            throw new InvalidOperationException($"{name} execution mode was not verified; see {name}.json.");
        }

        Console.WriteLine($"{name} {await File.ReadAllTextAsync(reportPath, cancellationToken)}");
    }

    private static async Task RunRuntimeTestsAsync(
        string testDllPath,
        string outputDirectory,
        Dictionary<string, string?> environment,
        CancellationToken cancellationToken)
    {
        await using var process = OwnedProcess.Start(
            "dotnet",
            [
                "test",
                testDllPath,
                "--filter",
                "FullyQualifiedName~LiveRouter",
                "--results-directory",
                outputDirectory,
                "--logger",
                "trx;LogFileName=live-runtimes.trx",
            ],
            FindRepositoryRoot(),
            environment,
            Path.Combine(outputDirectory, "runtimes.log"));
        await process.WaitForExitAsync(TimeSpan.FromSeconds(300), cancellationToken);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException("Dual-runtime acceptance failed; see runtimes.log.");
        }
    }

    private static async Task WaitForFixtureAsync(
        HttpClient client,
        int port,
        OwnedProcess process,
        CancellationToken cancellationToken)
    {
        var healthUri = new Uri($"http://127.0.0.1:{port}/health");
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(StartupTimeout);
        while (!timeoutSource.IsCancellationRequested)
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException("Weather fixture exited before readiness; see fixture.log.");
            }

            try
            {
                using var response = await client.GetAsync(healthUri, timeoutSource.Token);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), timeoutSource.Token);
        }

        cancellationToken.ThrowIfCancellationRequested();
        throw new TimeoutException("Weather fixture readiness timed out; see fixture.log.");
    }

    private static async Task WaitForListeningAsync(
        OwnedProcess process,
        int port,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(StartupTimeout);
        while (!timeoutSource.IsCancellationRequested)
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException("Nacos MCP Router exited before listening; see router.log.");
            }

            using var probe = new TcpClient();
            try
            {
                await probe.ConnectAsync(IPAddress.Loopback, port, timeoutSource.Token);
                return;
            }
            catch (SocketException)
            {
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), timeoutSource.Token);
        }

        cancellationToken.ThrowIfCancellationRequested();
        throw new TimeoutException("Nacos MCP Router startup timed out; see router.log.");
    }

    private static async Task CleanupAsync(string name, Func<ValueTask>? cleanup)
    {
        if (cleanup is null)
        {
            return;
        }

        try
        {
            await cleanup();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"{name} cleanup failed: {exception.Message}");
        }
    }

    private static void CopyNacosLog(string workDirectory, string outputDirectory)
    {
        var source = Path.Combine(workDirectory, "nacos.log");
        if (File.Exists(source))
        {
            File.Copy(source, Path.Combine(outputDirectory, "nacos.log"), overwrite: true);
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "OpenClaw.Net.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the OpenClaw repository root.");
    }
}

internal sealed class AcceptanceEvidence
{
    public string Nacos { get; init; } = string.Empty;
    public string Router { get; init; } = string.Empty;
    public bool Authentication { get; init; }
    public string Weather { get; init; } = string.Empty;
    public bool DualRuntimeTests { get; init; }
    public bool Passed { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AcceptanceEvidence))]
internal partial class AcceptanceJsonContext : JsonSerializerContext;