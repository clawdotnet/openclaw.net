using System.Diagnostics;

namespace OpenClaw.Core.Observability;

/// <summary>
/// Provides centralized OpenTelemetry instances (ActivitySource, Meters) for the OpenClaw application.
/// </summary>
public static class Telemetry
{
    public const string ServiceName = "OpenClaw.Gateway";
    public const string Version = "1.0.0";

    /// <summary>
    /// The primary ActivitySource for distributed tracing within OpenClaw.
    /// </summary>
    public static readonly ActivitySource ActivitySource = new(ServiceName, Version);
}
