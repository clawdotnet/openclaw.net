using OpenClaw.Core.Setup;

namespace OpenClaw.Cli;

internal static class ModelCommands
{
    public static async Task<int> RunLocalPackageCommandAsync(
        string subcommand,
        string[] args,
        TextWriter? output = null,
        TextWriter? error = null)
    {
        output ??= Console.Out;
        error ??= Console.Error;
        var packageId = GetPositionals(args).FirstOrDefault();
        var parsed = CliArgs.Parse(args);
        var modelsRoot = parsed.GetOption("--models-root");

        switch (subcommand)
        {
            case "packages":
                foreach (var package in LocalModelPackageCatalog.List())
                {
                    output.WriteLine($"- {package.Id} | preset={package.PresetId} | model={package.ModelId} | {package.DisplayName}");
                    output.WriteLine($"  backend={package.Runtime.Backend} format={package.Format} quant={package.Quantization} context={package.ContextWindow} experimental={ToBool(package.Experimental)} ram={package.MinRamGb}-{package.RecommendedRamGb}GB license={package.LicenseUrl ?? "n/a"}");
                    if (string.Equals(package.Runtime.Backend, "litert", StringComparison.OrdinalIgnoreCase))
                        output.WriteLine("  prerequisite=OpenClaw:LocalInference:LiteRtRuntimePath must point to an OpenClaw-compatible LiteRT adapter binary");
                    foreach (var file in LocalModelCache.GetPackageFiles(package))
                        output.WriteLine($"  file[{file.Role}] {file.FileName} required={ToBool(file.Required)} sha256={(string.IsNullOrWhiteSpace(file.ExpectedSha256) ? "manifest" : file.ExpectedSha256)}");
                }
                return 0;

            case "status":
                if (string.IsNullOrWhiteSpace(packageId))
                {
                    foreach (var status in LocalModelCache.ListStatuses(modelsRoot))
                        PrintStatus(status, output);
                    return 0;
                }

                if (!TryGetPackage(packageId, error, out var statusPackage))
                    return 2;
                PrintStatus(LocalModelCache.GetStatus(statusPackage!, modelsRoot), output);
                return 0;

            case "install":
                if (!TryGetPackage(packageId, error, out var installPackage))
                    return 2;

                var install = await LocalModelCache.InstallAsync(
                    installPackage!,
                    new LocalModelInstallRequest
                    {
                        SourcePath = parsed.GetOption("--path"),
                        MultimodalProjectorPath = parsed.GetOption("--mmproj-path"),
                        DraftModelPath = parsed.GetOption("--draft-path"),
                        SourceUrl = parsed.GetOption("--download-url"),
                        BearerToken = parsed.GetOption("--token"),
                        AcceptLicense = parsed.HasFlag("--accept-license"),
                        ModelsRoot = modelsRoot,
                        DownloadOptionalFiles = !parsed.HasFlag("--no-optional-files")
                    },
                    CancellationToken.None);
                output.WriteLine(install.Message);
                if (install.Status is not null)
                    PrintStatus(install.Status, output);
                return install.Success ? 0 : 1;

            case "verify":
                if (!TryGetPackage(packageId, error, out var verifyPackage))
                    return 2;
                var verified = await LocalModelCache.VerifyAsync(verifyPackage!, modelsRoot, CancellationToken.None);
                PrintStatus(verified, output);
                return verified.Verified ? 0 : 1;

            case "remove":
                if (!TryGetPackage(packageId, error, out var removePackage))
                    return 2;
                var removed = LocalModelCache.Remove(removePackage!, modelsRoot);
                output.WriteLine(removed ? $"Removed {removePackage!.Id}." : $"{removePackage!.Id} was not installed.");
                return 0;

            default:
                return 2;
        }
    }

    private static bool TryGetPackage(
        string? packageId,
        TextWriter error,
        out OpenClaw.Core.Models.LocalModelPackageDefinition? package)
    {
        if (!string.IsNullOrWhiteSpace(packageId) && LocalModelPackageCatalog.TryGet(packageId, out package))
            return true;

        package = null;
        error.WriteLine(string.IsNullOrWhiteSpace(packageId)
            ? "Package id is required."
            : $"Unknown local model package: {packageId}");
        return false;
    }

    private static void PrintStatus(OpenClaw.Core.Models.LocalModelPackageStatus status, TextWriter output)
    {
        output.WriteLine($"- {status.PackageId} | installed={ToBool(status.Installed)} | verified={ToBool(status.Verified)} | model={status.ModelId}");
        if (LocalModelPackageCatalog.TryGet(status.PackageId, out var package) && package is not null)
        {
            output.WriteLine($"  backend={package.Runtime.Backend} format={package.Format} context={package.ContextWindow} experimental={ToBool(package.Experimental)}");
            if (string.Equals(package.Runtime.Backend, "litert", StringComparison.OrdinalIgnoreCase))
                output.WriteLine("  prerequisite=OpenClaw:LocalInference:LiteRtRuntimePath must point to an OpenClaw-compatible LiteRT adapter binary");
        }
        output.WriteLine($"  path={status.ModelPath ?? "n/a"}");
        if (!string.IsNullOrWhiteSpace(status.Sha256))
            output.WriteLine($"  sha256={status.Sha256}");
        foreach (var file in status.Files)
        {
            output.WriteLine($"  file[{file.Role}] installed={ToBool(file.Installed)} verified={ToBool(file.Verified)} required={ToBool(file.Required)} path={file.Path ?? "n/a"}");
            if (!string.IsNullOrWhiteSpace(file.Sha256))
                output.WriteLine($"    sha256={file.Sha256}");
            if (!string.IsNullOrWhiteSpace(file.Issue))
                output.WriteLine($"    issue={file.Issue}");
        }
        if (!string.IsNullOrWhiteSpace(status.Issue))
            output.WriteLine($"  issue={status.Issue}");
    }

    private static string ToBool(bool value) => value ? "true" : "false";

    private static IReadOnlyList<string> GetPositionals(string[] args)
    {
        var result = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                result.Add(arg);
                continue;
            }

            if (arg is "--accept-license" or "--no-optional-files")
                continue;

            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                i++;
        }

        return result;
    }
}
