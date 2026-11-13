using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using OpenClaw.Agent.Tools;
using OpenClaw.Core.Models;
using OpenClaw.Gateway;
using OpenClaw.Gateway.Extensions;
using Xunit;

namespace OpenClaw.Tests;

public sealed partial class GatewayAdminEndpointTests
{
    [Theory]
    [InlineData("{}", true)]
    [InlineData("""{"enabled":true}""", true)]
    [InlineData("""{"enabled":false}""", false)]
    public void OperatorAccountRequest_WhenEnabledIsOptional_ShouldPreserveDocumentedDefault(string json, bool expected)
    {
        var request = System.Text.Json.JsonSerializer.Deserialize(json, CoreJsonContext.Default.OperatorAccountCreateRequest);
        Assert.Equal(expected, Assert.IsType<OperatorAccountCreateRequest>(request).Enabled);
    }

    [Theory]
    [InlineData(OperatorRoleNames.Viewer)]
    [InlineData(OperatorRoleNames.Operator)]
    public async Task WorkspaceAndChannelAdmin_WhenCallerLacksAdminRole_ShouldDenyAccess(string role)
    {
        await using var harness = await CreateHarnessAsync(true);
        harness.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            CreateOperatorToken(harness, role, "limited"));
        (HttpMethod Method, string Url)[] routes =
        [
            (HttpMethod.Get, "/admin/workspace/mcp"), (HttpMethod.Put, "/admin/workspace/mcp"),
            (HttpMethod.Get, "/admin/workspace/browse"), (HttpMethod.Get, "/admin/workspace/tree"),
            (HttpMethod.Get, "/admin/workspace/download"), (HttpMethod.Post, "/admin/workspace/upload"),
            (HttpMethod.Post, "/admin/digital-employee/upload"), (HttpMethod.Get, "/admin/channels/feishu"),
            (HttpMethod.Post, "/admin/channels/dingtalk/update"), (HttpMethod.Delete, "/admin/channels/dingtalk/override")
        ];
        foreach (var (method, url) in routes)
        {
            using var request = new HttpRequestMessage(method, url);
            using var response = await harness.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        using var status = await harness.Client.GetAsync("/auth/session");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        using var session = await ReadJsonAsync(status);
        Assert.Equal(role, session.RootElement.GetProperty("role").GetString());
    }

    [Theory]
    [InlineData("/api/integration/payment/virtual-card")]
    [InlineData("/api/integration/payment/execute")]
    public async Task PaymentMutation_WhenBrowserOmitsCsrf_ShouldRejectBeforeProcessing(string path)
    {
        await using var harness = await CreateHarnessAsync(true);
        var (cookie, _) = await LoginAsync(harness.Client, harness.AuthToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, path + "?yes=true") { Content = JsonContent("{}") };
        request.Headers.Add("Cookie", cookie);
        using var response = await harness.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task IntegrationMessages_WhenAccountsOmitRouting_ShouldCreateSeparateOwnedConversations()
    {
        await using var harness = await CreateHarnessAsync(true);
        string? firstSession = null;
        foreach (var username in new[] { "first", "second" })
        {
            var token = CreateOperatorToken(harness, OperatorRoleNames.Operator, username);
            harness.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await harness.Client.PostAsync("/api/integration/messages", JsonContent("""{"text":"hello"}"""));
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            Assert.True(harness.Runtime.Pipeline.InboundReader.TryRead(out var message));
            Assert.NotNull(message);
            Assert.NotNull(message.AuthenticatedUserId);
            Assert.NotEqual(firstSession, message.SessionId);
            await harness.MemoryStore.SaveSessionAsync(new Session
            {
                Id = message.SessionId!, ChannelId = message.ChannelId, SenderId = message.SenderId,
                OwnerAccountId = message.AuthenticatedUserId
            }, TestContext.Current.CancellationToken);
            firstSession ??= message.SessionId;
        }

        using var denied = await harness.Client.PostAsync("/api/integration/messages",
            JsonContent($$"""{"text":"other account","sessionId":"{{firstSession}}"}"""));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Fact]
    public async Task MediaUpload_WhenRoleOrFileTypeChanges_ShouldProtectAndPreserveDownloads()
    {
        await using var harness = await CreateHarnessAsync(true, configureServices: (services, config) =>
            services.AddSingleton(new MediaCacheStore(Path.Combine(config.Memory.StoragePath, "test-media"))));
        var viewer = CreateOperatorToken(harness, OperatorRoleNames.Viewer, "reader");
        var writer = CreateOperatorToken(harness, OperatorRoleNames.Operator, "writer");

        foreach (var (name, mime, bytes) in new[]
        {
            ("fixture.json", "application/json", "{\"fixture\":true}"), ("fixture.svg", "image/svg+xml", "<svg/>")
        })
        {
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(bytes, Encoding.UTF8, mime), "file", name);
            using var upload = new HttpRequestMessage(HttpMethod.Post, "/media/upload") { Content = form };
            upload.Headers.Authorization = new AuthenticationHeaderValue("Bearer", viewer);
            using var uploaded = await harness.Client.SendAsync(upload);
            Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
            using var metadata = await ReadJsonAsync(uploaded);
            using var download = new HttpRequestMessage(HttpMethod.Get, metadata.RootElement.GetProperty("url").GetString());
            download.Headers.Authorization = new AuthenticationHeaderValue("Bearer", viewer);
            using var downloaded = await harness.Client.SendAsync(download);
            Assert.Equal(HttpStatusCode.OK, downloaded.StatusCode);
            Assert.Equal(bytes, await downloaded.Content.ReadAsStringAsync());
            Assert.Equal("attachment", downloaded.Content.Headers.ContentDisposition?.DispositionType);
            Assert.Contains("sandbox", Assert.Single(downloaded.Headers.GetValues("Content-Security-Policy")));
        }

        using var mediaForm = new MultipartFormDataContent();
        mediaForm.Add(new StringContent("audio", Encoding.UTF8, "audio/mpeg"), "file", "fixture.mp3");
        using var mediaUpload = new HttpRequestMessage(HttpMethod.Post, "/media/upload") { Content = mediaForm };
        mediaUpload.Headers.Authorization = new AuthenticationHeaderValue("Bearer", writer);
        using var mediaUploaded = await harness.Client.SendAsync(mediaUpload);
        using var mediaMetadata = await ReadJsonAsync(mediaUploaded);
        using var mediaDownload = new HttpRequestMessage(HttpMethod.Get, mediaMetadata.RootElement.GetProperty("url").GetString());
        mediaDownload.Headers.Authorization = new AuthenticationHeaderValue("Bearer", viewer);
        using var mediaDownloaded = await harness.Client.SendAsync(mediaDownload);
        var csp = Assert.Single(mediaDownloaded.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("media-src 'self'", csp, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(256, HttpStatusCode.OK)]
    [InlineData(30 * 1024 * 1024 + 1, HttpStatusCode.BadRequest)]
    public async Task DigitalEmployee_WhenExpandedArchiveExceedsLimit_ShouldRejectBeforeWriting(int expandedBytes, HttpStatusCode expected)
    {
        var root = CreateSafeTempDirectory("employee-quota");
        try
        {
            await using var harness = await CreateHarnessAsync(true, config => config.Tooling.WorkspaceRoot = root);
            harness.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
            using var data = new MemoryStream();
            using (var archive = new ZipArchive(data, ZipArchiveMode.Create, leaveOpen: true))
            {
                using var entry = archive.CreateEntry("config/AGENTS.md").Open();
                entry.Write(new byte[expandedBytes]);
            }
            using var form = new MultipartFormDataContent();
            form.Add(new ByteArrayContent(data.ToArray()), "file", "employee.zip");
            using var response = await harness.Client.PostAsync("/admin/digital-employee/upload", form);
            Assert.Equal(expected, response.StatusCode);
            Assert.Equal(expected == HttpStatusCode.OK, File.Exists(Path.Combine(root, "AGENTS.md")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task GenericWebhook_WhenUnsignedRequestPrecedesValidRetry_ShouldOnlyReserveAuthenticatedDelivery()
    {
        await using var harness = await CreateHarnessAsync(true, config =>
        {
            config.Webhooks.Enabled = true;
            config.Webhooks.Endpoints["secure"] = new WebhookEndpointConfig
            {
                Secret = "raw:fixture", ValidateHmac = true
            };
        });
        const string body = """{"event":"fixture"}""";
        using var invalid = await PostWebhookAsync(harness.Client, "secure", body, "wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, invalid.StatusCode);
        Assert.False(harness.Runtime.Pipeline.InboundReader.TryRead(out _));
        using var valid = await PostWebhookAsync(harness.Client, "secure", body, "fixture");
        Assert.Equal("Webhook queued.", await valid.Content.ReadAsStringAsync());
        Assert.True(harness.Runtime.Pipeline.InboundReader.TryRead(out _));
        using var duplicate = await PostWebhookAsync(harness.Client, "secure", body, "fixture");
        Assert.Equal("Webhook already processed.", await duplicate.Content.ReadAsStringAsync());
        Assert.False(harness.Runtime.Pipeline.InboundReader.TryRead(out _));
    }

    [Theory]
    [InlineData("loadApprovals")]
    [InlineData("loadApprovalHistory")]
    [InlineData("loadApprovalGrants")]
    [InlineData("loadDeadLetter")]
    public async Task AdminHtml_WhenRenderingUntrustedRecords_ShouldEncodeInterpolations(string function)
    {
        var path = Path.GetFullPath(Path.Join(AppContext.BaseDirectory, "../../../../../src/OpenClaw.Gateway/wwwroot/admin.html"));
        var html = await File.ReadAllTextAsync(path);
        var start = html.IndexOf("async function " + function + "()", StringComparison.Ordinal);
        var end = html.IndexOf("async function ", start + 1, StringComparison.Ordinal);
        var renderer = html[start..end];
        Assert.Contains("escapeHtml(item.", renderer, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"\$\{item\.[^}?]+\}", renderer);
    }

    [Fact]
    public async Task WorkspaceFiles_WhenFileLinksOutside_ShouldRejectOverwriteAndExcludeFromArchive()
    {
        if (OperatingSystem.IsWindows())
            return; // Creating links can require elevated privileges on Windows.

        var root = CreateSafeTempDirectory("workspace-boundary");
        try
        {
            var workspace = Directory.CreateDirectory(Path.Combine(root, "workspace")).FullName;
            var outside = Path.Combine(root, "outside.txt");
            File.WriteAllText(outside, "original");
            File.CreateSymbolicLink(Path.Combine(workspace, "link.txt"), outside);
            File.WriteAllText(Path.Combine(workspace, "inside.txt"), "inside");
            await using var harness = await CreateHarnessAsync(true, config => config.Tooling.WorkspaceRoot = workspace);
            harness.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
            using var upload = new MultipartFormDataContent();
            upload.Add(new StringContent("replaced"), "file", "link.txt");
            using var result = await harness.Client.PostAsync("/admin/workspace/upload", upload);
            Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
            Assert.Equal("original", File.ReadAllText(outside));

            using var response = await harness.Client.GetAsync("/admin/workspace/download");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var zip = new ZipArchive(await response.Content.ReadAsStreamAsync());
            Assert.Equal("inside.txt", Assert.Single(zip.Entries).FullName);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SecureFileSystem_WhenAncestorIsLink_ShouldRejectWriteAndBindReadToOpenedTarget()
    {
        if (OperatingSystem.IsWindows())
            return; // Creating links can require elevated privileges on Windows.

        var root = CreateSafeTempDirectory("secure-file-handles");
        try
        {
            var workspace = Directory.CreateDirectory(Path.Join(root, "workspace")).FullName;
            var outside = Directory.CreateDirectory(Path.Join(root, "outside")).FullName;
            File.WriteAllText(Path.Join(outside, "fixture.txt"), "outside");
            var link = Path.Join(workspace, "linked");
            Directory.CreateSymbolicLink(link, outside);

            Assert.Throws<UnauthorizedAccessException>(() =>
                SecureFileSystem.OpenForWriteUnderRoot(workspace, Path.Join(link, "created.txt")));
            Assert.Throws<UnauthorizedAccessException>(() =>
                SecureFileSystem.OpenForReadUnderRoot(workspace, Path.Join(link, "fixture.txt")));
            var finalPath = ToolPathPolicy.ResolveRealPath(Path.Join(link, "fixture.txt"));
            var resolvedOutside = ToolPathPolicy.ResolveRealPath(outside);
            var stream = SecureFileSystem.OpenForReadUnderRoot(resolvedOutside, finalPath);
            using (stream)
            {
                Assert.Equal(Path.Join(resolvedOutside, "fixture.txt"), finalPath);
                using var reader = new StreamReader(stream);
                Assert.Equal("outside", reader.ReadToEnd());
            }
            Assert.False(File.Exists(Path.Join(outside, "created.txt")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FeishuConfig_WhenAdminReadsReferences_ShouldKeepResolvedSecretsPrivate()
    {
        var config = new FeishuChannelConfig
        {
            Enabled = true,
            AppId = "fixture-app",
            AppSecretRef = "raw:synthetic-secret"
        };
        await using var channel = new OpenClaw.Channels.FeishuChannel(config,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<OpenClaw.Channels.FeishuChannel>.Instance);
        using var stopped = new CancellationTokenSource();
        stopped.Cancel();
        await channel.StartAsync(stopped.Token);
        var result = channel.GetEffectiveConfigForAdmin();
        Assert.Equal(config.AppSecretRef, result.AppSecretRef);
        Assert.True(string.IsNullOrEmpty(result.AppSecret));
    }

    [Fact]
    public async Task MediaCache_WhenLoadingLegacyMetadata_ShouldPreserveAssetsAndRejectOverwrittenPayload()
    {
        var root = CreateSafeTempDirectory("legacy-media");
        try
        {
            var ct = TestContext.Current.CancellationToken;
            var store = new MediaCacheStore(root);
            foreach (var extension in new[] { ".txt", ".json" })
            {
                var path = Path.Combine(root, "legacy" + extension);
                File.WriteAllText(path, "original");
                var asset = new StoredMediaAsset
                {
                    Id = "legacy", Path = path, MediaType = "text/plain", FileName = "old" + extension, SizeBytes = 8
                };
                await File.WriteAllTextAsync(Path.Combine(root, "legacy.json"),
                    System.Text.Json.JsonSerializer.Serialize(asset, CoreJsonContext.Default.StoredMediaAsset), ct);
                var loaded = await store.GetAsync("legacy", ct);
                if (extension == ".json")
                    Assert.Null(loaded);
                else
                    Assert.Equal("original", File.ReadAllText(Assert.IsType<StoredMediaAsset>(loaded).Path));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FilePublication_WhenToolResultContainsMarker_ShouldOnlyCollectAuthorizedPublisherOutput()
    {
        var root = CreateSafeTempDirectory("publish-boundary");
        try
        {
            var ct = TestContext.Current.CancellationToken;
            var workspace = Directory.CreateDirectory(Path.Combine(root, "workspace")).FullName;
            var cache = new MediaCacheStore(Path.Combine(root, "media"));
            var config = new GatewayConfig();
            config.Tooling.WorkspaceRoot = workspace;
            config.Tooling.AllowedReadRoots = [root];
            config.Tooling.AllowedWriteRoots = [workspace];
            var source = Path.Combine(root, "fixture.txt");
            File.WriteAllText(source, "authorized file");
            var publisher = new PublishFileTool(config.Tooling);
            var args = new System.Text.Json.Nodes.JsonObject { ["path"] = source }.ToJsonString();
            var result = await publisher.ExecuteAsync(args, ct);
            var call = new ToolInvocation { ToolName = "publish_file", Arguments = args, Result = result, ResultStatus = ToolResultStatuses.Completed };
            Assert.Null(await GatewayInboundMessageWorker.TryCollectPublishedFileAsync(call with { ToolName = "read_file" }, config, cache, ct));
            Assert.Null(await GatewayInboundMessageWorker.TryCollectPublishedFileAsync(call with { ResultStatus = "failed" }, config, cache, ct));
            Assert.Null(await GatewayInboundMessageWorker.TryCollectPublishedFileAsync(call with { Result = $"[FILE_PATH:{source}]" }, config, cache, ct));
            var asset = await GatewayInboundMessageWorker.TryCollectPublishedFileAsync(call, config, cache, ct);
            Assert.NotNull(asset);
            Assert.Equal("authorized file", File.ReadAllText(asset.Path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
