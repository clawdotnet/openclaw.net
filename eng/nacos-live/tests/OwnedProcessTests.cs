using NacosLiveAcceptance;
using Xunit;

namespace NacosLiveAcceptance.Tests;

public sealed class OwnedProcessTests
{
    [Fact]
    public async Task Start_CapturesStandardOutputAndErrorInLog()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var logPath = Path.Combine(temporaryDirectory.Path, "process.log");

        await using (var process = OwnedProcess.Start(
            "dotnet",
            ["--version"],
            temporaryDirectory.Path,
            new Dictionary<string, string?>(),
            logPath))
        {
            await process.WaitForExitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        }

        Assert.Contains(
            "10.0.",
            await File.ReadAllTextAsync(logPath, TestContext.Current.CancellationToken),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task StopAsync_TerminatesLongLivedProcessTreeWithinTimeout()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var logPath = Path.Combine(temporaryDirectory.Path, "process.log");
        var (fileName, arguments) = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe", new[] { "/d", "/c", "ping -t 127.0.0.1" })
            : ("/bin/sh", new[] { "-c", "sleep 30" });

        await using var process = OwnedProcess.Start(
            fileName,
            arguments,
            temporaryDirectory.Path,
            new Dictionary<string, string?>(),
            logPath);
        await process.StopAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await process.WaitForExitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("nacos-process-test-").FullName;

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}