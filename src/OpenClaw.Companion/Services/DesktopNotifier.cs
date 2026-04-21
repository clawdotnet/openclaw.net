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
    public bool IsAvailable => Platform is NotifierPlatform.MacOs or NotifierPlatform.Linux;

    public string PlatformDescription => Platform switch
    {
        NotifierPlatform.MacOs => "macOS Notification Center (osascript)",
        NotifierPlatform.Linux => "libnotify (notify-send)",
        NotifierPlatform.Windows => "not supported on Windows in this release",
        _ => "unsupported platform"
    };

    public Task NotifyAsync(string title, string body, CancellationToken cancellationToken = default)
        => Platform switch
        {
            NotifierPlatform.MacOs => NotifyMacOsAsync(title, body, cancellationToken),
            NotifierPlatform.Linux => NotifyLinuxAsync(title, body, cancellationToken),
            _ => Task.CompletedTask
        };

    private static NotifierPlatform Platform
    {
        get
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return NotifierPlatform.MacOs;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return NotifierPlatform.Linux;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return NotifierPlatform.Windows;
            return NotifierPlatform.Unknown;
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

            using var proc = Process.Start(info);
            if (proc is null)
                return;

            await proc.WaitForExitAsync(ct);
        }
        catch
        {
            // Best-effort. Missing tools, perms issues, or cancellation should not bubble
            // into the caller — notifications are a nice-to-have, not load-bearing.
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
