using System.Net;
using System.Text;
using OpenClaw.Companion.Services;
using OpenClaw.Companion.ViewModels;
using OpenClaw.Client;
using Xunit;

namespace OpenClaw.Tests;

public sealed class CompanionRuntimeConsoleTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (DirectoryNotFoundException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Theory]
    [InlineData("home", 0)]
    [InlineData("sessions", 4)]
    [InlineData("runtime events", 8)]
    [InlineData("payment lab", 14)]
    public void NavigateToSectionCommand_SelectsRuntimeConsoleSection(string section, int expectedIndex)
    {
        var viewModel = CreateViewModel();

        viewModel.NavigateToSectionCommand.Execute(section);

        Assert.Equal(expectedIndex, viewModel.SelectedSectionIndex);
    }

    [Fact]
    public async Task PaymentMutationCommands_RequireConfirmationBeforeClientUse()
    {
        var viewModel = CreateViewModel();
        viewModel.VirtualCardMerchantName = "Example Merchant";
        viewModel.VirtualCardAmountMinor = "1200";
        viewModel.VirtualCardCurrency = "USD";

        await viewModel.IssueVirtualCardCommand.ExecuteAsync(null);

        Assert.Equal("", viewModel.PaymentResultText);
        Assert.Equal("Payment Lab not loaded.", viewModel.PaymentsStatus);
    }

    [Fact]
    public async Task AutomationDeleteCommand_RequiresConfirmationBeforeClientUse()
    {
        var viewModel = CreateViewModel();
        viewModel.SelectedAutomation = new AutomationRow
        {
            AutomationId = "daily-summary",
            Name = "Daily Summary",
            Schedule = "@daily",
            Delivery = "cron",
            State = "enabled",
            Tags = ""
        };

        await viewModel.DeleteSelectedAutomationCommand.ExecuteAsync(null);

        Assert.Equal("Automations not loaded.", viewModel.AutomationsStatus);
    }

    [Fact]
    public void RuntimeEventRow_FormatsMetadataPreview()
    {
        var row = RuntimeEventRow.FromEntry(new OpenClaw.Core.Models.RuntimeEventEntry
        {
            Id = "evt_1",
            TimestampUtc = DateTimeOffset.Parse("2026-05-16T12:00:00Z"),
            Component = "approvals",
            Action = "queued",
            Severity = "info",
            SessionId = "sess_1",
            ChannelId = "webchat",
            SenderId = "user_1",
            Summary = "Approval queued.",
            Metadata = new Dictionary<string, string> { ["tool"] = "openai-http" }
        });

        Assert.Equal("approvals", row.Component);
        Assert.Equal("queued", row.Action);
        Assert.Contains("openai-http", row.RawJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadSessionsCommand_AppliesFiltersDedupesAndMapsSnippets()
    {
        var requests = new List<Uri>();
        var viewModel = CreateViewModel((baseUrl, authToken) => new OpenClawHttpClient(
            baseUrl,
            authToken,
            new HttpClient(new CallbackHandler(request =>
            {
                requests.Add(request.RequestUri!);
                return request.RequestUri!.AbsolutePath switch
                {
                    "/api/integration/sessions" => JsonResponse("""
                    {
                      "filters": {},
                      "active": [
                        { "id": "sess-a", "channelId": "webchat", "senderId": "alice", "createdAt": "2026-05-17T00:00:00Z", "lastActiveAt": "2026-05-17T00:02:00Z", "state": 0, "historyTurns": 2, "totalInputTokens": 10, "totalOutputTokens": 5, "isActive": true }
                      ],
                      "persisted": {
                        "page": 1,
                        "pageSize": 25,
                        "hasMore": true,
                        "items": [
                          { "id": "sess-a", "channelId": "webchat", "senderId": "alice", "createdAt": "2026-05-17T00:00:00Z", "lastActiveAt": "2026-05-17T00:02:00Z", "state": 0, "historyTurns": 2, "totalInputTokens": 10, "totalOutputTokens": 5, "isActive": true },
                          { "id": "sess-b", "channelId": "whatsapp", "senderId": "bob", "createdAt": "2026-05-17T00:00:00Z", "lastActiveAt": "2026-05-17T00:03:00Z", "state": 1, "historyTurns": 4, "totalInputTokens": 30, "totalOutputTokens": 7, "isActive": false }
                        ]
                      }
                    }
                    """),
                    "/api/integration/session-search" => JsonResponse("""
                    {
                      "result": {
                        "query": { "text": "alpha", "limit": 25, "snippetLength": 180 },
                        "items": [
                          { "sessionId": "sess-b", "channelId": "whatsapp", "senderId": "bob", "role": "user", "timestamp": "2026-05-17T00:03:00Z", "snippet": "alpha matched here", "score": 1.0 }
                        ]
                      }
                    }
                    """),
                    _ => new HttpResponseMessage(HttpStatusCode.NotFound)
                };
            }))));
        viewModel.ServerUrl = "ws://localhost:7000/ws";
        viewModel.SessionsSearchText = "alpha";
        viewModel.SessionsChannelId = "webchat";
        viewModel.SessionsSenderId = "alice";
        viewModel.SessionsState = "active";
        viewModel.SessionsTag = "triage";
        viewModel.SessionsStarredOnly = true;

        await viewModel.LoadSessionsCommand.ExecuteAsync(null);

        var sessionsRequest = Assert.Single(requests, uri => uri.AbsolutePath == "/api/integration/sessions");
        Assert.Contains("search=alpha", sessionsRequest.Query, StringComparison.Ordinal);
        Assert.Contains("channelId=webchat", sessionsRequest.Query, StringComparison.Ordinal);
        Assert.Contains("senderId=alice", sessionsRequest.Query, StringComparison.Ordinal);
        Assert.Contains("state=Active", sessionsRequest.Query, StringComparison.Ordinal);
        Assert.Contains("tag=triage", sessionsRequest.Query, StringComparison.Ordinal);
        Assert.Contains("starred=true", sessionsRequest.Query, StringComparison.Ordinal);
        Assert.Equal(2, viewModel.SessionRows.Count);
        Assert.True(viewModel.HasSessionRows);
        Assert.True(viewModel.SessionsHasMore);
        Assert.Equal("alpha matched here", Assert.Single(viewModel.SessionRows, row => row.SessionId == "sess-b").Snippet);
        Assert.Equal("2 sessions loaded.", viewModel.SessionsStatus);
    }

    [Fact]
    public async Task SessionsPagination_ChangesPageAndFiltersResetToFirstPage()
    {
        var pages = new List<string>();
        var viewModel = CreateViewModel((baseUrl, authToken) => new OpenClawHttpClient(
            baseUrl,
            authToken,
            new HttpClient(new CallbackHandler(request =>
            {
                pages.Add(request.RequestUri!.Query);
                var hasMore = !request.RequestUri.Query.Contains("page=2", StringComparison.Ordinal);
                return JsonResponse($$"""
                {
                  "filters": {},
                  "active": [],
                  "persisted": {
                    "page": 1,
                    "pageSize": 25,
                    "hasMore": {{hasMore.ToString().ToLowerInvariant()}},
                    "items": [
                      { "id": "sess-page", "channelId": "webchat", "senderId": "alice", "createdAt": "2026-05-17T00:00:00Z", "lastActiveAt": "2026-05-17T00:02:00Z", "state": 0, "historyTurns": 1, "totalInputTokens": 1, "totalOutputTokens": 1, "isActive": false }
                    ]
                  }
                }
                """);
            }))));
        viewModel.ServerUrl = "ws://localhost:7000/ws";

        await viewModel.LoadSessionsCommand.ExecuteAsync(null);
        await viewModel.NextSessionsPageCommand.ExecuteAsync(null);

        Assert.Equal(2, viewModel.SessionsPage);
        Assert.False(viewModel.SessionsHasMore);
        Assert.Contains(pages, query => query.Contains("page=1", StringComparison.Ordinal));
        Assert.Contains(pages, query => query.Contains("page=2", StringComparison.Ordinal));

        viewModel.SessionsSearchText = "new filter";

        Assert.Equal(1, viewModel.SessionsPage);
    }

    [Fact]
    public async Task SelectedSession_LoadsDetailAndTimelineRows()
    {
        var viewModel = CreateViewModel((baseUrl, authToken) => new OpenClawHttpClient(
            baseUrl,
            authToken,
            new HttpClient(new CallbackHandler(request =>
            {
                return request.RequestUri!.AbsolutePath switch
                {
                    "/api/integration/sessions/sess-a" => JsonResponse("""{ "session": null, "isActive": true, "branchCount": 3, "metadata": null }"""),
                    "/api/integration/sessions/sess-a/timeline" => JsonResponse("""
                    {
                      "sessionId": "sess-a",
                      "events": [
                        { "id": "evt-1", "timestampUtc": "2026-05-17T00:04:00Z", "component": "chat", "action": "message", "severity": "info", "summary": "message received" }
                      ],
                      "providerTurns": []
                    }
                    """),
                    _ => new HttpResponseMessage(HttpStatusCode.NotFound)
                };
            }))));
        viewModel.ServerUrl = "ws://localhost:7000/ws";

        viewModel.SelectedSession = new SessionRow
        {
            SessionId = "sess-a",
            ChannelId = "webchat",
            SenderId = "alice",
            State = "Active",
            Source = "active",
            LastActivity = "now",
            Snippet = "",
            HistoryTurns = 2,
            TotalTokens = 15,
            IsActive = true
        };

        await WaitForAsync(() => viewModel.HasSessionTimelineRows);

        Assert.Contains("Session: sess-a", viewModel.SelectedSessionDetail, StringComparison.Ordinal);
        Assert.Contains("Branches: 3", viewModel.SelectedSessionDetail, StringComparison.Ordinal);
        var timeline = Assert.Single(viewModel.SessionTimelineRows);
        Assert.Equal("chat", timeline.Component);
        Assert.True(viewModel.HasSessionTimelineRows);
    }

    private MainWindowViewModel CreateViewModel(Func<string, string?, OpenClawHttpClient>? adminClientFactory = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "openclaw-companion-runtime-console-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return new MainWindowViewModel(new SettingsStore(dir), new GatewayWebSocketClient(), adminClientFactory);
    }

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 50; i++)
        {
            if (condition())
                return;

            await Task.Delay(20);
        }

        Assert.True(condition());
    }

    private sealed class CallbackHandler(Func<HttpRequestMessage, HttpResponseMessage> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(callback(request));
    }
}
