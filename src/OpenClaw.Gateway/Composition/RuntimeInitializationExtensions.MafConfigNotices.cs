using Microsoft.Extensions.Options;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using OpenClaw.MicrosoftAgentFrameworkAdapter;

namespace OpenClaw.Gateway.Composition;

internal static partial class RuntimeInitializationExtensions
{
    private static void RecordLegacyMafConfigNotice(
        WebApplication app,
        RuntimeServices services,
        ILogger startupLogger,
        IStartupNoticeSink startupNoticeSink)
    {
        var options = app.Services.GetService<IOptions<MafOptions>>()?.Value;
        if (options?.LegacySectionUsed != true)
            return;

        var message =
            $"Deprecated config section '{MafOptions.LegacySectionName}' is still supported for this release; move settings to '{MafOptions.SectionName}'.";
        startupLogger.LogWarning("{Message}", message);
        startupNoticeSink.Record(message);
        services.RuntimeEventStore.Append(new RuntimeEventEntry
        {
            Id = $"evt_{Guid.NewGuid():N}"[..20],
            Component = "configuration",
            Action = "deprecated_maf_section",
            Severity = "warning",
            Summary = message,
            Metadata = new Dictionary<string, string>
            {
                ["legacySection"] = MafOptions.LegacySectionName,
                ["replacementSection"] = MafOptions.SectionName
            }
        });
    }
}
