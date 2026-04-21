using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OpenClaw.Companion.Services;

public interface IDesktopNotifier
{
    bool IsAvailable { get; }

    string PlatformDescription { get; }

    Task NotifyAsync(string title, string body, CancellationToken cancellationToken = default);
}

public sealed class DesktopNotifier : IDesktopNotifier
{
    // Bound each notification attempt so a misbehaving child can't leak forever.
    private static readonly TimeSpan NotifyTimeout = TimeSpan.FromSeconds(5);

    private readonly NotifierPlatform _platform;
    private readonly bool _toolPresent;

    public DesktopNotifier()
    {
        _platform = DetectPlatform();
        _toolPresent = ResolveToolPresent(_platform);
    }

    public bool IsAvailable => _toolPresent;

    public string PlatformDescription => (_platform, _toolPresent) switch
    {
        (NotifierPlatform.MacOs, true) => "macOS Notification Center (osascript)",
        (NotifierPlatform.MacOs, false) => "macOS detected but osascript is not on PATH",
        (NotifierPlatform.Linux, true) => "libnotify (notify-send)",
        (NotifierPlatform.Linux, false) => "Linux detected but notify-send is not on PATH",
        (NotifierPlatform.Windows, _) => "not supported on Windows in this release",
        _ => "unsupported platform"
    };

    public Task NotifyAsync(string title, string body, CancellationToken cancellationToken = default)
    {
        if (!_toolPresent)
            return Task.CompletedTask;

        return _platform switch
        {
            NotifierPlatform.MacOs => NotifyMacOsAsync(title, body, cancellationToken),
            NotifierPlatform.Linux => NotifyLinuxAsync(title, body, cancellationToken),
            _ => Task.CompletedTask
        };
    }

    private static NotifierPlatform DetectPlatform()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return NotifierPlatform.MacOs;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return NotifierPlatform.Linux;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return NotifierPlatform.Windows;
        return NotifierPlatform.Unknown;
    }

    private static bool ResolveToolPresent(NotifierPlatform platform)
        => platform switch
        {
            NotifierPlatform.MacOs => IsOnPath("osascript"),
            NotifierPlatform.Linux => IsOnPath("notify-send"),
            _ => false
        };

    private static bool IsOnPath(string executable)
    {
        try
        {
            var info = new ProcessStartInfo("/usr/bin/env")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            info.ArgumentList.Add("which");
            info.ArgumentList.Add(executable);

            using var proc = Process.Start(info);
            if (proc is null)
                return false;

            if (!proc.WaitForExit(1000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
                return false;
            }

            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static Task NotifyMacOsAsync(string title, string body, CancellationToken ct)
    {
        var script = $"display notification \"{EscapeAppleScript(body)}\" with title \"{EscapeAppleScript(title)}\"";
        return RunProcessAsync("osascript", ["-e", script], ct);
    }

    private static Task NotifyLinuxAsync(string title, string body, CancellationToken ct)
        => RunProcessAsync("notify-send", ["--app-name=OpenClaw.Companion", "--", title, body], ct);

    private static async Task RunProcessAsync(string fileName, string[] arguments, CancellationToken ct)
    {
        Process? proc = null;
        try
        {
            var info = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var arg in arguments)
                info.ArgumentList.Add(arg);

            proc = Process.Start(info);
            if (proc is null)
                return;

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(NotifyTimeout);

            // Drain stdout/stderr concurrently so a chatty tool can't deadlock us
            // by filling the pipe buffer. Wait for exit as a third parallel task.
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(linked.Token);
            var stderrTask = proc.StandardError.ReadToEndAsync(linked.Token);
            var exitTask = proc.WaitForExitAsync(linked.Token);

            try
            {
                await Task.WhenAll(stdoutTask, stderrTask, exitTask);
            }
            catch (OperationCanceledException)
            {
                // Cancellation or timeout: kill the orphan so we don't leak processes.
                try { proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            }
        }
        catch
        {
            // Missing tools, perms issues, and similar should not bubble into callers —
            // notifications are nice-to-have, not load-bearing.
            try { proc?.Kill(entireProcessTree: true); } catch { /* best-effort */ }
        }
        finally
        {
            proc?.Dispose();
        }
    }

    private static string EscapeAppleScript(string value)
        => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private enum NotifierPlatform
    {
        Unknown,
        MacOs,
        Linux,
        Windows
    }
}
