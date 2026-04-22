using System.Text;
using OpenClaw.Gateway.Bootstrap;

namespace OpenClaw.Gateway.Pipeline;

internal static class StartupReadyReporter
{
    public static void Register(WebApplication app, GatewayStartupContext startup)
    {
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            try
            {
                Write(startup);
            }
            catch (Exception ex)
            {
                app.Logger.LogWarning(ex, "Failed to write startup readiness message.");
            }
        });
    }

    internal static void Write(GatewayStartupContext startup, TextWriter? output = null)
    {
        var writer = output ?? Console.Out;
        writer.WriteLine(Render(startup));
        writer.Flush();
    }

    internal static string Render(GatewayStartupContext startup)
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
}
