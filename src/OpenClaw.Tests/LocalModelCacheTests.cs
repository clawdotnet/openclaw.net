using System.Text.Json;
using OpenClaw.Cli;
using OpenClaw.Core.Models;
using OpenClaw.Core.Setup;
using Xunit;

namespace OpenClaw.Tests;

public sealed class LocalModelCacheTests : IDisposable
{
    private readonly string _tempDir;

    public LocalModelCacheTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "openclaw-local-model-cache-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void Catalog_ExposesInstallableEmbeddedGemmaPackage()
    {
        Assert.True(LocalModelPackageCatalog.TryGet("embedded-gemma-small-q4", out var package));
        Assert.NotNull(package);
        Assert.Equal("gemma-local-small-q4", package!.Id);
        Assert.Equal("embedded", package.Provider);
        Assert.Equal("gguf", package.Format);
        Assert.True(package.RequiresLicenseAcceptance);
        Assert.True(package.Capabilities.SupportsStreaming);
        Assert.True(package.Capabilities.SupportsSystemMessages);
        Assert.False(package.Capabilities.SupportsTools);
        Assert.False(package.Capabilities.SupportsStructuredOutputs);
        Assert.Equal("llama.cpp", package.Runtime.Backend);
    }

    [Fact]
    public void Catalog_ExposesGemma4PackagesWithRuntimeFilesAndDerivedPresets()
    {
        Assert.True(LocalModelPackageCatalog.TryGet("embedded-gemma-4-e4b", out var package));
        Assert.NotNull(package);
        Assert.Equal("gemma-4-e4b", package!.Id);
        Assert.Equal("Q4_K_M", package.Quantization);
        Assert.True(package.Capabilities.SupportsTools);
        Assert.True(package.Capabilities.SupportsVision);
        Assert.True(package.Capabilities.SupportsImageInput);
        Assert.True(package.Capabilities.SupportsVideoInput);
        Assert.True(package.Capabilities.SupportsAudioInput);
        Assert.True(package.Runtime.EnableJinja);
        Assert.Equal("gemma", package.Runtime.ChatTemplate);
        Assert.Equal(128000, package.Runtime.ContextSize);

        var files = LocalModelCache.GetPackageFiles(package);
        Assert.Contains(files, file => file.Role == LocalModelPackageFileRoles.Model && file.Required && !string.IsNullOrWhiteSpace(file.ExpectedSha256));
        Assert.Contains(files, file => file.Role == LocalModelPackageFileRoles.MultimodalProjector && file.Required && file.FileName.StartsWith("mmproj-", StringComparison.Ordinal));

        Assert.True(LocalModelPresetCatalog.TryGet("embedded-gemma-4-e4b", out var preset));
        Assert.NotNull(preset);
        Assert.Equal("embedded", preset!.Provider);
        Assert.Equal(package.Id, preset.PackageId);
        Assert.True(preset.Capabilities.SupportsTools);
        Assert.Equal(128000, preset.RecommendedContextTokens);
    }

    [Fact]
    public void Catalog_ExposesCorrectExperimentalLiteRtPackageMetadata()
    {
        Assert.True(LocalModelPackageCatalog.TryGet("gemma-4-litert-e2b", out var package));
        Assert.NotNull(package);
        Assert.Equal("https://huggingface.co/litert-community/gemma-4-E2B-it-litert-lm", package!.ModelPageUrl);
        Assert.Equal("gemma-4-E2B-it.litertlm", package.FileName);
        Assert.Equal("litertlm", package.Format);
        Assert.Equal("int4", package.Quantization);
        Assert.Equal("181938105e0eefd105961417e8da75903eacda102c4fce9ce90f50b97139a63c", package.ExpectedSha256);
        Assert.Equal("https://www.apache.org/licenses/LICENSE-2.0", package.LicenseUrl);
        Assert.True(package.Experimental);
        Assert.Equal(32768, package.ContextWindow);
        Assert.Equal(32768, package.Runtime.ContextSize);
        Assert.Equal("litert", package.Runtime.Backend);
        Assert.False(package.Capabilities.SupportsTools);
        Assert.False(package.Capabilities.SupportsVision);
        Assert.False(package.Capabilities.SupportsImageInput);
        Assert.False(package.Capabilities.SupportsVideoInput);
        Assert.False(package.Capabilities.SupportsAudioInput);

        var file = Assert.Single(LocalModelCache.GetPackageFiles(package));
        Assert.Equal("gemma-4-E2B-it.litertlm", file.FileName);
        Assert.Equal(package.ExpectedSha256, file.ExpectedSha256);
    }

    [Fact]
    public void ResolveConfiguredPath_ExpandsHomeDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var expected = Path.Combine(home, "openclaw-local-models");

        Assert.Equal(expected, LocalModelCache.ResolveConfiguredPath("~/openclaw-local-models"));
    }

    [Fact]
    public async Task InstallVerifyAndRemove_LocalPackage_FromManualPath()
    {
        var sourcePath = Path.Combine(_tempDir, "manual.gguf");
        await File.WriteAllTextAsync(sourcePath, "fake gguf bytes");
        var modelsRoot = Path.Combine(_tempDir, "models");
        var package = GetPackage();

        var denied = await LocalModelCache.InstallAsync(
            package,
            new LocalModelInstallRequest
            {
                SourcePath = sourcePath,
                ModelsRoot = modelsRoot
            },
            CancellationToken.None);

        Assert.False(denied.Success);
        Assert.Contains("requires explicit license acceptance", denied.Message, StringComparison.OrdinalIgnoreCase);

        var installed = await LocalModelCache.InstallAsync(
            package,
            new LocalModelInstallRequest
            {
                SourcePath = sourcePath,
                ModelsRoot = modelsRoot,
                AcceptLicense = true
            },
            CancellationToken.None);

        Assert.True(installed.Success, installed.Message);
        Assert.NotNull(installed.Status);
        Assert.True(installed.Status!.Installed);
        Assert.True(installed.Status.Verified);
        Assert.True(File.Exists(LocalModelCache.GetModelPath(package, modelsRoot)));

        var expectedSha = await LocalModelCache.ComputeSha256Async(sourcePath, CancellationToken.None);
        Assert.Equal(expectedSha, installed.Status.Sha256);

        var manifestJson = await File.ReadAllTextAsync(LocalModelCache.GetManifestPath(package, modelsRoot));
        var manifest = JsonSerializer.Deserialize(manifestJson, CoreJsonContext.Default.LocalModelInstallManifest);
        Assert.NotNull(manifest);
        Assert.True(manifest!.LicenseAccepted);
        Assert.Equal(expectedSha, manifest.Sha256);

        var verified = await LocalModelCache.VerifyAsync(package, modelsRoot, CancellationToken.None);
        Assert.True(verified.Verified);
        Assert.Equal(expectedSha, verified.Sha256);

        Assert.True(LocalModelCache.Remove(package, modelsRoot));
        Assert.False(Directory.Exists(LocalModelCache.GetPackageDirectory(package, modelsRoot)));
    }

    [Fact]
    public async Task ModelCommands_InstallVerifyRemove_UseTempModelRoot()
    {
        var sourcePath = Path.Combine(_tempDir, "cli-manual.gguf");
        await File.WriteAllTextAsync(sourcePath, "fake gguf bytes for cli");
        var modelsRoot = Path.Combine(_tempDir, "cli-models");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var installExit = await ModelCommands.RunLocalPackageCommandAsync(
            "install",
            ["gemma-local-small-q4", "--path", sourcePath, "--models-root", modelsRoot, "--accept-license"],
            output,
            error);

        var verifyExit = await ModelCommands.RunLocalPackageCommandAsync(
            "verify",
            ["gemma-local-small-q4", "--models-root", modelsRoot],
            output,
            error);

        var statusExit = await ModelCommands.RunLocalPackageCommandAsync(
            "status",
            ["gemma-local-small-q4", "--models-root", modelsRoot],
            output,
            error);

        var removeExit = await ModelCommands.RunLocalPackageCommandAsync(
            "remove",
            ["gemma-local-small-q4", "--models-root", modelsRoot],
            output,
            error);

        Assert.Equal(0, installExit);
        Assert.Equal(0, verifyExit);
        Assert.Equal(0, statusExit);
        Assert.Equal(0, removeExit);
        Assert.Equal(string.Empty, error.ToString());
        Assert.Contains("Installed gemma-local-small-q4.", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("verified=true", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Removed gemma-local-small-q4.", output.ToString(), StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private static LocalModelPackageDefinition GetPackage()
    {
        Assert.True(LocalModelPackageCatalog.TryGet("gemma-local-small-q4", out var package));
        Assert.NotNull(package);
        return package!;
    }
}
