using System.Text;
using OpenClaw.SkillKit.Abstractions;

namespace OpenClaw.SkillKit;

public sealed class SkillTraceUpdater
{
    public async Task AppendAsync(SkillPackage package, string message, CancellationToken cancellationToken = default)
    {
        var tracePath = SkillPackageReader.ResolvePackageFilePath(package.RootPath, "trace.md");
        var sanitizedMessage = SanitizeTraceMessage(message);
        var line = $"- {DateTimeOffset.UtcNow:O}: {sanitizedMessage}{Environment.NewLine}";
        if (!File.Exists(tracePath))
        {
            var renderer = new SkillTemplateRenderer();
            await File.WriteAllTextAsync(tracePath, renderer.RenderTrace(package.Manifest, "trace recreated"), Encoding.UTF8, cancellationToken);
        }

        await File.AppendAllTextAsync(tracePath, line, Encoding.UTF8, cancellationToken);
    }

    private static string SanitizeTraceMessage(string message)
    {
        var builder = new StringBuilder(message.Length);
        foreach (var ch in message)
            builder.Append(char.IsControl(ch) ? ' ' : ch);

        return builder.ToString().Trim();
    }
}
