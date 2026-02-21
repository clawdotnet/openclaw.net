using System.Net.Sockets;
using Spectre.Console;
using OpenClaw.Core.Models;

namespace OpenClaw.Core.Validation;

/// <summary>
/// Provides a self-diagnostic `--doctor` CLI mode that runs pre-flight checks on the
/// current configuration and environment, enabling rapid troubleshooting.
/// </summary>
public static class DoctorCheck
{
    public static async Task<bool> RunAsync(GatewayConfig config)
    {
        AnsiConsole.MarkupLine("\n[bold cyan]OpenClaw.NET Doctor Mode[/]\n");
        var allPassed = true;

        allPassed &= Check("LLM API Key configured", () => !string.IsNullOrWhiteSpace(config.Llm.ApiKey));
        
        allPassed &= Check("LLM max tokens > 0", () => config.Llm.MaxTokens > 0);

        if (config.BindAddress != "127.0.0.1" && config.BindAddress != "localhost")
        {
            allPassed &= Check("Public Bind: Auth Token is set", () => !string.IsNullOrWhiteSpace(config.AuthToken),
                warnOnly: false, "Binding to 0.0.0.0 without an AuthToken is extremely dangerous.");
                
            allPassed &= Check("Public Bind: Unsafe Shell Tooling disabled", () => !config.Security.AllowUnsafeToolingOnPublicBind || !config.Tooling.AllowShell,
                warnOnly: true, "Shell is enabled while bound to a public interface. This is a severe RCE risk unless behind a strict WAF.");
            
            allPassed &= Check("Public Bind: Wildcard read/write roots disabled", () => 
                !config.Tooling.AllowedReadRoots.Contains("*") && !config.Tooling.AllowedWriteRoots.Contains("*"),
                warnOnly: true, "Wildcard (*) filesystem access is enabled on a public bind. Data exfiltration risk.");
        }

        allPassed &= Check("Storage path exists and is writable", () =>
        {
            try
            {
                Directory.CreateDirectory(config.Memory.StoragePath);
                var testFile = Path.Combine(config.Memory.StoragePath, ".doctor-test");
                File.WriteAllText(testFile, "test");
                File.Delete(testFile);
                return true;
            }
            catch
            {
                return false;
            }
        });

        allPassed &= await CheckAsync("TCP Port is available", async () =>
        {
            try
            {
                using var tcpClient = new TcpClient();
                await tcpClient.ConnectAsync(config.BindAddress == "0.0.0.0" ? "127.0.0.1" : config.BindAddress, config.Port);
                return false; // Connection succeeded meaning something is already listening
            }
            catch (SocketException)
            {
                return true; // Connection refused = port is free
            }
        });
        
        if (config.Channels.Sms.Twilio.Enabled)
        {
            allPassed &= Check("Twilio config: AccountSID and TokenRef set", () => 
                !string.IsNullOrWhiteSpace(config.Channels.Sms.Twilio.AccountSid) && 
                !string.IsNullOrWhiteSpace(config.Channels.Sms.Twilio.AuthTokenRef));
        }

        if (config.Channels.Telegram.Enabled)
        {
            allPassed &= Check("Telegram config: BotTokenRef set", () => 
                !string.IsNullOrWhiteSpace(config.Channels.Telegram.BotTokenRef) || !string.IsNullOrWhiteSpace(config.Channels.Telegram.BotToken));
        }

        AnsiConsole.MarkupLine("\n[bold]Doctor summary:[/]");
        if (allPassed)
        {
            AnsiConsole.MarkupLine("[bold green]✔ All critical checks passed. OpenClaw is ready to launch.[/]\n");
            return true;
        }
        else
        {
            AnsiConsole.MarkupLine("[bold red]✖ One or more critical checks failed. Please review the output above.[/]\n");
            return false;
        }
    }

    private static bool Check(string description, Func<bool> checkFn, bool warnOnly = false, string? detail = null)
    {
        bool passed;
        try { passed = checkFn(); } catch { passed = false; }
        PrintResult(description, passed, warnOnly, detail);
        return warnOnly || passed;
    }

    private static async Task<bool> CheckAsync(string description, Func<Task<bool>> checkFn, bool warnOnly = false, string? detail = null)
    {
        bool passed;
        try { passed = await checkFn(); } catch { passed = false; }
        PrintResult(description, passed, warnOnly, detail);
        return warnOnly || passed;
    }

    private static void PrintResult(string description, bool passed, bool warnOnly, string? detail)
    {
        if (passed)
        {
            AnsiConsole.MarkupLine($"[green]✔[/] {description}");
        }
        else if (warnOnly)
        {
            AnsiConsole.MarkupLine($"[yellow]⚠[/] {description} (Warning)");
            if (detail != null) AnsiConsole.MarkupLine($"    [grey]{detail}[/]");
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]✖[/] {description} (Failed)");
            if (detail != null) AnsiConsole.MarkupLine($"    [grey]{detail}[/]");
        }
    }
}
