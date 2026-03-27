using System.Net;
using System.Text;
using System.Text.Json;
using OpenClaw.Agent.Tools;
using OpenClaw.Core.Models;
using OpenClaw.Core.Plugins;
using OpenClaw.Gateway.Composition;
using Xunit;

namespace OpenClaw.Tests;

public sealed class NotionToolTests
{
    [Fact]
    public async Task ReadPage_UsesDefaultPageId_WhenOmitted()
    {
        var handler = new SequenceHandler(
        [
            req =>
            {
                Assert.Equal(HttpMethod.Get, req.Method);
                Assert.EndsWith("/pages/page-default", req.RequestUri!.ToString(), StringComparison.Ordinal);
                return JsonResponse("""{"object":"page","id":"page-default","url":"https://notion.so/page-default","last_edited_time":"2026-03-25T00:00:00.000Z","parent":{"type":"page_id","page_id":"root"},"properties":{"Name":{"id":"title","type":"title","title":[{"plain_text":"Scratchpad"}]}}}""");
            },
            req =>
            {
                Assert.Equal(HttpMethod.Get, req.Method);
                Assert.Contains("/blocks/page-default/children", req.RequestUri!.ToString(), StringComparison.Ordinal);
                return JsonResponse("""{"results":[{"object":"block","id":"b1","type":"paragraph","paragraph":{"rich_text":[{"plain_text":"hello notion"}]}}],"has_more":false,"next_cursor":null}""");
            }
        ]);

        using var tool = new NotionTool(CreateConfig(defaultPageId: "page-default"), new HttpClient(handler));
        var result = await tool.ExecuteAsync("""{"op":"read_page"}""", CancellationToken.None);

        Assert.Contains("title: Scratchpad", result);
        Assert.Contains("page_id: page-default", result);
        Assert.Contains("hello notion", result);
    }

    [Fact]
    public async Task ListNotes_DisallowedDatabaseId_IsRejectedBeforeHttpCall()
    {
        var handler = new SequenceHandler([]);
        using var tool = new NotionTool(CreateConfig(defaultDatabaseId: "db-allowed"), new HttpClient(handler));

        var result = await tool.ExecuteAsync("""{"op":"list_notes","database_id":"db-denied"}""", CancellationToken.None);

        Assert.Contains("not allowed", result);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task Search_FiltersResultsToAllowedTargets()
    {
        var handler = new SequenceHandler(
        [
            req =>
            {
                Assert.Equal(HttpMethod.Post, req.Method);
                Assert.EndsWith("/search", req.RequestUri!.ToString(), StringComparison.Ordinal);
                return JsonResponse("""
                    {
                      "results": [
                        {
                          "object": "page",
                          "id": "page-allowed",
                          "url": "https://notion.so/page-allowed",
                          "last_edited_time": "2026-03-25T00:00:00.000Z",
                          "parent": { "type": "database_id", "database_id": "db-allowed" },
                          "properties": { "Name": { "type": "title", "title": [{ "plain_text": "Allowed note" }] } }
                        },
                        {
                          "object": "page",
                          "id": "page-denied",
                          "url": "https://notion.so/page-denied",
                          "last_edited_time": "2026-03-25T00:00:00.000Z",
                          "parent": { "type": "database_id", "database_id": "db-denied" },
                          "properties": { "Name": { "type": "title", "title": [{ "plain_text": "Denied note" }] } }
                        }
                      ],
                      "has_more": false,
                      "next_cursor": null
                    }
                    """);
            }
        ]);

        using var tool = new NotionTool(CreateConfig(defaultDatabaseId: "db-allowed"), new HttpClient(handler));
        var result = await tool.ExecuteAsync("""{"op":"search","query":"note"}""", CancellationToken.None);

        Assert.Contains("Allowed note", result);
        Assert.DoesNotContain("Denied note", result);
    }

    [Fact]
    public async Task AppendPage_Succeeds_ForAllowedDefaultPage()
    {
        var handler = new SequenceHandler(
        [
            req =>
            {
                Assert.Equal(HttpMethod.Get, req.Method);
                Assert.EndsWith("/pages/page-default", req.RequestUri!.ToString(), StringComparison.Ordinal);
                return JsonResponse("""{"object":"page","id":"page-default","url":"https://notion.so/page-default","last_edited_time":"2026-03-25T00:00:00.000Z","parent":{"type":"page_id","page_id":"root"},"properties":{"Name":{"type":"title","title":[{"plain_text":"Scratchpad"}]}}}""");
            },
            req =>
            {
                Assert.Equal(HttpMethod.Patch, req.Method);
                Assert.EndsWith("/blocks/page-default/children", req.RequestUri!.ToString(), StringComparison.Ordinal);
                return JsonResponse("""{"object":"list","results":[]}""");
            }
        ]);

        using var tool = new NotionWriteTool(CreateConfig(defaultPageId: "page-default"), new HttpClient(handler));
        var result = await tool.ExecuteAsync("""{"op":"append_page","content":"hello world"}""", CancellationToken.None);

        Assert.Contains("OK: appended content", result);
    }

    [Fact]
    public async Task CreateNote_Succeeds_AgainstAllowedDatabase()
    {
        var handler = new SequenceHandler(
        [
            req =>
            {
                Assert.Equal(HttpMethod.Get, req.Method);
                Assert.EndsWith("/databases/db-allowed", req.RequestUri!.ToString(), StringComparison.Ordinal);
                return JsonResponse("""
                    {
                      "object":"database",
                      "id":"db-allowed",
                      "properties":{
                        "Name":{"type":"title","title":{}},
                        "Tags":{"type":"multi_select","multi_select":{"options":[]}}
                      }
                    }
                    """);
            },
            req =>
            {
                Assert.Equal(HttpMethod.Post, req.Method);
                Assert.EndsWith("/pages", req.RequestUri!.ToString(), StringComparison.Ordinal);
                return JsonResponse("""{"object":"page","id":"page-created","url":"https://notion.so/page-created","last_edited_time":"2026-03-25T00:00:00.000Z","parent":{"type":"database_id","database_id":"db-allowed"},"properties":{"Name":{"type":"title","title":[{"plain_text":"Release Notes"}]}}}""");
            }
        ]);

        using var tool = new NotionWriteTool(CreateConfig(defaultDatabaseId: "db-allowed"), new HttpClient(handler));
        var result = await tool.ExecuteAsync("""{"op":"create_note","title":"Release Notes","content":"shipped","tags":["ops"]}""", CancellationToken.None);

        Assert.Contains("OK: created note 'Release Notes'", result);
        Assert.Contains("page-created", result);
    }

    [Fact]
    public async Task UpdateNote_Succeeds_WhenReplacingContent()
    {
        var handler = new SequenceHandler(
        [
            req => JsonResponse("""{"object":"page","id":"page-note","url":"https://notion.so/page-note","last_edited_time":"2026-03-25T00:00:00.000Z","parent":{"type":"database_id","database_id":"db-allowed"},"properties":{"Name":{"type":"title","title":[{"plain_text":"Release Notes"}]}}}"""),
            req => JsonResponse("""{"object":"database","id":"db-allowed","properties":{"Name":{"type":"title","title":{}}} }"""),
            req => JsonResponse("""{"object":"page","id":"page-note","url":"https://notion.so/page-note","last_edited_time":"2026-03-25T00:00:01.000Z","parent":{"type":"database_id","database_id":"db-allowed"},"properties":{"Name":{"type":"title","title":[{"plain_text":"Release Notes v2"}]}}}"""),
            req => JsonResponse("""{"results":[{"object":"block","id":"block-1","type":"paragraph","paragraph":{"rich_text":[{"plain_text":"old"}]}}],"has_more":false,"next_cursor":null}"""),
            req => JsonResponse("""{"object":"block","id":"block-1","archived":true}"""),
            req => JsonResponse("""{"object":"page","id":"page-note","url":"https://notion.so/page-note","last_edited_time":"2026-03-25T00:00:02.000Z","parent":{"type":"database_id","database_id":"db-allowed"},"properties":{"Name":{"type":"title","title":[{"plain_text":"Release Notes v2"}]}}}"""),
            req => JsonResponse("""{"object":"list","results":[]}"""),
            req => JsonResponse("""{"object":"page","id":"page-note","url":"https://notion.so/page-note","last_edited_time":"2026-03-25T00:00:03.000Z","parent":{"type":"database_id","database_id":"db-allowed"},"properties":{"Name":{"type":"title","title":[{"plain_text":"Release Notes v2"}]}}}""")
        ]);

        using var tool = new NotionWriteTool(CreateConfig(defaultDatabaseId: "db-allowed"), new HttpClient(handler));
        var result = await tool.ExecuteAsync("""{"op":"update_note","page_id":"page-note","title":"Release Notes v2","content":"new body"}""", CancellationToken.None);

        Assert.Contains("OK: updated note 'Release Notes v2'", result);
    }

    [Fact]
    public async Task WriteTool_BlocksGlobalReadOnlyMode()
    {
        using var tool = new NotionWriteTool(
            CreateConfig(defaultPageId: "page-default"),
            new HttpClient(new SequenceHandler([])),
            new ToolingConfig { ReadOnlyMode = true });

        var result = await tool.ExecuteAsync("""{"op":"append_page","content":"x"}""", CancellationToken.None);

        Assert.Contains("Tooling.ReadOnlyMode", result);
    }

    [Fact]
    public async Task WriteTool_BlocksIntegrationReadOnlyMode()
    {
        using var tool = new NotionWriteTool(
            CreateConfig(defaultPageId: "page-default", readOnly: true),
            new HttpClient(new SequenceHandler([])));

        var result = await tool.ExecuteAsync("""{"op":"append_page","content":"x"}""", CancellationToken.None);

        Assert.Contains("Plugins.Native.Notion.ReadOnly", result);
    }

    [Fact]
    public async Task ReadTool_Maps401ToActionableError()
    {
        var handler = new SequenceHandler(
        [
            _ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("""{"message":"unauthorized"}""", Encoding.UTF8, "application/json")
            }
        ]);

        using var tool = new NotionTool(CreateConfig(defaultPageId: "page-default"), new HttpClient(handler));
        var result = await tool.ExecuteAsync("""{"op":"read_page"}""", CancellationToken.None);

        Assert.Contains("authorization failed", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("shared with the integration", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveApprovalMode_ForcesNotionWriteApproval_WhenConfigured()
    {
        var config = new GatewayConfig
        {
            Tooling = new ToolingConfig
            {
                RequireToolApproval = false,
                AutonomyMode = "full",
                ApprovalRequiredTools = []
            },
            Plugins = new PluginsConfig
            {
                Native = new NativePluginsConfig
                {
                    Notion = new NotionConfig
                    {
                        Enabled = true,
                        ApiKeyRef = "raw:test-token",
                        DefaultPageId = "page-default",
                        ReadOnly = false,
                        RequireApprovalForWrites = true
                    }
                }
            }
        };

        var (requireApproval, requiredTools) = RuntimeInitializationExtensions.ResolveApprovalMode(config);

        Assert.True(requireApproval);
        Assert.Contains("notion_write", requiredTools, StringComparer.OrdinalIgnoreCase);
    }

    private static NotionConfig CreateConfig(
        string? defaultPageId = null,
        string? defaultDatabaseId = null,
        bool readOnly = false)
        => new()
        {
            Enabled = true,
            ApiKeyRef = "raw:test-token",
            DefaultPageId = defaultPageId,
            DefaultDatabaseId = defaultDatabaseId,
            AllowedPageIds = defaultPageId is null ? [] : [defaultPageId],
            AllowedDatabaseIds = defaultDatabaseId is null ? [] : [defaultDatabaseId],
            ReadOnly = readOnly
        };

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class SequenceHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses;

        public SequenceHandler(IEnumerable<Func<HttpRequestMessage, HttpResponseMessage>> responses)
        {
            _responses = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>(responses);
        }

        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            if (_responses.Count == 0)
                throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");

            return Task.FromResult(_responses.Dequeue()(request));
        }
    }
}
