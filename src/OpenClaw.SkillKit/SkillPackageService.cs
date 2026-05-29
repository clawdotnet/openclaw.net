using System.Text;
using OpenClaw.SkillKit.Abstractions;

namespace OpenClaw.SkillKit;

public sealed class SkillPackageService(
    SkillTemplateRenderer renderer,
    SkillPackageReader reader,
    SkillPackageWriter writer,
    SkillValidator validator,
    ISkillCritiqueProvider critiqueProvider,
    SkillRunPlanner runPlanner,
    SkillTraceUpdater traceUpdater)
{
    public static SkillPackageService CreateDefault()
    {
        var renderer = new SkillTemplateRenderer();
        var reader = new SkillPackageReader();
        return new SkillPackageService(
            renderer,
            reader,
            new SkillPackageWriter(renderer),
            new SkillValidator(),
            new DeterministicSkillCritiqueProvider(),
            new SkillRunPlanner(),
            new SkillTraceUpdater());
    }

    public async Task<SkillPackage> CreateNewAsync(string name, string category, string template, string skillsRoot, bool force, CancellationToken cancellationToken = default)
    {
        var manifest = renderer.CreateManifest(name, category, template);
        var root = await writer.CreateAsync(manifest, skillsRoot, force, cancellationToken);
        return await reader.ReadAsync(root, skillsRoot, cancellationToken);
    }

    public Task<IReadOnlyList<SkillPackage>> ListAsync(string skillsRoot, CancellationToken cancellationToken = default) =>
        reader.ListAsync(skillsRoot, cancellationToken);

    public Task<SkillPackage> ReadAsync(string skillRef, string skillsRoot, CancellationToken cancellationToken = default) =>
        reader.ReadAsync(skillRef, skillsRoot, cancellationToken);

    public Task<SkillValidationResult> ValidateAsync(string skillRef, string skillsRoot, CancellationToken cancellationToken = default) =>
        validator.ValidateAsync(skillRef, skillsRoot, cancellationToken);

    public async Task GenerateAsync(string skillRef, string skillsRoot, bool force, CancellationToken cancellationToken = default)
    {
        var package = await reader.ReadAsync(skillRef, skillsRoot, cancellationToken);
        await writer.GenerateMissingAsync(package, force, cancellationToken);
        await traceUpdater.AppendAsync(package, force ? "Regenerated skill files with force." : "Generated missing skill files.", cancellationToken);
    }

    public async Task<string> PackageAsync(string skillRef, string skillsRoot, string packagesRoot, bool force, CancellationToken cancellationToken = default)
    {
        var package = await reader.ReadAsync(skillRef, skillsRoot, cancellationToken);
        var zipPath = await writer.CreateZipAsync(package, packagesRoot, force, cancellationToken);
        await traceUpdater.AppendAsync(package, $"Packaged skill as {zipPath}.", cancellationToken);
        return zipPath;
    }

    public async Task<string> CritiqueAsync(string skillRef, string skillsRoot, CancellationToken cancellationToken = default)
    {
        var package = await reader.ReadAsync(skillRef, skillsRoot, cancellationToken);
        var critique = await critiqueProvider.CritiqueAsync(package, cancellationToken);
        var path = SkillPackageReader.ResolvePackageFilePath(package.RootPath, "critique.md");
        await File.WriteAllTextAsync(path, critique.Markdown, Encoding.UTF8, cancellationToken);
        await traceUpdater.AppendAsync(package, "Generated deterministic critique.", cancellationToken);
        return path;
    }

    public async Task<SkillRunPlan> PlanRunAsync(string skillRef, string skillsRoot, IReadOnlyList<string> inputPaths, CancellationToken cancellationToken = default)
    {
        var package = await reader.ReadAsync(skillRef, skillsRoot, cancellationToken);
        return runPlanner.Plan(package, inputPaths);
    }
}
