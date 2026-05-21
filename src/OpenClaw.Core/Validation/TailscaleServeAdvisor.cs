using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using OpenClaw.Core.Models;
using OpenClaw.Core.Setup;
using OpenClaw.Core.Security;

namespace OpenClaw.Core.Validation;

public sealed class TailscaleServeProbeOptions
{
    public bool ForceInclude { get; init; }
    public bool IdentityHeadersPresent { get; init; }
    public bool CheckCli { get; init; } = true;
    public Func<string, CancellationToken, Task<TailscaleCommandResult>>? CommandRunner { get; init; }
}

public readonly record struct TailscaleCommandResult(int ExitCode, string Output, string Error)
{
    public const int CommandNotFoundExitCode = -127;
}

public static class TailscaleServeAdvisor
{
    private const string ModeName = "tailscale-serve";

    public static bool IsTailscaleServeConfigured(GatewayConfig config)
        => string.Equals(config.Deployment.Mode, ModeName, StringComparison.OrdinalIgnoreCase) ||
           string.Equals(config.Deployment.ReverseProxy, ModeName, StringComparison.OrdinalIgnoreCase) ||
           (config.Tailscale.Enabled && string.Equals(config.Tailscale.Mode, "serve", StringComparison.OrdinalIgnoreCase));

    public static string BuildLocalGatewayUrl(GatewayConfig config)
    {
        if (Uri.TryCreate(config.Deployment.ExpectedLocalUrl, UriKind.Absolute, out var expected) &&
            (expected.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
             expected.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            return expected.ToString().TrimEnd('/');
        }

        if (BindAddressClassifier.IsLoopbackBind(config.BindAddress))
            return GatewaySetupArtifacts.BuildReachableBaseUrl(config.BindAddress, config.Port);

        return $"http://127.0.0.1:{config.Port.ToString(CultureInfo.InvariantCulture)}";
    }

    public static string BuildSuggestedServeCommand(string localGatewayUrl)
        => $"tailscale serve --bg {localGatewayUrl.TrimEnd('/')}";

    public static async Task<TailscaleServeStatusResponse?> BuildStatusAsync(
        GatewayConfig config,
        TailscaleServeProbeOptions? options,
        CancellationToken ct)
    {
        options ??= new TailscaleServeProbeOptions();
        if (!options.ForceInclude && !options.IdentityHeadersPresent && !IsTailscaleServeConfigured(config))
            return null;

        var localGatewayUrl = BuildLocalGatewayUrl(config);
        var publicBind = !BindAddressClassifier.IsLoopbackBind(config.BindAddress);
        var warnings = new List<string>();
        var cliDetected = false;
        var tailnetReachability = "unknown";
        var serveDetected = "unknown";

        if (publicBind)
            warnings.Add("Gateway appears to be bound publicly. Tailscale Serve usually works best with loopback binding.");

        if (options.IdentityHeadersPresent)
            warnings.Add("Tailscale identity headers are not currently used for operator auth unless Tailscale auth is explicitly enabled.");

        if (options.CheckCli)
        {
            var runner = options.CommandRunner ?? RunTailscaleAsync;
            var status = await runner("status", ct);
            cliDetected = status.ExitCode != TailscaleCommandResult.CommandNotFoundExitCode;
            if (!cliDetected)
            {
                warnings.Add("Tailscale CLI was not found. Install Tailscale or configure Serve manually.");
            }
            else
            {
                tailnetReachability = status.ExitCode == 0 ? "ok" : "error";
                if (status.ExitCode != 0)
                    warnings.Add("Tailscale daemon status could not be confirmed. Run 'tailscale status' and verify this device is connected to the expected tailnet.");

                var serveStatus = await runner("serve status", ct);
                serveDetected = ClassifyServeStatus(serveStatus, localGatewayUrl);
                if (!string.Equals(serveDetected, "true", StringComparison.Ordinal))
                    warnings.Add("Tailscale Serve status could not be confirmed. Run 'tailscale serve status' after enabling Serve.");
            }
        }

        return new TailscaleServeStatusResponse
        {
            Mode = IsTailscaleServeConfigured(config) ? ModeName : "detected-by-request",
            LocalGatewayUrl = localGatewayUrl,
            SuggestedServeCommand = BuildSuggestedServeCommand(localGatewayUrl),
            ServeDetected = serveDetected,
            TailscaleCliDetected = cliDetected,
            TailnetReachability = tailnetReachability,
            IdentityHeadersPresent = options.IdentityHeadersPresent,
            PublicBind = publicBind,
            Warnings = warnings
        };
    }

    public static DoctorCheckItem BuildDoctorCheck(TailscaleServeStatusResponse status, bool offline)
    {
        if (offline)
        {
            return new DoctorCheckItem
            {
                Id = "tailscale_serve",
                Label = "Tailscale Serve advisory",
                Category = DoctorCheckCategories.Network,
                Status = SetupCheckStates.Skip,
                Summary = "Tailscale Serve checks were skipped because offline mode is enabled.",
                Detail = BuildStatusDetail(status),
                NextStep = "Re-run without --offline to inspect local Tailscale CLI and Serve status."
            };
        }

        if (status.Warnings.Count > 0)
        {
            return new DoctorCheckItem
            {
                Id = "tailscale_serve",
                Label = "Tailscale Serve advisory",
                Category = DoctorCheckCategories.Network,
                Status = SetupCheckStates.Warn,
                Summary = "Tailscale Serve advisory checks found non-blocking warning(s).",
                Detail = BuildStatusDetail(status),
                NextStep = "Review the Tailscale Serve deployment guide and confirm the gateway stays loopback-bound."
            };
        }

        return new DoctorCheckItem
        {
            Id = "tailscale_serve",
            Label = "Tailscale Serve advisory",
            Category = DoctorCheckCategories.Network,
            Status = SetupCheckStates.Pass,
            Summary = "Tailscale Serve advisory checks did not find blocking issues.",
            Detail = BuildStatusDetail(status)
        };
    }

    private static string BuildStatusDetail(TailscaleServeStatusResponse status)
    {
        var lines = new List<string>
        {
            $"- mode: {status.Mode}",
            $"- local_gateway: {status.LocalGatewayUrl}",
            $"- serve_detected: {status.ServeDetected}",
            $"- tailscale_cli_detected: {status.TailscaleCliDetected.ToString().ToLowerInvariant()}",
            $"- tailnet_reachability: {status.TailnetReachability}",
            $"- identity_headers_present: {status.IdentityHeadersPresent.ToString().ToLowerInvariant()}",
            $"- public_bind: {status.PublicBind.ToString().ToLowerInvariant()}",
            $"- suggested_command: {status.SuggestedServeCommand}"
        };
        foreach (var warning in status.Warnings)
            lines.Add($"- warning: {warning}");
        return string.Join(Environment.NewLine, lines);
    }

    private static string ClassifyServeStatus(TailscaleCommandResult result, string localGatewayUrl)
    {
        if (result.ExitCode != 0)
            return "unknown";

        var text = string.Join('\n', result.Output, result.Error).Trim();
        if (string.IsNullOrWhiteSpace(text))
            return "unknown";

        if (text.Contains("no serve", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("not running", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("not enabled", StringComparison.OrdinalIgnoreCase))
        {
            return "false";
        }

        if (ServeStatusTargetsLocalGateway(text, localGatewayUrl))
        {
            return "true";
        }

        return "unknown";
    }

    private static bool ServeStatusTargetsLocalGateway(string text, string localGatewayUrl)
    {
        if (text.Contains(localGatewayUrl, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!Uri.TryCreate(localGatewayUrl, UriKind.Absolute, out var localGateway) ||
            localGateway.Port <= 0)
        {
            return false;
        }

        var port = localGateway.Port.ToString(CultureInfo.InvariantCulture);
        var targets = new[]
        {
            $"127.0.0.1:{port}",
            $"localhost:{port}",
            $"[::1]:{port}"
        };

        return targets.Any(target => text.Contains(target, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<TailscaleCommandResult> RunTailscaleAsync(string arguments, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "tailscale",
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        Task<string>? stdoutTask = null;
        Task<string>? stderrTask = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));

            process.Start();
            stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            return new TailscaleCommandResult(process.ExitCode, stdout, stderr);
        }
        catch (Win32Exception)
        {
            return new TailscaleCommandResult(TailscaleCommandResult.CommandNotFoundExitCode, string.Empty, "tailscale command not found");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await KillProcessAsync(process);
            await ObserveReadTaskAsync(stdoutTask);
            await ObserveReadTaskAsync(stderrTask);
            return new TailscaleCommandResult(124, string.Empty, "tailscale command timed out");
        }
        catch (InvalidOperationException ex)
        {
            return new TailscaleCommandResult(1, string.Empty, ex.Message);
        }
        catch (IOException ex)
        {
            return new TailscaleCommandResult(1, string.Empty, ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new TailscaleCommandResult(1, string.Empty, ex.Message);
        }
        finally
        {
            await KillProcessAsync(process);
        }
    }

    private static async Task KillProcessAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
        catch (ObjectDisposedException)
        {
            // Best-effort cleanup for advisory CLI probes.
        }
        catch (InvalidOperationException)
        {
            // Best-effort cleanup for advisory CLI probes.
        }
        catch (Win32Exception)
        {
            // Best-effort cleanup for advisory CLI probes.
        }
        catch (NotSupportedException)
        {
            // Best-effort cleanup for advisory CLI probes.
        }
    }

    private static async Task ObserveReadTaskAsync(Task<string>? task)
    {
        if (task is null)
            return;

        try
        {
            _ = await task;
        }
        catch (OperationCanceledException)
        {
            // Best-effort cleanup for advisory CLI probes.
        }
        catch (IOException)
        {
            // Best-effort cleanup for advisory CLI probes.
        }
        catch (ObjectDisposedException)
        {
            // Best-effort cleanup for advisory CLI probes.
        }
        catch (InvalidOperationException)
        {
            // Best-effort cleanup for advisory CLI probes.
        }
    }
}
