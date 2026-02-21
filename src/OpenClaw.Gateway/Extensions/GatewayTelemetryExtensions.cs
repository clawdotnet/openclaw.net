using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using OpenClaw.Core.Observability;

namespace OpenClaw.Gateway.Extensions;

public static class GatewayTelemetryExtensions
{
    /// <summary>
    /// Configures OpenTelemetry logging, metrics, and distributed tracing.
    /// OTLP exporter is enabled by default.
    /// </summary>
    public static void AddGatewayTelemetry(this WebApplicationBuilder builder)
    {
        var resourceBuilder = ResourceBuilder.CreateDefault()
            .AddService(serviceName: Telemetry.ServiceName, serviceVersion: Telemetry.Version);

        // Configure Logging to use OpenTelemetry
        builder.Logging.AddOpenTelemetry(options =>
        {
            options.IncludeScopes = true;
            options.IncludeFormattedMessage = true;
            options.SetResourceBuilder(resourceBuilder);
            options.AddOtlpExporter();
        });

        // Configure Metrics and Tracing
        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                metrics.SetResourceBuilder(resourceBuilder)
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddMeter("OpenClaw.Gateway") // Custom application metrics
                    .AddOtlpExporter();
            })
            .WithTracing(tracing =>
            {
                tracing.SetResourceBuilder(resourceBuilder)
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddSource(Telemetry.ServiceName) // Traces from our custom ActivitySource
                    .AddOtlpExporter();
            });
    }
}
