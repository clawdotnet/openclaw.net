using System.Text;
using OpenClaw.Core.Setup;
using OpenClaw.Gateway.Bootstrap;

namespace OpenClaw.Gateway.Pipeline;

internal static class StartupReadyReporter
{
    private static readonly TimeSpan LiveNoticeWindow = TimeSpan.FromSeconds(5);

    public static void Register(
        WebApplication app,
        GatewayStartupContext startup,
        StartupLaunchOptions launchOptions,
        LocalStartupSession? localSession,
        LocalStartupStateStore stateStore)
    {
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            try
            {
                var collector = app.Services.GetRequiredService<StartupNoticeCollector>();
                var notices = collector.Snapshot();
                Console.Out.WriteLine("Ready");
                Write(
                    startup,
                    notices,
                    ResolveKnownConfigPath(launchOptions, stateStore));
                collector.EnableLiveOutput(
                    Console.Out,
                    LiveNoticeWindow,
                    headerAlreadyWritten: notices.Count > 0);
            }
            catch (Exception ex)
            {
                app.Logger.LogWarning(ex, "Failed to write startup readiness message.");
            }

            if (localSession is not null && launchOptions.CanPrompt)
            {
                _ = Task.Run(() => LocalStartupPostReadyActions.RunAsync(
                    startup,
                    launchOptions,
                    localSession,
                    stateStore,
                    app.Logger,
                    app.Lifetime.ApplicationStopping));
            }
        });
    }

    internal static void Write(
        GatewayStartupContext startup,
        IReadOnlyList<StartupNoticeSnapshot>? notices = null,
        string? knownConfigPath = null,
        TextWriter? output = null)
    {
        var writer = output ?? Console.Out;
        writer.WriteLine(Render(startup, notices, knownConfigPath));
        writer.Flush();
    }

    internal static string Render(
        GatewayStartupContext startup,
        IReadOnlyList<StartupNoticeSnapshot>? notices = null,
        string? knownConfigPath = null)
    {
        var bindAddress = FormatHostForUri(startup.Config.BindAddress);
        var sb = new StringBuilder();

        sb.AppendLine("OpenClaw gateway ready.");
        sb.AppendLine($"Listening on http://{bindAddress}:{startup.Config.Port}");

        if (startup.Config.Port <= 0)
        {
            sb.AppendLine("URLs are not shown because a valid port was not configured.");
            return sb.ToString().TrimEnd();
        }

        var host = ResolveDisplayHost(startup.Config.BindAddress);
        var httpBase = $"http://{host}:{startup.Config.Port}";
        var wsBase = $"ws://{host}:{startup.Config.Port}";

        sb.AppendLine($"Chat UI: {httpBase}/chat");
        sb.AppendLine($"Admin UI: {httpBase}/admin");
        sb.AppendLine($"Doctor: {httpBase}/doctor/text");
        sb.AppendLine($"Health: {httpBase}/health");
        sb.AppendLine($"MCP: {httpBase}/mcp");
        sb.AppendLine($"WebSocket: {wsBase}/ws");
        sb.AppendLine("Ctrl-C to stop");

        if (notices is not null && notices.Count > 0)
        {
            sb.AppendLine("Started with notices:");
            foreach (var notice in notices)
            {
                sb.Append("- ");
                sb.Append(notice.Message);
                if (notice.Count > 1)
                    sb.Append($" (x{notice.Count})");
                sb.AppendLine();
            }
        }

        sb.AppendLine("Next useful commands:");
        if (!string.IsNullOrWhiteSpace(knownConfigPath))
        {
            sb.AppendLine($"- openclaw setup verify --config {GatewaySetupPaths.QuoteIfNeeded(knownConfigPath)}");
        }
        else
        {
            sb.AppendLine("- dotnet run --project src/OpenClaw.Gateway -c Release -- --doctor");
            sb.AppendLine("- openclaw models doctor");
        }

        return sb.ToString().TrimEnd();
    }

    internal static string ResolveDisplayHost(string bindAddress)
    {
        if (string.IsNullOrWhiteSpace(bindAddress))
            return "localhost";

        if (GatewaySecurity.IsLoopbackBind(bindAddress))
            return "localhost";

        return bindAddress switch
        {
            "0.0.0.0" or "*" or "+" or "::" or "[::]" => "localhost",
            _ => FormatHostForUri(bindAddress)
        };
    }

    internal static string FormatHostForUri(string bindAddress)
    {
        if (string.IsNullOrWhiteSpace(bindAddress))
            return "localhost";

        if (bindAddress.StartsWith('['))
            return bindAddress;

        if (bindAddress.Contains(':'))
            return $"[{bindAddress}]";

        return bindAddress;
    }

    private static string? ResolveKnownConfigPath(StartupLaunchOptions launchOptions, LocalStartupStateStore stateStore)
        => launchOptions.ExternalConfigPath ?? stateStore.Load().LastSavedConfigPath;
}
