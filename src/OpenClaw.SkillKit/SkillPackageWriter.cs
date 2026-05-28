using System.IO.Compression;
using System.Text;
using OpenClaw.SkillKit.Abstractions;

namespace OpenClaw.SkillKit;

public sealed class SkillPackageWriter(SkillTemplateRenderer renderer)
{
    public async Task<string> CreateAsync(SkillManifest manifest, string skillsRoot, bool force, CancellationToken cancellationToken = default)
    {
        var packageRoot = Path.Combine(skillsRoot, manifest.Id);
        if (Directory.Exists(packageRoot) && !force)
            throw new IOException($"Skill already exists: {packageRoot}. Use --force to overwrite.");

        Directory.CreateDirectory(skillsRoot);
        if (Directory.Exists(packageRoot) && force)
            Directory.Delete(packageRoot, recursive: true);
        Directory.CreateDirectory(packageRoot);

        foreach (var (file, content) in renderer.RenderFiles(manifest))
            await File.WriteAllTextAsync(Path.Combine(packageRoot, file), content, Encoding.UTF8, cancellationToken);

        return packageRoot;
    }

    public async Task GenerateMissingAsync(SkillPackage package, bool force, CancellationToken cancellationToken = default)
    {
        foreach (var (file, content) in renderer.RenderFiles(package.Manifest))
        {
            var target = Path.Combine(package.RootPath, file);
            if (File.Exists(target) && !force)
                continue;

            await File.WriteAllTextAsync(target, content, Encoding.UTF8, cancellationToken);
        }
    }

    public Task<string> CreateZipAsync(SkillPackage package, string packagesRoot, bool force, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(packagesRoot);
        var zipPath = Path.Combine(packagesRoot, $"{package.Manifest.Id}-{package.Manifest.Version}.zip");
        if (File.Exists(zipPath))
        {
            if (!force)
                throw new IOException($"Package already exists: {zipPath}. Use --force to overwrite.");
            File.Delete(zipPath);
        }

        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var file in SkillTemplateRenderer.RequiredFiles)
        {
            var source = Path.Combine(package.RootPath, file);
            if (File.Exists(source))
                archive.CreateEntryFromFile(source, file, CompressionLevel.SmallestSize);
        }

        return Task.FromResult(zipPath);
    }
}
