using System.Net.Sockets;
using Spectre.Console;
using OpenClaw.Core.Models;
using OpenClaw.Core.Plugins;
using OpenClaw.Core.Security;

namespace OpenClaw.Core.Validation;

/// <summary>
/// Provides a self-diagnostic `--doctor` CLI mode that runs pre-flight checks on the
/// current configuration and environment, enabling rapid troubleshooting.
/// </summary>
public static class DoctorCheck
{
    public static async Task<bool> RunAsync(GatewayConfig config, GatewayRuntimeState runtimeState)
    {
        AnsiConsole.MarkupLine("\n[bold cyan]OpenClaw.NET Doctor Mode[/]\n");
        var allPassed = true;

        // Note: by the time Doctor runs, the Gateway has already applied env var overrides
        // (MODEL_PROVIDER_KEY, etc.). So this should reflect the effective configuration.
        allPassed &= Check("Runtime mode resolved", () => true, detail:
            $"requested={runtimeState.RequestedMode}, effective={runtimeState.EffectiveModeName}, dynamic_code_supported={runtimeState.DynamicCodeSupported}");
        allPassed &= Check("LLM API Key configured", () => !string.IsNullOrWhiteSpace(config.Llm.ApiKey));
        
        allPassed &= Check("LLM max tokens > 0", () => config.Llm.MaxTokens > 0);

        var workspaceRoot = ResolveConfiguredPath(config.Tooling.WorkspaceRoot);
        if (config.Tooling.WorkspaceOnly)
        {
            allPassed &= Check(
                "Workspace root resolves to an absolute path",
                () => !string.IsNullOrWhiteSpace(workspaceRoot) && Path.IsPathRooted(workspaceRoot),
                warnOnly: false,
                detail: "Set OpenClaw:Tooling:WorkspaceRoot to an absolute path or a resolving env: reference.");
            allPassed &= Check(
                "Workspace root exists",
                () => !string.IsNullOrWhiteSpace(workspaceRoot) && Directory.Exists(workspaceRoot),
                warnOnly: false,
                detail: "Create the workspace directory or disable Tooling.WorkspaceOnly.");
        }

        allPassed &= Check(
            "Filesystem root policy is well-formed",
            () => HasValidRootSet(config.Tooling.AllowedReadRoots) && HasValidRootSet(config.Tooling.AllowedWriteRoots),
            warnOnly: false,
            detail: "Do not mix '*' with explicit roots, and use absolute paths for explicit filesystem roots.");

        if (config.BindAddress != "127.0.0.1" && config.BindAddress != "localhost")
        {
            allPassed &= Check("Public Bind: Auth Token is set", () => !string.IsNullOrWhiteSpace(config.AuthToken),
                warnOnly: false, "Binding to 0.0.0.0 without an AuthToken is extremely dangerous.");

            allPassed &= Check(
                "Public Bind: requester-matched HTTP tool approvals enabled",
                () => config.Security.RequireRequesterMatchForHttpToolApproval,
                warnOnly: true,
                detail: "Enable OpenClaw:Security:RequireRequesterMatchForHttpToolApproval for public deployments.");

            var localShellEnabled = config.Tooling.AllowShell &&
                !ToolSandboxPolicy.IsRequireSandboxed(config, "shell", ToolSandboxMode.Prefer);
            allPassed &= Check("Public Bind: Unsafe Shell Tooling disabled", () => !config.Security.AllowUnsafeToolingOnPublicBind || !localShellEnabled,
                warnOnly: true, "Shell is enabled while bound to a public interface. This is a severe RCE risk unless behind a strict WAF.");
            
            allPassed &= Check("Public Bind: Wildcard read/write roots disabled", () => 
                !config.Tooling.AllowedReadRoots.Contains("*") && !config.Tooling.AllowedWriteRoots.Contains("*"),
                warnOnly: true, "Wildcard (*) filesystem access is enabled on a public bind. Data exfiltration risk.");
        }

        if (config.Plugins.DynamicNative.Enabled)
        {
            allPassed &= Check(
                "Dynamic native plugins require JIT mode",
                () => runtimeState.EffectiveMode == GatewayRuntimeMode.Jit,
                warnOnly: false,
                detail: "Disable OpenClaw:Plugins:DynamicNative:Enabled or run a JIT-capable artifact / mode.");
        }

        if (config.Plugins.Enabled)
        {
            allPassed &= Check(
                "Bridge plugin host dependency available",
                IsNodeAvailable,
                warnOnly: true,
                detail: "Install Node.js or disable bridge plugins.");
        }

        if (config.Plugins.Mcp.Enabled)
        {
            foreach (var (serverId, server) in config.Plugins.Mcp.Servers)
            {
                if (!server.Enabled)
                    continue;

                var transport = server.NormalizeTransport();
                if (transport == "http")
                {
                    allPassed &= Check(
                        $"MCP server '{serverId}' URL configured",
                        () => Uri.TryCreate(server.Url, UriKind.Absolute, out _),
                        warnOnly: true,
                        detail: "Set a valid absolute HTTP(S) URL for the MCP server.");
                }
                else
                {
                    allPassed &= Check(
                        $"MCP server '{serverId}' command configured",
                        () => !string.IsNullOrWhiteSpace(server.Command),
                        warnOnly: true,
                        detail: "Set Plugins:Mcp:Servers:<id>:Command or disable the server.");
                }
            }
        }

        if (config.Plugins.Native.Mqtt.Enabled)
        {
            allPassed &= Check(
                "MQTT integration host/port configured",
                () => !string.IsNullOrWhiteSpace(config.Plugins.Native.Mqtt.Host) && config.Plugins.Native.Mqtt.Port > 0,
                warnOnly: true,
                detail: "Set Plugins:Native:Mqtt:Host and a valid Port, or disable MQTT.");
        }

        if (config.Plugins.Native.Email.Enabled)
        {
            allPassed &= Check(
                "Email integration SMTP configured",
                () => !string.IsNullOrWhiteSpace(config.Plugins.Native.Email.SmtpHost) && config.Plugins.Native.Email.SmtpPort > 0,
                warnOnly: true,
                detail: "Set Plugins:Native:Email:SmtpHost/SmtpPort for outbound mail.");
            allPassed &= Check(
                "Email integration IMAP configured",
                () => !string.IsNullOrWhiteSpace(config.Plugins.Native.Email.ImapHost) && config.Plugins.Native.Email.ImapPort > 0,
                warnOnly: true,
                detail: "Set Plugins:Native:Email:ImapHost/ImapPort for inbox monitoring.");
        }

        if (ToolSandboxPolicy.IsOpenSandboxProviderConfigured(config))
        {
            allPassed &= Check(
                "OpenSandbox endpoint configured",
                () => Uri.TryCreate(config.Sandbox.Endpoint, UriKind.Absolute, out _),
                warnOnly: false);

            allPassed &= await CheckAsync(
                "OpenSandbox endpoint reachable",
                () => PingOpenSandboxAsync(config),
                warnOnly: false,
                detail: "Verify OpenClaw:Sandbox:Endpoint and the OpenSandbox service/API key.");
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

        allPassed &= Check("Storage volume has free space", () =>
        {
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(config.Memory.StoragePath));
                if (string.IsNullOrWhiteSpace(root))
                    return true;

                var drive = new DriveInfo(root);
                return drive.AvailableFreeSpace > 100L * 1024L * 1024L;
            }
            catch
            {
                return true;
            }
        }, warnOnly: true, detail: "Less than 100 MB free space remains on the storage volume.");

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

    private static async Task<bool> PingOpenSandboxAsync(GatewayConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.Sandbox.Endpoint) ||
            !Uri.TryCreate(config.Sandbox.Endpoint, UriKind.Absolute, out var endpoint))
        {
            return false;
        }

        var apiKey = config.Sandbox.ApiKey;
        if (!string.IsNullOrWhiteSpace(apiKey) &&
            (apiKey.StartsWith("env:", StringComparison.OrdinalIgnoreCase) ||
             apiKey.StartsWith("raw:", StringComparison.OrdinalIgnoreCase)))
        {
            apiKey = SecretResolver.Resolve(apiKey);
        }

        var baseUri = endpoint.AbsoluteUri.TrimEnd('/');
        if (!baseUri.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            baseUri += "/v1";

        var pingUri = new Uri(baseUri + "/ping", UriKind.Absolute);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var request = new HttpRequestMessage(HttpMethod.Get, pingUri);
        if (!string.IsNullOrWhiteSpace(apiKey))
            request.Headers.TryAddWithoutValidation("OPEN-SANDBOX-API-KEY", apiKey);

        try
        {
            using var response = await http.SendAsync(request);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsNodeAvailable()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var candidates = OperatingSystem.IsWindows()
            ? new[] { "node.exe", "node.cmd", "node.bat" }
            : new[] { "node" };
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var candidate in candidates)
            {
                try
                {
                    if (File.Exists(Path.Combine(dir, candidate)))
                        return true;
                }
                catch
                {
                    // Ignore malformed PATH entries.
                }
            }
        }

        return false;
    }

    private static bool HasValidRootSet(string[] roots)
    {
        var wildcardCount = roots.Count(static root => string.Equals(root, "*", StringComparison.Ordinal));
        if (wildcardCount > 0 && roots.Length > wildcardCount)
            return false;

        foreach (var root in roots)
        {
            if (string.Equals(root, "*", StringComparison.Ordinal))
                continue;

            var resolved = ResolveConfiguredPath(root);
            if (string.IsNullOrWhiteSpace(resolved) || !Path.IsPathRooted(resolved))
                return false;
        }

        return true;
    }

    private static string ResolveConfiguredPath(string? path)
        => ConfigPathResolver.Resolve(path);
}
