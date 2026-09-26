using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NacosLiveAcceptance;

internal static class NacosServer
{
    private const string Version = "3.2.4";
    private const string PinnedSha256 = "da5eec77934140133fe93e5532079e4e99b4cae7eb50463f2f6cd2bc8f380a70";
    private const string ArchiveUrl = "https://github.com/alibaba/nacos/releases/download/3.2.4/nacos-server-3.2.4.tar.gz";

    public static async Task<NacosDeployment> StartAsync(
        HttpClient client,
        string workDirectory,
        CancellationToken cancellationToken)
    {
        var fullWorkDirectory = Path.GetFullPath(workDirectory);
        Directory.CreateDirectory(fullWorkDirectory);
        var archivePath = Path.Combine(fullWorkDirectory, $"nacos-server-{Version}.tar.gz");
        var temporaryArchivePath = archivePath + ".download";
        await DownloadFileAsync(client, new Uri(ArchiveUrl), temporaryArchivePath, cancellationToken);
        var actualHash = await ComputeSha256Async(temporaryArchivePath, cancellationToken);
        if (!MatchesPinnedSha256(actualHash))
        {
            File.Delete(temporaryArchivePath);
            throw new InvalidDataException("Nacos archive does not match the pinned 3.2.4 release SHA-256.");
        }

        File.Move(temporaryArchivePath, archivePath, overwrite: true);
        ExtractArchive(archivePath, fullWorkDirectory);
        File.Delete(archivePath);

        var nacosDirectory = Path.Combine(fullWorkDirectory, "nacos");
        var propertiesPath = Path.Combine(nacosDirectory, "conf", "application.properties");
        var ports = SelectPorts();
        var password = CreatePassword();
        var identityValue = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        var tokenSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        var properties = string.Join('\n',
        [
            $"nacos.server.main.port={ports.ServerPort}",
            $"nacos.console.port={ports.ConsolePort}",
            "server.address=127.0.0.1",
            "nacos.inetutils.ip-address=127.0.0.1",
            "nacos.core.auth.enabled=true",
            "nacos.core.auth.admin.enabled=true",
            "nacos.core.auth.console.enabled=true",
            "nacos.core.auth.caching.enabled=false",
            "nacos.ai.mcp.registry.enabled=false",
            "nacos.ai.skill.registry.enabled=false",
            "nacos.core.auth.server.identity.key=acceptance",
            $"nacos.core.auth.server.identity.value={identityValue}",
            $"nacos.core.auth.plugin.nacos.token.secret.key={tokenSecret}",
        ]);
        await File.AppendAllTextAsync(propertiesPath, Environment.NewLine + properties + Environment.NewLine, cancellationToken);
        RestrictFilePermissions(propertiesPath);

        var logPath = Path.Combine(fullWorkDirectory, "nacos.log");
        var process = OwnedProcess.Start(
            "java",
            [
                "-Xms256m",
                "-Xmx512m",
                "-Dnacos.standalone=true",
                "--add-opens=java.base/java.lang=ALL-UNNAMED",
                "--add-opens=java.base/java.lang.reflect=ALL-UNNAMED",
                "--add-opens=java.base/java.util=ALL-UNNAMED",
                "-Dnacos.deployment.type=merged",
                $"-Dnacos.home={nacosDirectory}",
                $"-Dloader.path={Path.Combine(nacosDirectory, "plugins")}",
                "-jar",
                Path.Combine(nacosDirectory, "target", "nacos-server.jar"),
                $"--spring.config.additional-location=file:{Path.Combine(nacosDirectory, "conf")}{Path.DirectorySeparatorChar}",
                $"--logging.config={Path.Combine(nacosDirectory, "conf", "nacos-logback.xml")}",
            ],
            fullWorkDirectory,
            new Dictionary<string, string?>(),
            logPath);

        try
        {
            var consoleAddress = $"http://127.0.0.1:{ports.ConsolePort}";
            await WaitForReadinessAsync(client, consoleAddress, process, cancellationToken);
            await InitializeAdminAsync(client, consoleAddress, password, cancellationToken);
            return new NacosDeployment(
                $"127.0.0.1:{ports.ServerPort}",
                ports.ServerPort,
                ports.ConsolePort,
                ports.RouterPort,
                ports.FixturePort,
                "nacos",
                password,
                process);
        }
        catch
        {
            await process.DisposeAsync();
            throw;
        }
    }

    public static (int ServerPort, int ConsolePort, int RouterPort, int FixturePort) SelectPorts()
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var serverPort = RandomNumberGenerator.GetInt32(20000, 45000);
            var routerPort = 0;
            var fixturePort = 0;
            var ports = new HashSet<int>
            {
                serverPort,
                serverPort + 1000,
                serverPort + 1001,
                8080,
            };
            while (routerPort == 0 || fixturePort == 0)
            {
                var candidate = RandomNumberGenerator.GetInt32(20000, 45000);
                if (ports.Add(candidate))
                {
                    if (routerPort == 0)
                    {
                        routerPort = candidate;
                    }
                    else
                    {
                        fixturePort = candidate;
                    }
                }
            }

            var listeners = new List<Socket>();
            try
            {
                foreach (var port in ports)
                {
                    var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    listeners.Add(socket);
                    socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
                }

                return (serverPort, 8080, routerPort, fixturePort);
            }
            catch (SocketException)
            {
            }
            finally
            {
                foreach (var listener in listeners)
                {
                    listener.Dispose();
                }
            }
        }

        throw new IOException("Could not reserve loopback ports for the Nacos live acceptance run.");
    }

    internal static bool MatchesPinnedSha256(string actualSha256) =>
        string.Equals(actualSha256, PinnedSha256, StringComparison.Ordinal);

    internal static bool HasAccessToken(JsonElement loginResponse) =>
        loginResponse.ValueKind == JsonValueKind.Object
        && loginResponse.TryGetProperty("accessToken", out var accessToken)
        && accessToken.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(accessToken.GetString());

    private static async Task DownloadFileAsync(
        HttpClient client,
        Uri source,
        string destination,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var sourceStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destinationStream = new FileStream(
            destination,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            81920,
            useAsync: true);
        await sourceStream.CopyToAsync(destinationStream, cancellationToken);
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexStringLower(hash);
    }

    private static void ExtractArchive(string archivePath, string destination)
    {
        var root = Path.GetFullPath(destination);
        using var file = File.OpenRead(archivePath);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        TarEntry? entry;
        while ((entry = reader.GetNextEntry()) is not null)
        {
            var entryName = entry.Name.Replace('/', Path.DirectorySeparatorChar);
            var targetPath = Path.GetFullPath(Path.Combine(root, entryName));
            var relativePath = Path.GetRelativePath(root, targetPath);
            if (Path.IsPathRooted(entryName)
                || relativePath == ".."
                || relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Nacos archive contains an unsafe path: {entry.Name}");
            }

            if (entry.EntryType == TarEntryType.Directory)
            {
                Directory.CreateDirectory(targetPath);
                continue;
            }

            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
            {
                throw new InvalidDataException($"Nacos archive contains an unsupported entry: {entry.Name}");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            using var output = new FileStream(targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            entry.DataStream?.CopyTo(output);
        }
    }

    private static void RestrictFilePermissions(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static async Task WaitForReadinessAsync(
        HttpClient client,
        string consoleAddress,
        OwnedProcess process,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(TimeSpan.FromSeconds(90));
        var readinessToken = timeoutSource.Token;
        var readinessUri = new Uri($"{consoleAddress}/v3/console/health/readiness");
        while (!readinessToken.IsCancellationRequested)
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException("Nacos exited before readiness; see nacos.log.");
            }

            try
            {
                using var response = await client.GetAsync(readinessUri, readinessToken);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), readinessToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        throw new TimeoutException("Nacos readiness timed out after 90 seconds; see nacos.log.");
    }

    private static async Task InitializeAdminAsync(
        HttpClient client,
        string consoleAddress,
        string password,
        CancellationToken cancellationToken)
    {
        using var initializeResponse = await client.PostAsync(
            new Uri($"{consoleAddress}/v3/auth/user/admin"),
            new FormUrlEncodedContent(new Dictionary<string, string> { ["password"] = password }),
            cancellationToken);
        initializeResponse.EnsureSuccessStatusCode();
        using var initializeJson = JsonDocument.Parse(await initializeResponse.Content.ReadAsStringAsync(cancellationToken));
        if (!initializeJson.RootElement.TryGetProperty("code", out var initializeCode)
            || initializeCode.ValueKind != JsonValueKind.Number
            || initializeCode.GetInt32() != 0)
        {
            throw new InvalidOperationException("Nacos administrator initialization response did not contain code 0.");
        }

        using var loginResponse = await client.PostAsync(
            new Uri($"{consoleAddress}/v3/auth/user/login"),
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["username"] = "nacos",
                ["password"] = password,
            }),
            cancellationToken);
        loginResponse.EnsureSuccessStatusCode();
        using var loginJson = JsonDocument.Parse(await loginResponse.Content.ReadAsStringAsync(cancellationToken));
        if (!HasAccessToken(loginJson.RootElement))
        {
            throw new InvalidOperationException("Nacos administrator login response did not contain an accessToken.");
        }
    }

    private static string CreatePassword()
    {
        var random = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
            .Replace('+', 'x')
            .Replace('/', 'y')
            .TrimEnd('=');
        return "Nacos-" + random;
    }
}

internal sealed record NacosDeployment(
    string ServerAddress,
    int ServerPort,
    int ConsolePort,
    int RouterPort,
    int FixturePort,
    string Username,
    string Password,
    OwnedProcess Process);