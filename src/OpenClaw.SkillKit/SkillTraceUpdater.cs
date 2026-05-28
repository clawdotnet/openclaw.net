using System.Text;
using OpenClaw.SkillKit.Abstractions;

namespace OpenClaw.SkillKit;

public sealed class SkillTraceUpdater
{
    public async Task AppendAsync(SkillPackage package, string message, CancellationToken cancellationToken = default)
    {
        var tracePath = Path.Combine(package.RootPath, "trace.md");
        var line = $"- {DateTimeOffset.UtcNow:O}: {message}{Environment.NewLine}";
        if (!File.Exists(tracePath))
        {
            var renderer = new SkillTemplateRenderer();
            await File.WriteAllTextAsync(tracePath, renderer.RenderTrace(package.Manifest, "trace recreated"), Encoding.UTF8, cancellationToken);
        }

        await File.AppendAllTextAsync(tracePath, line, Encoding.UTF8, cancellationToken);
    }
}
