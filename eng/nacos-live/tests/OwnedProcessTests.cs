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
        var (fileName, arguments) = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe",
                new[] { "/d", "/c", "echo owned-process-stdout & echo owned-process-stderr 1>&2" })
            : ("/bin/sh", new[] { "-c", "printf '%s\\n' owned-process-stdout; printf '%s\\n' owned-process-stderr >&2" });

        await using (var process = OwnedProcess.Start(
            fileName,
            arguments,
            temporaryDirectory.Path,
            new Dictionary<string, string?>(),
            logPath))
        {
            await process.WaitForExitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            Assert.Equal(0, process.ExitCode);
        }

        var log = await File.ReadAllTextAsync(logPath, TestContext.Current.CancellationToken);
        Assert.Contains("owned-process-stdout", log, StringComparison.Ordinal);
        Assert.Contains("owned-process-stderr", log, StringComparison.Ordinal);
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