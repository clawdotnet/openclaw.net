using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Net;

namespace OpenClaw.LayaService.Models;

public sealed record DownloadOptions(string Destination, string Revision, IReadOnlyList<string> Checkpoints);

public static class HuggingFaceDownloader
{
    private const string Repository = "convaiinnovations/laya";
    private const string NoticeFile = "THIRD_PARTY_NOTICES.md";
    private const string LicenseFile = "licenses/laya-APACHE-2.0.txt";

    public static async Task<string> DownloadAsync(DownloadOptions options, HttpClient http, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(http);
        if (!ModelManifest.IsRevision(options.Revision)) throw new ArgumentException("Revision must be a 40-character lowercase commit id.");
        if (options.Checkpoints is null || options.Checkpoints.Count == 0 ||
            options.Checkpoints.Any(checkpoint => !ModelManifest.CheckpointNames.Contains(checkpoint)) ||
            options.Checkpoints.Distinct(StringComparer.Ordinal).Count() != options.Checkpoints.Count)
        {
            throw new ArgumentException("At least one unique supported checkpoint is required.");
        }

        var destination = Path.GetFullPath(options.Destination);
        Directory.CreateDirectory(destination);
        if ((new DirectoryInfo(destination).Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Model destination cannot be a symbolic link.");
        }
        var revisionDirectory = Path.Combine(destination, options.Revision);
        Directory.CreateDirectory(revisionDirectory);
        ModelManifest.RejectReparsePoints(destination, revisionDirectory);
        var manifestPath = Path.Combine(revisionDirectory, "manifest.json");
        var manifest = LoadOrCreateManifest(manifestPath, options.Revision);
        var checkpointEntries = new Dictionary<string, CheckpointManifestEntry>(manifest.Checkpoints, StringComparer.Ordinal);

        foreach (var checkpoint in options.Checkpoints)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var checkpointPrefix = checkpoint == "english" ? string.Empty : checkpoint + "/";
            var checkpointDirectory = checkpoint == "english"
                ? Path.Combine(revisionDirectory, "hub")
                : Path.Combine(revisionDirectory, "hub", checkpoint);
            Directory.CreateDirectory(checkpointDirectory);
            ModelManifest.RejectReparsePoints(revisionDirectory, checkpointDirectory);
            var fileHashes = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var file in ModelManifest.ModelFiles)
            {
                var target = Path.Combine(checkpointDirectory, file.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                ModelManifest.RejectReparsePoints(revisionDirectory, Path.GetDirectoryName(target)!);
                var sourceFile = checkpointPrefix + file;
                var requestUri = new Uri($"https://huggingface.co/{Repository}/resolve/{options.Revision}/{sourceFile}");
                await DownloadFileAsync(http, requestUri, target, checkpoint, file, options.Revision, cancellationToken);
                fileHashes.Add(file, await HashFileAsync(target, cancellationToken));
            }

            checkpointEntries[checkpoint] = new CheckpointManifestEntry
            {
                Path = Path.GetRelativePath(revisionDirectory, checkpointDirectory).Replace(Path.DirectorySeparatorChar, '/'),
                Sha256 = fileHashes
            };
        }

        var updatedManifest = new ModelManifestDocument
        {
            Version = 1,
            Revision = options.Revision,
            Checkpoints = checkpointEntries
        };
        var temporaryManifest = manifestPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporaryManifest, JsonSerializer.SerializeToUtf8Bytes(updatedManifest, JsonOptions), cancellationToken);
            _ = ModelManifest.LoadAndVerify(temporaryManifest, options.Revision);
            File.Move(temporaryManifest, manifestPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryManifest)) File.Delete(temporaryManifest);
        }

        CopyProvenanceFile(NoticeFile, Path.Combine(revisionDirectory, "LAYA-NOTICE.md"));
        CopyProvenanceFile(LicenseFile, Path.Combine(revisionDirectory, "LAYA-LICENSE.txt"));
        return manifestPath;
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static ModelManifestDocument LoadOrCreateManifest(string path, string revision)
    {
        if (!File.Exists(path))
        {
            return new ModelManifestDocument { Version = 1, Revision = revision, Checkpoints = new(StringComparer.Ordinal) };
        }

        _ = ModelManifest.LoadAndVerify(path, revision);
        return JsonSerializer.Deserialize<ModelManifestDocument>(File.ReadAllBytes(path))
            ?? throw new InvalidDataException("Existing model manifest is invalid.");
    }

    private static async Task DownloadFileAsync(
        HttpClient http,
        Uri requestUri,
        string target,
        string checkpoint,
        string file,
        string revision,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.RequestMessage?.RequestUri != requestUri)
        {
            throw new HttpRequestException("Model download response did not match the requested URI.");
        }

        if (response.StatusCode == HttpStatusCode.Found)
        {
            if (!IsAllowedCdnRedirect(response.Headers.Location))
            {
                throw new HttpRequestException("Model download failed or was redirected.");
            }

            var redirectUri = response.Headers.Location!;
            using var redirectRequest = new HttpRequestMessage(HttpMethod.Get, redirectUri);
            using var redirectedResponse = await http.SendAsync(redirectRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            await StoreResponseAsync(redirectedResponse, redirectUri, target, checkpoint, file, revision, cancellationToken);
            return;
        }

        if (response.StatusCode == HttpStatusCode.TemporaryRedirect)
        {
            var cacheUri = GetAllowedResolveCacheRedirect(response.Headers.Location, requestUri, checkpoint, file, revision);
            if (cacheUri is null)
            {
                throw new HttpRequestException("Model download failed or was redirected.");
            }

            using var cacheRequest = new HttpRequestMessage(HttpMethod.Get, cacheUri);
            using var cacheResponse = await http.SendAsync(cacheRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (cacheResponse.RequestMessage?.RequestUri != cacheUri)
            {
                throw new HttpRequestException("Model cache response did not match the requested URI.");
            }

            if (cacheResponse.StatusCode == HttpStatusCode.Found)
            {
                if (!IsAllowedCdnRedirect(cacheResponse.Headers.Location))
                {
                    throw new HttpRequestException("Model download failed or was redirected.");
                }

                var cdnUri = cacheResponse.Headers.Location!;
                using var cdnRequest = new HttpRequestMessage(HttpMethod.Get, cdnUri);
                using var cdnResponse = await http.SendAsync(cdnRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                await StoreResponseAsync(cdnResponse, cdnUri, target, checkpoint, file, revision, cancellationToken);
                return;
            }

            await StoreResponseAsync(cacheResponse, cacheUri, target, checkpoint, file, revision, cancellationToken);
            return;
        }

        await StoreResponseAsync(response, requestUri, target, checkpoint, file, revision, cancellationToken);
    }

    private static bool IsAllowedCdnRedirect(Uri? redirectUri)
    {
        if (redirectUri is not { IsAbsoluteUri: true } || redirectUri.Scheme != Uri.UriSchemeHttps ||
            redirectUri.Port != 443 || redirectUri.UserInfo.Length != 0 || redirectUri.Fragment.Length != 0)
        {
            return false;
        }

        return redirectUri.IdnHost.EndsWith(".cdn.hf.co", StringComparison.OrdinalIgnoreCase);
    }

    private static Uri? GetAllowedResolveCacheRedirect(Uri? location, Uri requestUri, string checkpoint, string file, string revision)
    {
        if (location is null) return null;
        var redirectUri = location.IsAbsoluteUri ? location : new Uri(requestUri, location);
        var sourceFile = (checkpoint == "english" ? string.Empty : checkpoint + "/") + file;
        var expectedPath = $"/api/resolve-cache/models/{Repository}/{revision}/{Uri.EscapeDataString(sourceFile)}";
        return redirectUri.Scheme == Uri.UriSchemeHttps && redirectUri.IdnHost.Equals("huggingface.co", StringComparison.OrdinalIgnoreCase) &&
            redirectUri.Port == 443 && redirectUri.UserInfo.Length == 0 && redirectUri.Fragment.Length == 0 &&
            redirectUri.AbsolutePath.Equals(expectedPath, StringComparison.Ordinal) && redirectUri.Query.Length > 0
                ? redirectUri
                : null;
    }

    private static async Task StoreResponseAsync(
        HttpResponseMessage response,
        Uri expectedUri,
        string target,
        string checkpoint,
        string file,
        string revision,
        CancellationToken cancellationToken)
    {
        if (response.StatusCode != HttpStatusCode.OK || response.Headers.Location is not null ||
            response.RequestMessage?.RequestUri != expectedUri)
        {
            throw new HttpRequestException("Model download failed or was redirected.");
        }

        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = new FileStream(temporary, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough
            }))
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            {
                await input.CopyToAsync(output, cancellationToken);
                await output.FlushAsync(cancellationToken);
            }

            var upstreamHash = await HashFileAsync(temporary, cancellationToken);
            var expectedHash = ModelManifest.GetPinnedHash(revision, checkpoint, file);
            if (expectedHash is not null && !string.Equals(upstreamHash, expectedHash, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Downloaded checkpoint does not match its pinned upstream digest.");
            }

            if (file == "tokenizer/tokenizer_config.json")
            {
                await NormalizeTokenizerConfigAsync(temporary, cancellationToken);
            }

            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static async Task NormalizeTokenizerConfigAsync(string path, CancellationToken cancellationToken)
    {
        var text = await File.ReadAllTextAsync(path, cancellationToken);
        var config = JsonNode.Parse(text) as JsonObject ?? throw new InvalidDataException("Tokenizer configuration is invalid.");
        var tokenizerClass = config["tokenizer_class"]?.GetValue<string>();
        if (tokenizerClass is null or "TokenizersBackend")
        {
            config["tokenizer_class"] = "PreTrainedTokenizerFast";
            config.Remove("backend");
            config.Remove("is_local");
        }

        if (config["extra_special_tokens"] is JsonArray specialTokens)
        {
            var normalized = new JsonObject();
            for (var index = 0; index < specialTokens.Count; index++)
            {
                normalized[$"extra_{index}"] = specialTokens[index]?.DeepClone();
            }
            config["extra_special_tokens"] = normalized;
        }

        var normalizedBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(config, JsonOptions) + "\n");
        await File.WriteAllBytesAsync(path, normalizedBytes, cancellationToken);
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private static void CopyProvenanceFile(string sourceRelativePath, string destination)
    {
        var source = Path.Combine(AppContext.BaseDirectory, sourceRelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(source))
        {
            var repositoryRoot = FindRepositoryRoot();
            source = Path.Combine(repositoryRoot, "tools", "laya_service", sourceRelativePath.Replace('/', Path.DirectorySeparatorChar));
        }
        File.Copy(source, destination, overwrite: true);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "OpenClaw.Net.slnx"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Laya attribution files are not available.");
    }
}