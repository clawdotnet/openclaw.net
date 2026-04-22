using System.Diagnostics;

namespace OpenClaw.Gateway.Bootstrap;

internal static class BrowserLauncher
{
    public static void TryOpen(string url)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                }
            };
            process.Start();
        }
        catch
        {
        }
    }
}
