using System.Net;
using NacosLiveAcceptance;
using Xunit;

namespace NacosLiveAcceptance.Tests;

public sealed class ModelAssetsTests
{
    [Fact]
    public void BuildDownloadUri_UsesConfiguredEndpointRepositoryBranchAndFile()
    {
        var source = new ModelSource(new Uri("https://models.example/"), "team/model", "release/1");

        var uri = ModelAssets.BuildDownloadUri(source, "onnx/model.onnx");

        Assert.Equal(
            "https://models.example/team/model/resolve/release%2F1/onnx/model.onnx",
            uri.AbsoluteUri);
    }

    [Fact]
    public void BuildDownloadUri_UsesModelScopeApiLayout()
    {
        var source = new ModelSource(new Uri("https://www.modelscope.cn/"), "team/model", "master");

        var uri = ModelAssets.BuildDownloadUri(source, "onnx/model.onnx");

        Assert.Equal(
            "https://www.modelscope.cn/api/v1/models/team/model/repo?Revision=master&FilePath=onnx%2Fmodel.onnx",
            uri.AbsoluteUri);
    }

    [Fact]
    public void FromEnvironment_UsesMainForNonModelScopeResolveEndpoint()
    {
        var source = ModelSource.FromEnvironment(new Dictionary<string, string?>
        {
            ["HF_ENDPOINT"] = "https://models.example",
            ["HF_REPO"] = "team/model",
        });

        var uri = ModelAssets.BuildDownloadUri(source, "onnx/model.onnx");

        Assert.Equal("main", source.Branch);
        Assert.Equal(
            "https://models.example/team/model/resolve/main/onnx/model.onnx",
            uri.AbsoluteUri);
    }

    [Fact]
    public async Task PrepareAsync_SendsUserAgentAcceptedByModelScopeCdn()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var handler = new UserAgentHandler();
        using var client = new HttpClient(handler);

        await ModelAssets.PrepareAsync(
            client,
            temporaryDirectory.Path,
            new ModelSource(new Uri("https://models.example/"), "team/model", "main"),
            TestContext.Current.CancellationToken);

        Assert.Equal(4, handler.UserAgents.Count);
        Assert.All(handler.UserAgents, userAgent => Assert.Contains("OpenClaw-NacosLive/1.0", userAgent, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("model.onnx")]
    [InlineData("tokenizer.json")]
    [InlineData("vocab.txt")]
    [InlineData("config.json")]
    public void ValidateFiles_ReportsEachMissingAsset(string missingFile)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        foreach (var fileName in new[] { "model.onnx", "tokenizer.json", "vocab.txt", "config.json" })
        {
            if (fileName != missingFile)
            {
                File.WriteAllText(Path.Combine(temporaryDirectory.Path, fileName), "asset");
            }
        }

        var exception = Assert.Throws<InvalidDataException>(() => ModelAssets.ValidateFiles(temporaryDirectory.Path));

        Assert.Contains(missingFile, exception.Message, StringComparison.Ordinal);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("nacos-model-test-").FullName;

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private sealed class UserAgentHandler : HttpMessageHandler
    {
        public List<string> UserAgents { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            UserAgents.Add(string.Join(' ', request.Headers.UserAgent));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("asset"),
            });
        }
    }
}