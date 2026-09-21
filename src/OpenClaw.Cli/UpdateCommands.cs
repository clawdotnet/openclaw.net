using System.Diagnostics;
using OpenClaw.Core.Updates;
namespace OpenClaw.Cli;
internal static class UpdateCommands
{
    public static async Task<int> RunAsync(string[] args)
    {
        string? Option(string key) { var i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        var updater = new BundleUpdater(http, Option("--root") ?? BundleUpdater.DefaultRoot);
        var command = args.FirstOrDefault();
        switch (command)
        {
            case "trust":
                updater.ConfigureTrust(new(Option("--manifest") ?? throw new ArgumentException("--manifest is required"), File.ReadAllText(Option("--key") ?? throw new ArgumentException("--key PEM path is required"))));
                Console.WriteLine("Publisher trust saved. Verify the key fingerprint through an independent channel."); return 0;
            case "check":
                var release = await updater.CheckAsync(Option("--channel") ?? "stable", Option("--version"), CancellationToken.None);
                Console.WriteLine($"{release.Channel}: {release.Version}"); return 0;
            case "install":
                if (!args.Contains("--yes")) throw new ArgumentException("Review update check, then pass --yes to install and activate the bundle.");
                Console.WriteLine(await updater.InstallAsync(Option("--channel") ?? "stable", Option("--version"), CancellationToken.None));
                Console.WriteLine("Bundle activated. Stop the old gateway before launching the new gateway. Use openclaw update launch companion (or cli/gateway)."); return 0;
            case "rollback":
                if (!args.Contains("--yes")) throw new ArgumentException("Pass --yes to activate the previous bundle.");
                Console.WriteLine(updater.Rollback()); return 0;
            case "launch":
                var component = args.Length > 1 ? args[1] : "companion";
                var info = new ProcessStartInfo(updater.GetActiveExecutable(component)) { UseShellExecute = false };
                var separator = Array.IndexOf(args, "--");
                if (separator >= 0) foreach (var arg in args[(separator + 1)..]) info.ArgumentList.Add(arg);
                using (var process = Process.Start(info) ?? throw new IOException("Cannot launch active bundle.")) { await process.WaitForExitAsync(); return process.ExitCode; }
            default:
                Console.WriteLine("openclaw update trust --manifest <https URL> --key <publisher.pem>\nopenclaw update check|install [--channel stable|beta] [--version <version>] [--yes]\nopenclaw update rollback --yes\nopenclaw update launch cli|gateway|companion [-- <arguments>]\nAll commands accept --root <managed update directory>."); return 0;
        }
    }
}
