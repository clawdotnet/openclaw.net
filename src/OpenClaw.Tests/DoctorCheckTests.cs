using System.Text.Json;
using OpenClaw.Core.Models;
using OpenClaw.Core.Validation;
using Xunit;

namespace OpenClaw.Tests;

public sealed class DoctorCheckTests
{
    [Fact]
    public async Task RunAsync_Ipv6LoopbackDoesNotRequirePublicBindAuthToken()
    {
        var storagePath = Path.Combine(Path.GetTempPath(), "openclaw-doctor-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var config = new GatewayConfig
            {
                BindAddress = "::1",
                Port = 18789,
                Memory = new MemoryConfig
                {
                    StoragePath = storagePath
                },
                Llm = new LlmProviderConfig
                {
                    Provider = "ollama",
                    Model = "llama3.2"
                }
            };

            var runtimeState = RuntimeModeResolver.Resolve(config.Runtime);

            var ready = await DoctorCheck.RunAsync(config, runtimeState);

            Assert.True(ready);
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task BuildDoctorReport_IncludesConfigSourceDiagnosticsWithoutSecrets()
    {
        var storagePath = Path.Combine(Path.GetTempPath(), "openclaw-doctor-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var config = new GatewayConfig
            {
                Memory = new MemoryConfig
                {
                    StoragePath = storagePath
                },
                Llm = new LlmProviderConfig
                {
                    Provider = "openai",
                    Model = "gpt-4o",
                    ApiKey = "sk-test-secret"
                }
            };

            var report = await SetupVerificationService.BuildDoctorReportAsync(new DoctorReportRequest
            {
                Config = config,
                RuntimeState = RuntimeModeResolver.Resolve(config.Runtime),
                Offline = true,
                RequireProvider = false,
                CheckPortAvailability = false,
                ConfigSources = new ConfigSourceDiagnostics
                {
                    Items =
                    [
                        new ConfigSourceDiagnosticItem
                        {
                            Label = "API key",
                            Key = "OpenClaw:Llm:ApiKey",
                            EffectiveValue = "sk-test-secret",
                            Source = "environment variable MODEL_PROVIDER_KEY",
                            Redacted = true
                        }
                    ]
                }
            }, CancellationToken.None);

            var text = SetupVerificationService.RenderDoctorText(report);

            Assert.Contains("config/config_sources", text, StringComparison.Ordinal);
            Assert.Contains("API key: configured (redacted)", text, StringComparison.Ordinal);
            Assert.Contains("MODEL_PROVIDER_KEY", text, StringComparison.Ordinal);
            Assert.DoesNotContain("sk-test-secret", text, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task BuildDoctorReport_TailscaleServeOfflineMode_AddsAdvisorySkip()
    {
        var storagePath = Path.Combine(Path.GetTempPath(), "openclaw-doctor-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var config = new GatewayConfig
            {
                BindAddress = "127.0.0.1",
                Port = 18789,
                Deployment = new DeploymentConfig
                {
                    Mode = "tailscale-serve",
                    PublicExposure = false,
                    ReverseProxy = "tailscale-serve",
                    ExpectedLocalUrl = "http://127.0.0.1:18789"
                },
                Memory = new MemoryConfig
                {
                    StoragePath = storagePath
                },
                Llm = new LlmProviderConfig
                {
                    Provider = "ollama",
                    Model = "llama3.2"
                }
            };

            var report = await SetupVerificationService.BuildDoctorReportAsync(new DoctorReportRequest
            {
                Config = config,
                RuntimeState = RuntimeModeResolver.Resolve(config.Runtime),
                Offline = true,
                RequireProvider = false,
                CheckPortAvailability = false
            }, CancellationToken.None);

            var tailscale = Assert.Single(report.Checks, static item => item.Id == "tailscale_serve");
            Assert.Equal(SetupCheckStates.Skip, tailscale.Status);
            Assert.Contains("offline mode", tailscale.Summary, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task TailscaleServeAdvisor_MissingCli_IsAdvisoryAndSerializable()
    {
        var config = new GatewayConfig
        {
            Deployment = new DeploymentConfig
            {
                Mode = "tailscale-serve",
                PublicExposure = false,
                ReverseProxy = "tailscale-serve",
                ExpectedLocalUrl = "http://127.0.0.1:18789"
            }
        };

        var status = await TailscaleServeAdvisor.BuildStatusAsync(
            config,
            new TailscaleServeProbeOptions
            {
                CommandRunner = (_, _) => Task.FromResult(new TailscaleCommandResult(
                    TailscaleCommandResult.CommandNotFoundExitCode,
                    string.Empty,
                    "missing"))
            },
            CancellationToken.None);

        Assert.NotNull(status);
        Assert.False(status!.TailscaleCliDetected);
        Assert.Equal("unknown", status.TailnetReachability);
        Assert.Contains(status.Warnings, warning => warning.Contains("Tailscale CLI was not found", StringComparison.Ordinal));

        var json = JsonSerializer.Serialize(status, CoreJsonContext.Default.TailscaleServeStatusResponse);
        var roundTripped = JsonSerializer.Deserialize(json, CoreJsonContext.Default.TailscaleServeStatusResponse);
        Assert.Equal("tailscale-serve", roundTripped!.Mode);
        Assert.Equal("tailscale serve --bg http://127.0.0.1:18789", roundTripped.SuggestedServeCommand);
    }
}
