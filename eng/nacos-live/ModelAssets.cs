namespace NacosLiveAcceptance;

internal sealed record ModelSource(Uri Endpoint, string Repository, string Branch)
{
    public static ModelSource FromEnvironment(IReadOnlyDictionary<string, string?>? environment = null)
    {
        var endpointValue = GetValue("HF_ENDPOINT", "https://www.modelscope.cn", environment);
        if (!Uri.TryCreate(endpointValue, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("HF_ENDPOINT must be an absolute HTTPS URI.");
        }

        var repository = GetValue("HF_REPO", "sentence-transformers/all-MiniLM-L6-v2", environment);
        var defaultBranch = IsModelScopeEndpoint(endpoint) ? "master" : "main";
        var branch = GetValue("HF_BRANCH", defaultBranch, environment);
        return new ModelSource(endpoint, repository, branch);
    }

    internal static bool IsModelScopeEndpoint(Uri endpoint) =>
        endpoint.Host.Equals("www.modelscope.cn", StringComparison.OrdinalIgnoreCase)
        || endpoint.Host.Equals("modelscope.cn", StringComparison.OrdinalIgnoreCase);

    private static string GetValue(
        string key,
        string fallback,
        IReadOnlyDictionary<string, string?>? environment)
    {
        if (environment is not null)
        {
            return environment.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value
                : fallback;
        }

        var environmentValue = Environment.GetEnvironmentVariable(key);
        return !string.IsNullOrWhiteSpace(environmentValue) ? environmentValue : fallback;
    }
}

internal static class ModelAssets
{
    private static readonly (string RemotePath, string FileName)[] RequiredAssets =
    [
        ("onnx/model.onnx", "model.onnx"),
        ("tokenizer.json", "tokenizer.json"),
        ("vocab.txt", "vocab.txt"),
        ("config.json", "config.json"),
    ];

    public static async Task<string> PrepareAsync(
        HttpClient client,
        string destination,
        ModelSource source,
        CancellationToken cancellationToken)
    {
        var fullDestination = Path.GetFullPath(destination);
        Directory.CreateDirectory(fullDestination);
        foreach (var (remotePath, fileName) in RequiredAssets)
        {
            var targetPath = Path.Combine(fullDestination, fileName);
            var temporaryPath = targetPath + ".download";
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, BuildDownloadUri(source, remotePath));
                request.Headers.UserAgent.ParseAdd("OpenClaw-NacosLive/1.0");
                using var response = await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using (var output = new FileStream(
                    temporaryPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    81920,
                    useAsync: true))
                {
                    await input.CopyToAsync(output, cancellationToken);
                    await output.FlushAsync(cancellationToken);
                    if (output.Length == 0)
                    {
                        throw new InvalidDataException($"Model asset is empty: {fileName}");
                    }
                }

                File.Move(temporaryPath, targetPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }

        ValidateFiles(fullDestination);
        return fullDestination;
    }

    internal static Uri BuildDownloadUri(ModelSource source, string remotePath)
    {
        if (source.Endpoint.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("Model assets must be downloaded over HTTPS.", nameof(source));
        }

        if (string.IsNullOrWhiteSpace(source.Repository)
            || source.Repository.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new ArgumentException("Model repository path is invalid.", nameof(source));
        }

        if (string.IsNullOrWhiteSpace(remotePath)
            || remotePath.StartsWith("/", StringComparison.Ordinal)
            || remotePath.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new ArgumentException("Model asset path is invalid.", nameof(remotePath));
        }

        var repositoryPath = string.Join('/', source.Repository.Split('/').Select(Uri.EscapeDataString));
        var builder = new UriBuilder(source.Endpoint);
        if (ModelSource.IsModelScopeEndpoint(source.Endpoint))
        {
            builder.Path = $"/api/v1/models/{repositoryPath}/repo";
            builder.Query = $"Revision={Uri.EscapeDataString(source.Branch)}&FilePath={Uri.EscapeDataString(remotePath)}";
        }
        else
        {
            var remotePathSegments = string.Join('/', remotePath.Split('/').Select(Uri.EscapeDataString));
            builder.Path = $"/{repositoryPath}/resolve/{Uri.EscapeDataString(source.Branch)}/{remotePathSegments}";
            builder.Query = string.Empty;
        }

        return builder.Uri;
    }

    internal static void ValidateFiles(string directory)
    {
        foreach (var (_, fileName) in RequiredAssets)
        {
            var path = Path.Combine(directory, fileName);
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
            {
                throw new InvalidDataException($"Model asset is missing or empty: {fileName}");
            }
        }
    }
}