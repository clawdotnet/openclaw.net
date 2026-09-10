using System.Collections.ObjectModel;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenClaw.Core.Models;

namespace OpenClaw.Companion.ViewModels;

public sealed partial class MainWindowViewModel
{
    [ObservableProperty]
    private bool _isSessionsBusy;

    [ObservableProperty]
    private string _sessionsStatus = "Sessions not loaded.";

    [ObservableProperty]
    private string _sessionsSearchText = "";

    [ObservableProperty]
    private string _sessionsChannelId = "";

    [ObservableProperty]
    private string _sessionsSenderId = "";

    [ObservableProperty]
    private string _sessionsState = "";

    [ObservableProperty]
    private string _sessionsTag = "";

    [ObservableProperty]
    private bool _sessionsStarredOnly;

    [ObservableProperty]
    private int _sessionsPage = 1;

    [ObservableProperty]
    private bool _sessionsHasMore;

    [ObservableProperty]
    private SessionRow? _selectedSession;

    [ObservableProperty]
    private string _selectedSessionDetail = "Select a session to inspect metadata and timeline.";

    public ObservableCollection<SessionRow> SessionRows { get; } = [];
    public ObservableCollection<RuntimeEventRow> SessionTimelineRows { get; } = [];
    public bool HasSessionRows => SessionRows.Count > 0;
    public bool HasSessionTimelineRows => SessionTimelineRows.Count > 0;

    partial void OnSelectedSessionChanged(SessionRow? value)
    {
        if (value is null)
        {
            SelectedSessionDetail = "Select a session to inspect metadata and timeline.";
            ReplaceItems(SessionTimelineRows, []);
            OnPropertyChanged(nameof(HasSessionTimelineRows));
            return;
        }

        _ = LoadSelectedSessionAsync(value);
    }

    partial void OnSessionsSearchTextChanged(string value) => ResetSessionsPageForFilterChange();
    partial void OnSessionsChannelIdChanged(string value) => ResetSessionsPageForFilterChange();
    partial void OnSessionsSenderIdChanged(string value) => ResetSessionsPageForFilterChange();
    partial void OnSessionsStateChanged(string value) => ResetSessionsPageForFilterChange();
    partial void OnSessionsTagChanged(string value) => ResetSessionsPageForFilterChange();
    partial void OnSessionsStarredOnlyChanged(bool value) => ResetSessionsPageForFilterChange();

    [RelayCommand]
    private async Task LoadSessionsAsync()
    {
        if (IsSessionsBusy)
            return;

        IsSessionsBusy = true;
        try
        {
            using var client = RequireIntegrationClient(status => SessionsStatus = status);
            if (client is null)
                return;

            var query = new SessionListQuery
            {
                Search = EmptyToNull(SessionsSearchText),
                ChannelId = EmptyToNull(SessionsChannelId),
                SenderId = EmptyToNull(SessionsSenderId),
                State = TryParseSessionState(SessionsState),
                Starred = SessionsStarredOnly ? true : null,
                Tag = EmptyToNull(SessionsTag)
            };
            var response = await client.ListSessionsAsync(SessionsPage, pageSize: 25, query, CancellationToken.None);
            var snippets = await LoadSessionSnippetsAsync(client);
            var rows = response.Active.Select(item => SessionRow.FromSummary(item, "active", snippets))
                .Concat(response.Persisted.Items.Select(item => SessionRow.FromSummary(item, "persisted", snippets)))
                .GroupBy(static row => row.SessionId, StringComparer.Ordinal)
                .Select(static group => group.First())
                .ToList();

            SessionsHasMore = response.Persisted.HasMore;
            ReplaceItems(SessionRows, rows);
            OnPropertyChanged(nameof(HasSessionRows));
            SessionsStatus = rows.Count == 0
                ? "No sessions match the current filters."
                : $"{rows.Count} session{(rows.Count == 1 ? "" : "s")} loaded.";
        }
        catch (OperationCanceledException ex)
        {
            SessionsStatus = $"Sessions load canceled: {ex.Message}";
        }
        catch (HttpRequestException ex)
        {
            SessionsStatus = $"Sessions load failed: {ex.Message}";
        }
        catch (InvalidOperationException ex)
        {
            SessionsStatus = $"Sessions load failed: {ex.Message}";
        }
        finally
        {
            IsSessionsBusy = false;
        }
    }

    [RelayCommand]
    private async Task NextSessionsPageAsync()
    {
        if (!SessionsHasMore)
            return;

        SessionsPage++;
        await LoadSessionsAsync();
    }

    [RelayCommand]
    private async Task PreviousSessionsPageAsync()
    {
        if (SessionsPage <= 1)
            return;

        SessionsPage--;
        await LoadSessionsAsync();
    }

    private async Task<Dictionary<string, string>> LoadSessionSnippetsAsync(OpenClaw.Client.OpenClawHttpClient client)
    {
        if (string.IsNullOrWhiteSpace(SessionsSearchText))
            return new Dictionary<string, string>(StringComparer.Ordinal);

        var search = await client.SearchSessionsAsync(new SessionSearchQuery
        {
            Text = SessionsSearchText,
            ChannelId = EmptyToNull(SessionsChannelId),
            SenderId = EmptyToNull(SessionsSenderId),
            Limit = 25,
            SnippetLength = 180
        }, CancellationToken.None);

        return search.Result.Items
            .GroupBy(static item => item.SessionId, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First().Snippet, StringComparer.Ordinal);
    }

    private async Task LoadSelectedSessionAsync(SessionRow row)
    {
        var requestedSessionId = row.SessionId;
        try
        {
            using var client = RequireIntegrationClient(status => SelectedSessionDetail = status);
            if (client is null)
                return;

            var detail = await client.GetSessionAsync(requestedSessionId, CancellationToken.None);
            var timeline = await client.GetSessionTimelineAsync(requestedSessionId, 100, CancellationToken.None);
            if (SelectedSession?.SessionId != requestedSessionId)
                return;

            SelectedSessionDetail = string.Join(Environment.NewLine, new[]
            {
                $"Session: {requestedSessionId}",
                $"Channel: {row.ChannelId}",
                $"Sender: {row.SenderId}",
                $"State: {row.State}",
                $"Active: {detail.IsActive}",
                $"Branches: {detail.BranchCount}",
                $"History turns: {row.HistoryTurns}",
                $"Tokens: {row.TotalTokens}"
            });
            if (detail.Recovery is { } recovery)
                SelectedSessionDetail += Environment.NewLine + Environment.NewLine
                    + "Run explanation and recovery" + Environment.NewLine + recovery.Summary
                    + Environment.NewLine + "Recorded evidence:" + Environment.NewLine
                    + string.Join(Environment.NewLine, recovery.Evidence.Select(item => "• " + item))
                    + Environment.NewLine + "Next steps:" + Environment.NewLine
                    + string.Join(Environment.NewLine, recovery.NextSteps.Select(item => "• " + item));
            ReplaceItems(SessionTimelineRows, timeline.Events.Select(RuntimeEventRow.FromEntry));
            OnPropertyChanged(nameof(HasSessionTimelineRows));
        }
        catch (OperationCanceledException ex) when (SelectedSession?.SessionId == requestedSessionId)
        {
            ReplaceItems(SessionTimelineRows, []);
            OnPropertyChanged(nameof(HasSessionTimelineRows));
            SelectedSessionDetail = $"Session detail load canceled: {ex.Message}";
        }
        catch (HttpRequestException ex) when (SelectedSession?.SessionId == requestedSessionId)
        {
            ReplaceItems(SessionTimelineRows, []);
            OnPropertyChanged(nameof(HasSessionTimelineRows));
            SelectedSessionDetail = $"Session detail load failed: {ex.Message}";
        }
        catch (InvalidOperationException ex) when (SelectedSession?.SessionId == requestedSessionId)
        {
            ReplaceItems(SessionTimelineRows, []);
            OnPropertyChanged(nameof(HasSessionTimelineRows));
            SelectedSessionDetail = $"Session detail load failed: {ex.Message}";
        }
    }

    private void ResetSessionsPageForFilterChange()
    {
        if (SessionsPage != 1)
            SessionsPage = 1;
    }

    private static SessionState? TryParseSessionState(string? state)
        => Enum.TryParse<SessionState>(state, ignoreCase: true, out var parsed) ? parsed : null;
}

public sealed class SessionRow
{
    public required string SessionId { get; init; }
    public required string ChannelId { get; init; }
    public required string SenderId { get; init; }
    public required string State { get; init; }
    public required string Source { get; init; }
    public required string LastActivity { get; init; }
    public required string Snippet { get; init; }
    public int HistoryTurns { get; init; }
    public long TotalTokens { get; init; }
    public bool IsActive { get; init; }

    public static SessionRow FromSummary(SessionSummary item, string source, IReadOnlyDictionary<string, string> snippets)
        => new()
        {
            SessionId = item.Id,
            ChannelId = item.ChannelId,
            SenderId = item.SenderId,
            State = item.State.ToString(),
            Source = source,
            LastActivity = item.LastActiveAt.ToLocalTime().ToString("g"),
            HistoryTurns = item.HistoryTurns,
            TotalTokens = item.TotalInputTokens + item.TotalOutputTokens,
            IsActive = item.IsActive,
            Snippet = snippets.TryGetValue(item.Id, out var snippet) ? snippet : ""
        };
}
