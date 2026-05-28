using System.Text;
using OpenClaw.SkillKit.Abstractions;

namespace OpenClaw.SkillKit;

public sealed class SkillPackageReader
{
    public async Task<SkillPackage> ReadAsync(string skillRef, string skillsRoot, CancellationToken cancellationToken = default)
    {
        var packageRoot = ResolveSkillPath(skillRef, skillsRoot);
        var manifestPath = Path.Combine(packageRoot, "skill.yaml");
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException($"Skill manifest not found: {manifestPath}", manifestPath);

        var manifest = await SkillManifestSerializer.ReadAsync(manifestPath, cancellationToken);
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in SkillTemplateRenderer.RequiredFiles)
        {
            var path = Path.Combine(packageRoot, file);
            if (File.Exists(path))
                files[file] = await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken);
        }

        return new SkillPackage
        {
            RootPath = packageRoot,
            Manifest = manifest,
            Files = files
        };
    }

    public async Task<IReadOnlyList<SkillPackage>> ListAsync(string skillsRoot, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(skillsRoot))
            return [];

        var packages = new List<SkillPackage>();
        foreach (var directory in Directory.EnumerateDirectories(skillsRoot).Order(StringComparer.OrdinalIgnoreCase))
        {
            var manifestPath = Path.Combine(directory, "skill.yaml");
            if (!File.Exists(manifestPath))
                continue;

            try
            {
                packages.Add(await ReadAsync(directory, skillsRoot, cancellationToken));
            }
            catch (IOException)
            {
            }
            catch (InvalidDataException)
            {
            }
        }

        return packages;
    }

    public static string ResolveSkillPath(string skillRef, string skillsRoot)
    {
        if (string.IsNullOrWhiteSpace(skillRef))
            throw new ArgumentException("Skill id or path is required.", nameof(skillRef));

        var fullRef = Path.GetFullPath(skillRef);
        if (Directory.Exists(fullRef) || File.Exists(Path.Combine(fullRef, "skill.yaml")))
            return fullRef;

        return Path.GetFullPath(Path.Combine(skillsRoot, skillRef));
    }
}
