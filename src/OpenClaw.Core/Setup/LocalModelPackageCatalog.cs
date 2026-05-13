using System.Security.Cryptography;
using System.Text.Json;
using OpenClaw.Core.Models;

namespace OpenClaw.Core.Setup;

public sealed class LocalModelInstallRequest
{
    public string? SourcePath { get; init; }
    public string? MultimodalProjectorPath { get; init; }
    public string? DraftModelPath { get; init; }
    public string? SourceUrl { get; init; }
    public string? BearerToken { get; init; }
    public bool AcceptLicense { get; init; }
    public string? ModelsRoot { get; init; }
    public bool DownloadOptionalFiles { get; init; } = true;
}

public sealed class LocalModelInstallResult
{
    public bool Success { get; init; }
    public required string Message { get; init; }
    public LocalModelPackageStatus? Status { get; init; }
}

public static class LocalModelPackageCatalog
{
    private static readonly LocalModelPackageDefinition[] Packages =
    [
        new()
        {
            Id = "gemma-local-small-q4",
            PresetId = "embedded-gemma-small-q4",
            DisplayName = "Gemma 3 4B IT QAT Q4",
            Description = "Instruction-tuned Gemma GGUF package for OpenClaw embedded local mode.",
            Provider = "embedded",
            ModelId = "gemma-local-small-q4",
            Family = "gemma",
            Format = "gguf",
            Quantization = "Q4_0",
            FileName = "gemma-3-4b-it-q4_0.gguf",
            DownloadUrl = "https://huggingface.co/google/gemma-3-4b-it-qat-q4_0-gguf/resolve/main/gemma-3-4b-it-q4_0.gguf",
            ModelPageUrl = "https://huggingface.co/google/gemma-3-4b-it-qat-q4_0-gguf",
            LicenseUrl = "https://ai.google.dev/gemma/terms",
            RequiresLicenseAcceptance = true,
            RequiresDownloadToken = true,
            MinRamGb = 8,
            RecommendedRamGb = 16,
            ContextWindow = 4096,
            MaxOutputTokens = 1024,
            Tags = ["local", "private", "offline", "cheap"],
            Capabilities = new ModelCapabilities
            {
                SupportsTools = false,
                SupportsVision = false,
                SupportsJsonSchema = false,
                SupportsStructuredOutputs = false,
                SupportsStreaming = true,
                SupportsParallelToolCalls = false,
                SupportsReasoningEffort = false,
                SupportsSystemMessages = true,
                SupportsImageInput = false,
                SupportsAudioInput = false,
                MaxContextTokens = 4096,
                MaxOutputTokens = 1024
            },
            Runtime = new LocalModelRuntimeDefaults
            {
                Backend = "llama.cpp",
                Threads = "auto",
                GpuLayers = "auto",
                ContextSize = 4096
            }
        },
        new()
        {
            Id = "gemma-4-e2b",
            PresetId = "embedded-gemma-4-e2b",
            DisplayName = "Gemma 4 E2B Q8",
            Description = "Gemma 4 E2B instruction-tuned GGUF package for ultra-mobile/edge multimodal local inference.",
            Provider = "embedded",
            ModelId = "gemma-4-e2b",
            Family = "gemma",
            Format = "gguf",
            Quantization = "Q8_0",
            FileName = "gemma-4-E2B-it-Q8_0.gguf",
            DownloadUrl = "https://huggingface.co/ggml-org/gemma-4-E2B-it-GGUF/resolve/main/gemma-4-E2B-it-Q8_0.gguf",
            ExpectedSha256 = "e049411c01fb7a81161768c52e38828970e55a64e22738957adcbe51d20f1c8e",
            ModelPageUrl = "https://huggingface.co/ggml-org/gemma-4-E2B-it-GGUF",
            LicenseUrl = "https://ai.google.dev/gemma/terms",
            RequiresLicenseAcceptance = true,
            MinRamGb = 4,
            RecommendedRamGb = 8,
            ContextWindow = 128000,
            MaxOutputTokens = 4096,
            Tags = ["local", "private", "offline", "cheap", "gemma4"],
            Capabilities = new ModelCapabilities
            {
                SupportsTools = true,
                SupportsVision = true,
                SupportsJsonSchema = false,
                SupportsStructuredOutputs = false,
                SupportsStreaming = true,
                SupportsParallelToolCalls = true,
                SupportsReasoningEffort = true,
                SupportsSystemMessages = true,
                SupportsImageInput = true,
                SupportsVideoInput = true,
                SupportsAudioInput = true,
                MaxContextTokens = 128000,
                MaxOutputTokens = 4096
            },
            Files =
            [
                new()
                {
                    Role = LocalModelPackageFileRoles.Model,
                    FileName = "gemma-4-E2B-it-Q8_0.gguf",
                    DownloadUrl = "https://huggingface.co/ggml-org/gemma-4-E2B-it-GGUF/resolve/main/gemma-4-E2B-it-Q8_0.gguf",
                    ExpectedSha256 = "e049411c01fb7a81161768c52e38828970e55a64e22738957adcbe51d20f1c8e",
                    Required = true,
                    InstallByDefault = true
                },
                new()
                {
                    Role = LocalModelPackageFileRoles.MultimodalProjector,
                    FileName = "mmproj-gemma-4-E2B-it-Q8_0.gguf",
                    DownloadUrl = "https://huggingface.co/ggml-org/gemma-4-E2B-it-GGUF/resolve/main/mmproj-gemma-4-E2B-it-Q8_0.gguf",
                    ExpectedSha256 = "8a82e0fd831bb7cb5c8898b86393eb14042986b950a60e1034bf21d061aac8a8",
                    Required = true,
                    InstallByDefault = true
                }
            ],
            Runtime = new LocalModelRuntimeDefaults
            {
                Backend = "llama.cpp",
                Threads = "auto",
                GpuLayers = "auto",
                ContextSize = 128000,
                EnableJinja = true,
                ChatTemplate = "gemma",
                MultimodalProjectorFileName = "mmproj-gemma-4-E2B-it-Q8_0.gguf",
                ReasoningMode = "auto"
            }
        },
        new()
        {
            Id = "gemma-4-litert-e2b",
            PresetId = "embedded-gemma-4-litert-e2b",
            DisplayName = "Gemma 4 E2B LiteRT",
            Description = "Experimental Gemma 4 E2B LiteRT-LM package for edge adapters.",
            Provider = "embedded",
            ModelId = "gemma-4-litert-e2b",
            Family = "gemma",
            Format = "litertlm",
            Quantization = "int4",
            FileName = "gemma-4-E2B-it.litertlm",
            DownloadUrl = "https://huggingface.co/litert-community/gemma-4-E2B-it-litert-lm/resolve/main/gemma-4-E2B-it.litertlm",
            ExpectedSha256 = "181938105e0eefd105961417e8da75903eacda102c4fce9ce90f50b97139a63c",
            ModelPageUrl = "https://huggingface.co/litert-community/gemma-4-E2B-it-litert-lm",
            LicenseUrl = "https://www.apache.org/licenses/LICENSE-2.0",
            Experimental = true,
            MinRamGb = 4,
            RecommendedRamGb = 8,
            ContextWindow = 32768,
            MaxOutputTokens = 4096,
            Tags = ["local", "private", "offline", "edge", "gemma4", "litert", "experimental"],
            Capabilities = new ModelCapabilities
            {
                SupportsTools = false,
                SupportsVision = false,
                SupportsJsonSchema = false,
                SupportsStructuredOutputs = false,
                SupportsStreaming = true,
                SupportsParallelToolCalls = false,
                SupportsReasoningEffort = false,
                SupportsSystemMessages = true,
                SupportsImageInput = false,
                SupportsVideoInput = false,
                SupportsAudioInput = false,
                MaxContextTokens = 32768,
                MaxOutputTokens = 4096
            },
            Files =
            [
                new()
                {
                    Role = LocalModelPackageFileRoles.Model,
                    FileName = "gemma-4-E2B-it.litertlm",
                    DownloadUrl = "https://huggingface.co/litert-community/gemma-4-E2B-it-litert-lm/resolve/main/gemma-4-E2B-it.litertlm",
                    ExpectedSha256 = "181938105e0eefd105961417e8da75903eacda102c4fce9ce90f50b97139a63c",
                    Required = true,
                    InstallByDefault = true
                }
            ],
            Runtime = new LocalModelRuntimeDefaults
            {
                Backend = "litert",
                Threads = "auto",
                GpuLayers = "auto",
                ContextSize = 32768,
                EnableJinja = false,
                ChatTemplate = "gemma"
            }
        },
        new()
        {
            Id = "gemma-4-e4b",
            PresetId = "embedded-gemma-4-e4b",
            DisplayName = "Gemma 4 E4B Q4_K_M",
            Description = "Gemma 4 E4B instruction-tuned GGUF package for mobile/edge multimodal local inference.",
            Provider = "embedded",
            ModelId = "gemma-4-e4b",
            Family = "gemma",
            Format = "gguf",
            Quantization = "Q4_K_M",
            FileName = "gemma-4-E4B-it-Q4_K_M.gguf",
            DownloadUrl = "https://huggingface.co/ggml-org/gemma-4-E4B-it-GGUF/resolve/main/gemma-4-E4B-it-Q4_K_M.gguf",
            ExpectedSha256 = "90ce98129eb3e8cc57e62433d500c97c624b1e3af1fcc85dd3b55ad7e0313e9f",
            ModelPageUrl = "https://huggingface.co/ggml-org/gemma-4-E4B-it-GGUF",
            LicenseUrl = "https://ai.google.dev/gemma/terms",
            RequiresLicenseAcceptance = true,
            MinRamGb = 6,
            RecommendedRamGb = 16,
            ContextWindow = 128000,
            MaxOutputTokens = 4096,
            Tags = ["local", "private", "offline", "cheap", "gemma4"],
            Capabilities = new ModelCapabilities
            {
                SupportsTools = true,
                SupportsVision = true,
                SupportsJsonSchema = false,
                SupportsStructuredOutputs = false,
                SupportsStreaming = true,
                SupportsParallelToolCalls = true,
                SupportsReasoningEffort = true,
                SupportsSystemMessages = true,
                SupportsImageInput = true,
                SupportsVideoInput = true,
                SupportsAudioInput = true,
                MaxContextTokens = 128000,
                MaxOutputTokens = 4096
            },
            Files =
            [
                new()
                {
                    Role = LocalModelPackageFileRoles.Model,
                    FileName = "gemma-4-E4B-it-Q4_K_M.gguf",
                    DownloadUrl = "https://huggingface.co/ggml-org/gemma-4-E4B-it-GGUF/resolve/main/gemma-4-E4B-it-Q4_K_M.gguf",
                    ExpectedSha256 = "90ce98129eb3e8cc57e62433d500c97c624b1e3af1fcc85dd3b55ad7e0313e9f",
                    Required = true,
                    InstallByDefault = true
                },
                new()
                {
                    Role = LocalModelPackageFileRoles.MultimodalProjector,
                    FileName = "mmproj-gemma-4-E4B-it-Q8_0.gguf",
                    DownloadUrl = "https://huggingface.co/ggml-org/gemma-4-E4B-it-GGUF/resolve/main/mmproj-gemma-4-E4B-it-Q8_0.gguf",
                    ExpectedSha256 = "51d4b7fd825e4569f746b200fccc5332bf914e8ef7cbe447272ce4fec6df3db6",
                    Required = true,
                    InstallByDefault = true
                }
            ],
            Runtime = new LocalModelRuntimeDefaults
            {
                Backend = "llama.cpp",
                Threads = "auto",
                GpuLayers = "auto",
                ContextSize = 128000,
                EnableJinja = true,
                ChatTemplate = "gemma",
                MultimodalProjectorFileName = "mmproj-gemma-4-E4B-it-Q8_0.gguf",
                ReasoningMode = "auto"
            }
        },
        new()
        {
            Id = "gemma-4-31b",
            PresetId = "embedded-gemma-4-31b",
            DisplayName = "Gemma 4 31B Dense Q4_K_M",
            Description = "Gemma 4 31B dense instruction-tuned GGUF package for workstation/server local inference.",
            Provider = "embedded",
            ModelId = "gemma-4-31b",
            Family = "gemma",
            Format = "gguf",
            Quantization = "Q4_K_M",
            FileName = "gemma-4-31B-it-Q4_K_M.gguf",
            DownloadUrl = "https://huggingface.co/ggml-org/gemma-4-31B-it-GGUF/resolve/main/gemma-4-31B-it-Q4_K_M.gguf",
            ExpectedSha256 = "4f369f8fe0e1bedc5caee9abb89316887f548f80f3035398a5d222a737e699e6",
            ModelPageUrl = "https://huggingface.co/ggml-org/gemma-4-31B-it-GGUF",
            LicenseUrl = "https://ai.google.dev/gemma/terms",
            RequiresLicenseAcceptance = true,
            MinRamGb = 20,
            RecommendedRamGb = 32,
            ContextWindow = 256000,
            MaxOutputTokens = 4096,
            Tags = ["local", "private", "offline", "gemma4"],
            Capabilities = new ModelCapabilities
            {
                SupportsTools = true,
                SupportsVision = true,
                SupportsJsonSchema = false,
                SupportsStructuredOutputs = false,
                SupportsStreaming = true,
                SupportsParallelToolCalls = true,
                SupportsReasoningEffort = true,
                SupportsSystemMessages = true,
                SupportsImageInput = true,
                SupportsVideoInput = true,
                SupportsAudioInput = false,
                MaxContextTokens = 256000,
                MaxOutputTokens = 4096
            },
            Files =
            [
                new()
                {
                    Role = LocalModelPackageFileRoles.Model,
                    FileName = "gemma-4-31B-it-Q4_K_M.gguf",
                    DownloadUrl = "https://huggingface.co/ggml-org/gemma-4-31B-it-GGUF/resolve/main/gemma-4-31B-it-Q4_K_M.gguf",
                    ExpectedSha256 = "4f369f8fe0e1bedc5caee9abb89316887f548f80f3035398a5d222a737e699e6",
                    Required = true,
                    InstallByDefault = true
                },
                new()
                {
                    Role = LocalModelPackageFileRoles.MultimodalProjector,
                    FileName = "mmproj-gemma-4-31B-it-Q8_0.gguf",
                    DownloadUrl = "https://huggingface.co/ggml-org/gemma-4-31B-it-GGUF/resolve/main/mmproj-gemma-4-31B-it-Q8_0.gguf",
                    ExpectedSha256 = "1e8de54a30a5d08fa400c8d956a5ef7f8ad5ba51a39b860d1ccb463d7c330c37",
                    Required = true,
                    InstallByDefault = true
                }
            ],
            Runtime = new LocalModelRuntimeDefaults
            {
                Backend = "llama.cpp",
                Threads = "auto",
                GpuLayers = "auto",
                ContextSize = 256000,
                EnableJinja = true,
                ChatTemplate = "gemma",
                MultimodalProjectorFileName = "mmproj-gemma-4-31B-it-Q8_0.gguf",
                ReasoningMode = "auto"
            }
        },
        new()
        {
            Id = "gemma-4-26b-a4b",
            PresetId = "embedded-gemma-4-26b-a4b",
            DisplayName = "Gemma 4 26B A4B MoE Q4_K_M",
            Description = "Gemma 4 26B A4B MoE instruction-tuned GGUF package for efficient advanced local inference.",
            Provider = "embedded",
            ModelId = "gemma-4-26b-a4b",
            Family = "gemma",
            Format = "gguf",
            Quantization = "Q4_K_M",
            FileName = "gemma-4-26B-A4B-it-Q4_K_M.gguf",
            DownloadUrl = "https://huggingface.co/ggml-org/gemma-4-26B-A4B-it-GGUF/resolve/main/gemma-4-26B-A4B-it-Q4_K_M.gguf",
            ExpectedSha256 = "88f4a13b0bb95f031a7fad973e10854122fb67ebc34d214d39a2f65053046abc",
            ModelPageUrl = "https://huggingface.co/ggml-org/gemma-4-26B-A4B-it-GGUF",
            LicenseUrl = "https://ai.google.dev/gemma/terms",
            RequiresLicenseAcceptance = true,
            MinRamGb = 18,
            RecommendedRamGb = 24,
            ContextWindow = 256000,
            MaxOutputTokens = 4096,
            Tags = ["local", "private", "offline", "moe", "gemma4"],
            Capabilities = new ModelCapabilities
            {
                SupportsTools = true,
                SupportsVision = true,
                SupportsJsonSchema = false,
                SupportsStructuredOutputs = false,
                SupportsStreaming = true,
                SupportsParallelToolCalls = true,
                SupportsReasoningEffort = true,
                SupportsSystemMessages = true,
                SupportsImageInput = true,
                SupportsVideoInput = true,
                SupportsAudioInput = false,
                MaxContextTokens = 256000,
                MaxOutputTokens = 4096
            },
            Files =
            [
                new()
                {
                    Role = LocalModelPackageFileRoles.Model,
                    FileName = "gemma-4-26B-A4B-it-Q4_K_M.gguf",
                    DownloadUrl = "https://huggingface.co/ggml-org/gemma-4-26B-A4B-it-GGUF/resolve/main/gemma-4-26B-A4B-it-Q4_K_M.gguf",
                    ExpectedSha256 = "88f4a13b0bb95f031a7fad973e10854122fb67ebc34d214d39a2f65053046abc",
                    Required = true,
                    InstallByDefault = true
                },
                new()
                {
                    Role = LocalModelPackageFileRoles.MultimodalProjector,
                    FileName = "mmproj-gemma-4-26B-A4B-it-Q8_0.gguf",
                    DownloadUrl = "https://huggingface.co/ggml-org/gemma-4-26B-A4B-it-GGUF/resolve/main/mmproj-gemma-4-26B-A4B-it-Q8_0.gguf",
                    ExpectedSha256 = "1f2339eb6497bd69fde3c68e1592cd472f1ce176dfefe6e6d156d5a55719705e",
                    Required = true,
                    InstallByDefault = true
                }
            ],
            Runtime = new LocalModelRuntimeDefaults
            {
                Backend = "llama.cpp",
                Threads = "auto",
                GpuLayers = "auto",
                ContextSize = 256000,
                EnableJinja = true,
                ChatTemplate = "gemma",
                MultimodalProjectorFileName = "mmproj-gemma-4-26B-A4B-it-Q8_0.gguf",
                ReasoningMode = "auto"
            }
        }
    ];

    public static IReadOnlyList<LocalModelPackageDefinition> List() => Packages;

    public static bool TryGet(string? id, out LocalModelPackageDefinition? package)
    {
        package = Packages.FirstOrDefault(item =>
            string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(item.PresetId, id, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(item.ModelId, id, StringComparison.OrdinalIgnoreCase));
        return package is not null;
    }
}

public static class LocalModelCache
{
    private const string ManifestFileName = "manifest.json";

    public static IReadOnlyList<LocalModelPackageFileDefinition> GetPackageFiles(LocalModelPackageDefinition package)
        => package.Files.Count > 0
            ? package.Files
            :
            [
                new LocalModelPackageFileDefinition
                {
                    Role = LocalModelPackageFileRoles.Model,
                    FileName = package.FileName,
                    DownloadUrl = package.DownloadUrl,
                    ExpectedSha256 = package.ExpectedSha256,
                    Required = true,
                    InstallByDefault = true
                }
            ];

    public static string ResolveModelsRoot(string? configuredRoot = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredRoot))
            return ResolveConfiguredPath(configuredRoot);

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "OpenClaw", "models");
        }

        return Path.Combine(home, ".openclaw", "models");
    }

    public static string ResolveConfiguredPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Configured path cannot be empty.", nameof(path));

        var expanded = Environment.ExpandEnvironmentVariables(path.Trim());
        if (expanded == "~")
        {
            expanded = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
        else if (expanded.StartsWith("~/", StringComparison.Ordinal) ||
                 expanded.StartsWith("~\\", StringComparison.Ordinal))
        {
            expanded = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                expanded[2..]);
        }

        return Path.GetFullPath(expanded);
    }

    public static string GetPackageDirectory(LocalModelPackageDefinition package, string? modelsRoot = null)
        => Path.Combine(ResolveModelsRoot(modelsRoot), package.Id);

    public static string GetModelPath(LocalModelPackageDefinition package, string? modelsRoot = null)
        => Path.Combine(GetPackageDirectory(package, modelsRoot), package.FileName);

    public static string GetPackageFilePath(
        LocalModelPackageDefinition package,
        LocalModelPackageFileDefinition file,
        string? modelsRoot = null)
        => Path.Combine(GetPackageDirectory(package, modelsRoot), file.FileName);

    public static string? GetPackageRolePath(
        LocalModelPackageDefinition package,
        string role,
        string? modelsRoot = null)
    {
        var file = GetPackageFiles(package)
            .FirstOrDefault(item => string.Equals(item.Role, role, StringComparison.OrdinalIgnoreCase));
        return file is null ? null : GetPackageFilePath(package, file, modelsRoot);
    }

    public static string GetManifestPath(LocalModelPackageDefinition package, string? modelsRoot = null)
        => Path.Combine(GetPackageDirectory(package, modelsRoot), ManifestFileName);

    public static IReadOnlyList<LocalModelPackageStatus> ListStatuses(string? modelsRoot = null)
        => LocalModelPackageCatalog.List()
            .Select(package => GetStatus(package, modelsRoot))
            .ToArray();

    public static LocalModelPackageStatus GetStatus(LocalModelPackageDefinition package, string? modelsRoot = null)
    {
        var packageFiles = GetPackageFiles(package);
        var primaryFile = packageFiles.First(item => string.Equals(item.Role, LocalModelPackageFileRoles.Model, StringComparison.OrdinalIgnoreCase));
        var modelPath = GetPackageFilePath(package, primaryFile, modelsRoot);
        var manifestPath = GetManifestPath(package, modelsRoot);
        var manifest = TryReadManifest(manifestPath, out var manifestError);
        var fileStatuses = packageFiles
            .Select(file => GetFileStatus(package, file, manifest, modelsRoot))
            .ToArray();
        var installed = fileStatuses.Where(file => file.Required).All(file => file.Installed);
        var verified = manifest is not null &&
                       fileStatuses.Where(file => file.Required).All(file => file.Verified);
        var issue = manifest is null
            ? manifestError ?? "Install manifest is missing."
            : fileStatuses.FirstOrDefault(file => file.Required && !file.Verified)?.Issue;

        return new LocalModelPackageStatus
        {
            PackageId = package.Id,
            PresetId = package.PresetId,
            ModelId = package.ModelId,
            DisplayName = package.DisplayName,
            Installed = installed,
            Verified = verified,
            ModelPath = modelPath,
            Sha256 = manifest?.Files.FirstOrDefault(file => string.Equals(file.Role, LocalModelPackageFileRoles.Model, StringComparison.OrdinalIgnoreCase))?.Sha256 ?? manifest?.Sha256,
            Issue = verified ? null : issue ?? "Package files are not installed and verified.",
            Files = fileStatuses
        };
    }

    public static async Task<LocalModelInstallResult> InstallAsync(
        LocalModelPackageDefinition package,
        LocalModelInstallRequest request,
        CancellationToken ct)
    {
        if (package.RequiresLicenseAcceptance && !request.AcceptLicense)
        {
            return new LocalModelInstallResult
            {
                Success = false,
                Message = $"Package '{package.Id}' requires explicit license acceptance: {package.LicenseUrl}"
            };
        }

        var packageDir = GetPackageDirectory(package, request.ModelsRoot);
        Directory.CreateDirectory(packageDir);
        var packageFiles = GetPackageFiles(package);
        var primaryFile = packageFiles.First(item => string.Equals(item.Role, LocalModelPackageFileRoles.Model, StringComparison.OrdinalIgnoreCase));
        var installedFiles = new List<LocalModelInstallFileManifest>();

        var source = request.SourcePath;
        if (!string.IsNullOrWhiteSpace(source))
        {
            var copyResult = await CopyInstallFileAsync(package, primaryFile, source, request.ModelsRoot, ct);
            if (!copyResult.Success)
                return copyResult.Result!;

            installedFiles.Add(copyResult.Manifest!);
            foreach (var file in packageFiles.Where(file => !string.Equals(file.Role, LocalModelPackageFileRoles.Model, StringComparison.OrdinalIgnoreCase)))
            {
                var fileSource = ResolveManualSource(file.Role, request);
                if (string.IsNullOrWhiteSpace(fileSource))
                {
                    if (file.Required)
                    {
                        return new LocalModelInstallResult
                        {
                            Success = false,
                            Message = $"Package '{package.Id}' requires {file.Role} file '{file.FileName}'. Pass --{file.Role}-path or install from the package download."
                        };
                    }

                    continue;
                }

                copyResult = await CopyInstallFileAsync(package, file, fileSource, request.ModelsRoot, ct);
                if (!copyResult.Success)
                    return copyResult.Result!;
                installedFiles.Add(copyResult.Manifest!);
            }

            return WriteManifestAndVerify(package, installedFiles, request, source);
        }

        var filesToDownload = packageFiles
            .Where(file => file.Required || (request.DownloadOptionalFiles && file.InstallByDefault))
            .ToArray();
        if (filesToDownload.Length == 0)
        {
            return new LocalModelInstallResult
            {
                Success = false,
                Message = $"Package '{package.Id}' does not define a download URL. Use --path to install an existing GGUF file."
            };
        }

        if (package.RequiresDownloadToken && string.IsNullOrWhiteSpace(request.BearerToken))
        {
            return new LocalModelInstallResult
            {
                Success = false,
                Message = $"Package '{package.Id}' is gated. Pass --accept-license and --token, or install from a local file with --path."
            };
        }

        using var http = OpenClaw.Core.Http.HttpClientFactory.Create();
        foreach (var file in filesToDownload)
        {
            var downloadUrl = string.Equals(file.Role, LocalModelPackageFileRoles.Model, StringComparison.OrdinalIgnoreCase)
                ? request.SourceUrl ?? file.DownloadUrl
                : file.DownloadUrl;
            if (string.IsNullOrWhiteSpace(downloadUrl))
            {
                if (file.Required)
                {
                    return new LocalModelInstallResult
                    {
                        Success = false,
                        Message = $"Package '{package.Id}' does not define a download URL for required {file.Role} file '{file.FileName}'."
                    };
                }

                continue;
            }

            var downloadResult = await DownloadInstallFileAsync(package, file, downloadUrl, request, http, ct);
            if (!downloadResult.Success)
                return downloadResult.Result!;
            installedFiles.Add(downloadResult.Manifest!);
        }

        return WriteManifestAndVerify(package, installedFiles, request, request.SourceUrl ?? primaryFile.DownloadUrl);
    }

    public static async Task<LocalModelPackageStatus> VerifyAsync(
        LocalModelPackageDefinition package,
        string? modelsRoot,
        CancellationToken ct)
    {
        var packageFiles = GetPackageFiles(package);
        if (packageFiles.Where(file => file.Required).Any(file => !File.Exists(GetPackageFilePath(package, file, modelsRoot))))
            return GetStatus(package, modelsRoot);

        var manifest = TryReadManifest(GetManifestPath(package, modelsRoot), out _) ??
            new LocalModelInstallManifest
            {
                PackageId = package.Id,
                PresetId = package.PresetId,
                ModelId = package.ModelId,
                FileName = package.FileName,
                Sha256 = "",
                LicenseUrl = package.LicenseUrl,
                LicenseAccepted = false
            };
        var fileManifests = new List<LocalModelInstallFileManifest>();
        foreach (var file in packageFiles.Where(file => File.Exists(GetPackageFilePath(package, file, modelsRoot))))
        {
            var path = GetPackageFilePath(package, file, modelsRoot);
            fileManifests.Add(new LocalModelInstallFileManifest
            {
                Role = file.Role,
                FileName = file.FileName,
                Sha256 = await ComputeSha256Async(path, ct),
                Source = manifest.Files.FirstOrDefault(item => string.Equals(item.Role, file.Role, StringComparison.OrdinalIgnoreCase))?.Source
            });
        }

        var primarySha = fileManifests.FirstOrDefault(item => string.Equals(item.Role, LocalModelPackageFileRoles.Model, StringComparison.OrdinalIgnoreCase))?.Sha256 ?? manifest.Sha256;
        WriteManifest(package, modelsRoot, new LocalModelInstallManifest
        {
            PackageId = package.Id,
            PresetId = package.PresetId,
            ModelId = package.ModelId,
            FileName = package.FileName,
            Sha256 = primarySha,
            Source = manifest.Source,
            LicenseUrl = manifest.LicenseUrl ?? package.LicenseUrl,
            LicenseAccepted = manifest.LicenseAccepted,
            InstalledAtUtc = manifest.InstalledAtUtc,
            Files = fileManifests
        });

        return GetStatus(package, modelsRoot);
    }

    public static bool Remove(LocalModelPackageDefinition package, string? modelsRoot = null)
    {
        var directory = GetPackageDirectory(package, modelsRoot);
        if (!Directory.Exists(directory))
            return false;

        Directory.Delete(directory, recursive: true);
        return true;
    }

    public static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private sealed record InstallFileWrite(
        bool Success,
        LocalModelInstallFileManifest? Manifest,
        LocalModelInstallResult? Result);

    private static async Task<InstallFileWrite> CopyInstallFileAsync(
        LocalModelPackageDefinition package,
        LocalModelPackageFileDefinition file,
        string source,
        string? modelsRoot,
        CancellationToken ct)
    {
        var sourcePath = ResolveConfiguredPath(source);
        if (!File.Exists(sourcePath))
        {
            return new InstallFileWrite(
                false,
                null,
                new LocalModelInstallResult
                {
                    Success = false,
                    Message = $"Source {file.Role} file was not found: {sourcePath}"
                });
        }

        var destinationPath = GetPackageFilePath(package, file, modelsRoot);
        File.Copy(sourcePath, destinationPath, overwrite: true);
        return await BuildManifestEntryAsync(package, file, destinationPath, sourcePath, ct);
    }

    private static async Task<InstallFileWrite> DownloadInstallFileAsync(
        LocalModelPackageDefinition package,
        LocalModelPackageFileDefinition file,
        string downloadUrl,
        LocalModelInstallRequest request,
        HttpClient http,
        CancellationToken ct)
    {
        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
        if (!string.IsNullOrWhiteSpace(request.BearerToken))
            httpRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", request.BearerToken);

        using var response = await http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            return new InstallFileWrite(
                false,
                null,
                new LocalModelInstallResult
                {
                    Success = false,
                    Message = $"Download for {file.Role} file '{file.FileName}' failed with HTTP {(int)response.StatusCode} {response.StatusCode}."
                });
        }

        var destinationPath = GetPackageFilePath(package, file, request.ModelsRoot);
        await using (var sourceStream = await response.Content.ReadAsStreamAsync(ct))
        await using (var destination = File.Create(destinationPath))
        {
            await sourceStream.CopyToAsync(destination, ct);
        }

        return await BuildManifestEntryAsync(package, file, destinationPath, downloadUrl, ct);
    }

    private static async Task<InstallFileWrite> BuildManifestEntryAsync(
        LocalModelPackageDefinition package,
        LocalModelPackageFileDefinition file,
        string path,
        string source,
        CancellationToken ct)
    {
        var sha = await ComputeSha256Async(path, ct);
        if (!string.IsNullOrWhiteSpace(file.ExpectedSha256) &&
            !string.Equals(sha, file.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(path);
            return new InstallFileWrite(
                false,
                null,
                new LocalModelInstallResult
                {
                    Success = false,
                    Message = $"Checksum mismatch for {file.Role} file '{file.FileName}' in package '{package.Id}'."
                });
        }

        return new InstallFileWrite(
            true,
            new LocalModelInstallFileManifest
            {
                Role = file.Role,
                FileName = file.FileName,
                Sha256 = sha,
                Source = source
            },
            null);
    }

    private static LocalModelInstallResult WriteManifestAndVerify(
        LocalModelPackageDefinition package,
        IReadOnlyList<LocalModelInstallFileManifest> installedFiles,
        LocalModelInstallRequest request,
        string? primarySource)
    {
        var primary = installedFiles.FirstOrDefault(file => string.Equals(file.Role, LocalModelPackageFileRoles.Model, StringComparison.OrdinalIgnoreCase));
        WriteManifest(package, request.ModelsRoot, new LocalModelInstallManifest
        {
            PackageId = package.Id,
            PresetId = package.PresetId,
            ModelId = package.ModelId,
            FileName = package.FileName,
            Sha256 = primary?.Sha256 ?? "",
            Source = primarySource,
            LicenseUrl = package.LicenseUrl,
            LicenseAccepted = request.AcceptLicense,
            Files = installedFiles
        });

        var status = GetStatus(package, request.ModelsRoot);
        return new LocalModelInstallResult
        {
            Success = status.Verified,
            Message = status.Verified
                ? $"Installed {package.Id}."
                : status.Issue ?? $"Installed {package.Id}, but verification did not pass.",
            Status = status
        };
    }

    private static string? ResolveManualSource(string role, LocalModelInstallRequest request)
        => string.Equals(role, LocalModelPackageFileRoles.MultimodalProjector, StringComparison.OrdinalIgnoreCase)
            ? request.MultimodalProjectorPath
            : string.Equals(role, LocalModelPackageFileRoles.DraftModel, StringComparison.OrdinalIgnoreCase)
                ? request.DraftModelPath
                : null;

    private static LocalModelPackageFileStatus GetFileStatus(
        LocalModelPackageDefinition package,
        LocalModelPackageFileDefinition file,
        LocalModelInstallManifest? manifest,
        string? modelsRoot)
    {
        var path = GetPackageFilePath(package, file, modelsRoot);
        if (!File.Exists(path))
        {
            return new LocalModelPackageFileStatus
            {
                Role = file.Role,
                FileName = file.FileName,
                Required = file.Required,
                Installed = false,
                Verified = false,
                Path = path,
                Issue = file.Required ? $"Required {file.Role} file is not installed." : $"{file.Role} file is not installed."
            };
        }

        var fileManifest = FindManifestFile(file, manifest);
        if (fileManifest is null)
        {
            return new LocalModelPackageFileStatus
            {
                Role = file.Role,
                FileName = file.FileName,
                Required = file.Required,
                Installed = true,
                Verified = false,
                Path = path,
                Issue = "Install manifest does not contain this file."
            };
        }

        var expected = string.IsNullOrWhiteSpace(file.ExpectedSha256)
            ? fileManifest.Sha256
            : file.ExpectedSha256;
        var verified = !string.IsNullOrWhiteSpace(expected) &&
                       string.Equals(fileManifest.Sha256, expected, StringComparison.OrdinalIgnoreCase);
        return new LocalModelPackageFileStatus
        {
            Role = file.Role,
            FileName = file.FileName,
            Required = file.Required,
            Installed = true,
            Verified = verified,
            Path = path,
            Sha256 = fileManifest.Sha256,
            Issue = verified ? null : "Manifest checksum does not match the expected package checksum."
        };
    }

    private static LocalModelInstallFileManifest? FindManifestFile(
        LocalModelPackageFileDefinition file,
        LocalModelInstallManifest? manifest)
    {
        if (manifest is null)
            return null;

        var match = manifest.Files.FirstOrDefault(item =>
            string.Equals(item.Role, file.Role, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(item.FileName, file.FileName, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
            return match;

        if (string.Equals(file.Role, LocalModelPackageFileRoles.Model, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(manifest.Sha256))
        {
            return new LocalModelInstallFileManifest
            {
                Role = LocalModelPackageFileRoles.Model,
                FileName = manifest.FileName,
                Sha256 = manifest.Sha256,
                Source = manifest.Source
            };
        }

        return null;
    }

    private static void WriteManifest(
        LocalModelPackageDefinition package,
        string? modelsRoot,
        LocalModelInstallManifest manifest)
    {
        var path = GetManifestPath(package, modelsRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(manifest, CoreJsonContext.Default.LocalModelInstallManifest);
        File.WriteAllText(path, json);
    }

    private static LocalModelInstallManifest? TryReadManifest(string path, out string? error)
    {
        error = null;
        if (!File.Exists(path))
        {
            error = "Install manifest is missing.";
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(path), CoreJsonContext.Default.LocalModelInstallManifest);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            error = $"Install manifest could not be read: {ex.Message}";
            return null;
        }
    }

}
