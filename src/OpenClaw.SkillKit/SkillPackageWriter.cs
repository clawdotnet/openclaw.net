using System.IO.Compression;
using System.Text;
using OpenClaw.SkillKit.Abstractions;

namespace OpenClaw.SkillKit;

public sealed class SkillPackageWriter(SkillTemplateRenderer renderer)
{
    public async Task<string> CreateAsync(SkillManifest manifest, string skillsRoot, bool force, CancellationToken cancellationToken = default)
    {
        var skillDirectoryName = ValidateSinglePathSegment(manifest.Id, nameof(manifest.Id));
        var packageRoot = Path.Join(skillsRoot, skillDirectoryName);
        if (Directory.Exists(packageRoot) && !force)
            throw new IOException($"Skill already exists: {packageRoot}. Use --force to overwrite.");

        Directory.CreateDirectory(skillsRoot);
        if (Directory.Exists(packageRoot) && force)
            Directory.Delete(packageRoot, recursive: true);
        Directory.CreateDirectory(packageRoot);

        foreach (var (file, content) in renderer.RenderFiles(manifest))
            await File.WriteAllTextAsync(SkillPackageReader.ResolvePackageFilePath(packageRoot, file), content, Encoding.UTF8, cancellationToken);

        return packageRoot;
    }

    public async Task GenerateMissingAsync(SkillPackage package, bool force, CancellationToken cancellationToken = default)
    {
        foreach (var (file, content) in renderer.RenderFiles(package.Manifest))
        {
            var target = SkillPackageReader.ResolvePackageFilePath(package.RootPath, file);
            if (File.Exists(target) && !force)
                continue;

            await File.WriteAllTextAsync(target, content, Encoding.UTF8, cancellationToken);
        }
    }

    public Task<string> CreateZipAsync(SkillPackage package, string packagesRoot, bool force, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(packagesRoot);
        var packageFileName = $"{ValidateSinglePathSegment(package.Manifest.Id, nameof(package.Manifest.Id))}-{ValidateSinglePathSegment(package.Manifest.Version, nameof(package.Manifest.Version))}.zip";
        var zipPath = Path.Join(packagesRoot, packageFileName);
        if (File.Exists(zipPath))
        {
            if (!force)
                throw new IOException($"Package already exists: {zipPath}. Use --force to overwrite.");
            File.Delete(zipPath);
        }

        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var file in SkillTemplateRenderer.RequiredFiles)
        {
            var source = SkillPackageReader.ResolvePackageFilePath(package.RootPath, file);
            if (File.Exists(source))
                archive.CreateEntryFromFile(source, file, CompressionLevel.SmallestSize);
        }

        return Task.FromResult(zipPath);
    }

    private static string ValidateSinglePathSegment(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Value cannot be empty.", parameterName);
        if (Path.IsPathRooted(value) ||
            value.Contains(Path.DirectorySeparatorChar) ||
            value.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new ArgumentException($"Value must be a single path segment: {value}", parameterName);
        }

        return value;
    }
}
