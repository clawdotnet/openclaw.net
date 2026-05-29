using System.Diagnostics;
using System.Text;
using OpenClaw.SkillKit.Abstractions;

namespace OpenClaw.SkillKit;

public sealed class SkillPackageReader
{
    public async Task<SkillPackage> ReadAsync(string skillRef, string skillsRoot, CancellationToken cancellationToken = default)
    {
        var packageRoot = ResolveSkillPath(skillRef, skillsRoot);
        var manifestPath = Path.Join(packageRoot, "skill.yaml");
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException($"Skill manifest not found: {manifestPath}", manifestPath);

        var manifest = await SkillManifestSerializer.ReadAsync(manifestPath, cancellationToken);
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in SkillTemplateRenderer.RequiredFiles)
        {
            var path = ResolvePackageFilePath(packageRoot, file);
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
        foreach (var directory in EnumerateSkillDirectories(skillsRoot))
        {
            var manifestPath = Path.Join(directory, "skill.yaml");
            if (!File.Exists(manifestPath))
                continue;

            try
            {
                packages.Add(await ReadAsync(directory, skillsRoot, cancellationToken));
            }
            catch (IOException ex)
            {
                Trace.TraceWarning("Skipping skill package at '{0}' because it could not be read: {1}", directory, ex.Message);
            }
            catch (InvalidDataException ex)
            {
                Trace.TraceWarning("Skipping skill package at '{0}' because it is invalid: {1}", directory, ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                Trace.TraceWarning("Skipping skill package at '{0}' because access was denied: {1}", directory, ex.Message);
            }
        }

        return packages;
    }

    private static IReadOnlyList<string> EnumerateSkillDirectories(string skillsRoot)
    {
        try
        {
            return Directory.EnumerateDirectories(skillsRoot)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    public static string ResolveSkillPath(string skillRef, string skillsRoot)
    {
        if (string.IsNullOrWhiteSpace(skillRef))
            throw new ArgumentException("Skill id or path is required.", nameof(skillRef));

        var fullRef = Path.GetFullPath(skillRef);
        if (Directory.Exists(fullRef) || File.Exists(Path.Join(fullRef, "skill.yaml")))
            return fullRef;

        if (Path.IsPathRooted(skillRef))
            return fullRef;

        return Path.GetFullPath(Path.Join(skillsRoot, skillRef));
    }

    internal static string ResolvePackageFilePath(string packageRoot, string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || Path.IsPathRooted(fileName))
            throw new InvalidDataException($"Skill package file name must be relative: {fileName}");

        var fullRoot = Path.GetFullPath(packageRoot);
        var fullPath = Path.GetFullPath(Path.Join(fullRoot, fileName));
        var relative = Path.GetRelativePath(fullRoot, fullPath);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new InvalidDataException($"Skill package file escapes package root: {fileName}");

        return fullPath;
    }
}
