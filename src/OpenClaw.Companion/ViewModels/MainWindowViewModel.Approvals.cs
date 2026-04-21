using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenClaw.Companion.Services;
using OpenClaw.Core.Models;
using OpenClaw.Core.Pipeline;

namespace OpenClaw.Companion.ViewModels;

public enum ApprovalsPollingMode
{
    Paused,
    Background,
    Active
}

public enum ApprovalQueueSeverity
{
    None,
    Light,
    Heavy
}

public sealed partial class MainWindowViewModel
{
    // Polling cadence thresholds used by ResolvePollingInterval; mirrors the v2 scope:
    // active tab + focused window => 1.5s, otherwise 5s when running, Paused when minimized.
    private static readonly TimeSpan ActivePollInterval = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan BackgroundPollInterval = TimeSpan.FromSeconds(5);

    // Severity tiers for the tab-badge color. 1-2 = light, 3+ = heavy (operator should act).
    private const int HeavyQueueThreshold = 3;

    private CancellationTokenSource? _approvalsPollCts;
    private readonly SemaphoreSlim _approvalsRefreshLock = new(1, 1);
    private readonly HashSet<string> _knownApprovalIds = new(StringComparer.Ordinal);
    private bool _hasSeededKnownApprovals;
    private IDesktopNotifier? _desktopNotifier;

    [ObservableProperty]
    private bool _isApprovalsBusy;

    [ObservableProperty]
    private string _approvalsStatus = "Approvals queue not loaded.";

    [ObservableProperty]
    private DateTimeOffset? _approvalsLastPolled;

    [ObservableProperty]
    private bool _isApprovalsTabActive;

    [ObservableProperty]
    private bool _isWindowActive = true;

    [ObservableProperty]
    private bool _isWindowMinimized;

    [ObservableProperty]
    private ApprovalsPollingMode _approvalsPollingMode = ApprovalsPollingMode.Background;

    [ObservableProperty]
    private string _approvalHistoryStatus = "History not loaded.";

    [ObservableProperty]
    private bool _isHistoryBusy;

    public ObservableCollection<PendingApprovalItem> PendingApprovals { get; } = [];

    public ObservableCollection<ApprovalHistoryItem> ApprovalHistory { get; } = [];

    public int PendingApprovalsCount => PendingApprovals.Count;

    public bool HasPendingApprovals => PendingApprovals.Count > 0;

    public ApprovalQueueSeverity QueueSeverity => PendingApprovals.Count switch
    {
        0 => ApprovalQueueSeverity.None,
        < HeavyQueueThreshold => ApprovalQueueSeverity.Light,
        _ => ApprovalQueueSeverity.Heavy
    };

    public bool QueueSeverityIsLight => QueueSeverity == ApprovalQueueSeverity.Light;

    public bool QueueSeverityIsHeavy => QueueSeverity == ApprovalQueueSeverity.Heavy;

    public bool DesktopNotifierAvailable => _desktopNotifier?.IsAvailable ?? false;

    public string DesktopNotifierDescription => _desktopNotifier?.PlatformDescription ?? "not configured";

    internal void AttachDesktopNotifier(IDesktopNotifier notifier)
    {
        _desktopNotifier = notifier;
        OnPropertyChanged(nameof(DesktopNotifierAvailable));
        OnPropertyChanged(nameof(DesktopNotifierDescription));
    }

    partial void OnIsApprovalsBusyChanged(bool value) => RefreshApprovalsCommand.NotifyCanExecuteChanged();

    partial void OnIsApprovalsTabActiveChanged(bool value) => RecomputeApprovalsPollingMode();

    partial void OnIsWindowActiveChanged(bool value) => RecomputeApprovalsPollingMode();

    partial void OnIsWindowMinimizedChanged(bool value) => RecomputeApprovalsPollingMode();

    private void NotifyPendingApprovalsChanged()
    {
        OnPropertyChanged(nameof(PendingApprovalsCount));
        OnPropertyChanged(nameof(HasPendingApprovals));
        OnPropertyChanged(nameof(QueueSeverity));
        OnPropertyChanged(nameof(QueueSeverityIsLight));
        OnPropertyChanged(nameof(QueueSeverityIsHeavy));
    }

    [RelayCommand(CanExecute = nameof(CanInteractWithApprovals))]
    private async Task RefreshApprovalsAsync()
    {
        await RefreshApprovalsInternalAsync(CancellationToken.None);
    }

    [RelayCommand(CanExecute = nameof(CanInteractWithApprovals))]
    private async Task LoadApprovalHistoryAsync()
    {
        await LoadApprovalHistoryInternalAsync();
    }

    [RelayCommand]
    private async Task ApproveApprovalAsync(PendingApprovalItem? item)
    {
        if (item is null)
            return;
        await SubmitApprovalDecisionAsync(item, approved: true);
    }

    [RelayCommand]
    private async Task DenyApprovalAsync(PendingApprovalItem? item)
    {
        if (item is null)
            return;
        await SubmitApprovalDecisionAsync(item, approved: false);
    }

    public void StartApprovalsPolling()
    {
        StopApprovalsPolling();

        _approvalsPollCts = new CancellationTokenSource();
        var token = _approvalsPollCts.Token;

        _ = Task.Run(async () =>
        {
            await RefreshApprovalsInternalAsync(token);
            while (!token.IsCancellationRequested)
            {
                var interval = ResolvePollingInterval();
                if (interval is null)
                {
                    // Paused: wait a short probe interval before re-checking state.
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(2), token);
                    }
                    catch (TaskCanceledException)
                    {
                        return;
                    }
                    continue;
                }

                try
                {
                    await Task.Delay(interval.Value, token);
                }
                catch (TaskCanceledException)
                {
                    return;
                }

                if (token.IsCancellationRequested)
                    return;

                await RefreshApprovalsInternalAsync(token);
            }
        }, token);
    }

    public void StopApprovalsPolling()
    {
        _approvalsPollCts?.Cancel();
        _approvalsPollCts?.Dispose();
        _approvalsPollCts = null;
    }

    private TimeSpan? ResolvePollingInterval()
        => ApprovalsPollingMode switch
        {
            ApprovalsPollingMode.Paused => null,
            ApprovalsPollingMode.Active => ActivePollInterval,
            _ => BackgroundPollInterval
        };

    private void RecomputeApprovalsPollingMode()
    {
        ApprovalsPollingMode = (IsWindowMinimized, IsWindowActive && IsApprovalsTabActive) switch
        {
            (true, _) => ApprovalsPollingMode.Paused,
            (false, true) => ApprovalsPollingMode.Active,
            _ => ApprovalsPollingMode.Background
        };
    }

    private bool CanInteractWithApprovals() => !IsApprovalsBusy;

    private async Task RefreshApprovalsInternalAsync(CancellationToken ct)
    {
        // Skip if another refresh (poll tick or manual click) is already in flight.
        if (!await _approvalsRefreshLock.WaitAsync(TimeSpan.Zero, CancellationToken.None))
            return;

        Dispatcher.UIThread.Post(() => IsApprovalsBusy = true);
        try
        {
            using var client = CreateAdminClient(out var error);
            if (client is null)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    ApprovalsStatus = error ?? "Invalid gateway URL.";
                    PendingApprovals.Clear();
                    _knownApprovalIds.Clear();
                    _hasSeededKnownApprovals = false;
                    NotifyPendingApprovalsChanged();
                });
                return;
            }

            if (string.IsNullOrWhiteSpace(AuthToken))
            {
                Dispatcher.UIThread.Post(() =>
                {
                    ApprovalsStatus = "Operator token required to load approvals.";
                    PendingApprovals.Clear();
                    _knownApprovalIds.Clear();
                    _hasSeededKnownApprovals = false;
                    NotifyPendingApprovalsChanged();
                });
                return;
            }

            var response = await client.GetIntegrationApprovalsAsync(channelId: null, senderId: null, ct);
            var items = response.Items
                .Select(PendingApprovalItem.FromRequest)
                .ToList();

            var newlyArrived = DetectNewApprovals(items);

            Dispatcher.UIThread.Post(() =>
            {
                MergePendingApprovals(items);
                ApprovalsLastPolled = DateTimeOffset.UtcNow;
                ApprovalsStatus = items.Count == 0
                    ? "No pending approvals."
                    : $"{items.Count} pending approval{(items.Count == 1 ? "" : "s")}.";
                NotifyPendingApprovalsChanged();
            });

            if (newlyArrived.Count > 0)
                await FireNotificationsForAsync(newlyArrived, ct);
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown / cancellation.
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() =>
            {
                ApprovalsStatus = $"Approvals refresh failed: {ex.Message}";
            });
        }
        finally
        {
            Dispatcher.UIThread.Post(() => IsApprovalsBusy = false);
            _approvalsRefreshLock.Release();
        }
    }

    internal IReadOnlyList<PendingApprovalItem> DetectNewApprovals(IReadOnlyList<PendingApprovalItem> latest)
    {
        // The first poll seeds the "known" set silently — we don't want to fire a
        // flurry of notifications for items that already existed when the Companion started.
        if (!_hasSeededKnownApprovals)
        {
            foreach (var item in latest)
                _knownApprovalIds.Add(item.ApprovalId);
            _hasSeededKnownApprovals = true;
            return [];
        }

        var newItems = latest.Where(item => !_knownApprovalIds.Contains(item.ApprovalId)).ToList();
        foreach (var item in newItems)
            _knownApprovalIds.Add(item.ApprovalId);

        // Prune known ids that are no longer pending so the set doesn't grow unboundedly.
        var stillPending = latest.Select(static i => i.ApprovalId).ToHashSet(StringComparer.Ordinal);
        _knownApprovalIds.RemoveWhere(id => !stillPending.Contains(id));

        return newItems;
    }

    private async Task FireNotificationsForAsync(IReadOnlyList<PendingApprovalItem> newItems, CancellationToken ct)
    {
        var notifier = _desktopNotifier;
        if (notifier is null || !notifier.IsAvailable)
            return;
        if (!ApprovalDesktopNotificationsEnabled)
            return;
        if (ApprovalDesktopNotificationsOnlyWhenUnfocused && IsWindowActive && !IsWindowMinimized)
            return;

        // Coalesce into one notification when multiple arrive at once.
        if (newItems.Count == 1)
        {
            var item = newItems[0];
            var title = $"Approval needed: {item.ToolName}";
            var body = $"from {item.Origin}";
            await notifier.NotifyAsync(title, body, ct);
            return;
        }

        var combinedTitle = $"{newItems.Count} approvals pending";
        var preview = string.Join(", ", newItems.Take(3).Select(i => i.ToolName));
        var combinedBody = newItems.Count > 3 ? $"{preview}, and {newItems.Count - 3} more" : preview;
        await notifier.NotifyAsync(combinedTitle, combinedBody, ct);
    }

    internal void MergePendingApprovals(IReadOnlyList<PendingApprovalItem> latest)
    {
        var latestIds = latest.Select(static i => i.ApprovalId).ToHashSet(StringComparer.Ordinal);
        for (var i = PendingApprovals.Count - 1; i >= 0; i--)
        {
            if (!latestIds.Contains(PendingApprovals[i].ApprovalId))
                PendingApprovals.RemoveAt(i);
        }

        var existingIds = PendingApprovals.Select(static i => i.ApprovalId).ToHashSet(StringComparer.Ordinal);
        foreach (var item in latest)
        {
            if (!existingIds.Contains(item.ApprovalId))
                PendingApprovals.Add(item);
        }
    }

    private async Task SubmitApprovalDecisionAsync(PendingApprovalItem item, bool approved)
    {
        using var client = CreateAdminClient(out var error);
        if (client is null)
        {
            ApprovalsStatus = error ?? "Invalid gateway URL.";
            return;
        }

        if (string.IsNullOrWhiteSpace(AuthToken))
        {
            ApprovalsStatus = "Operator token required to decide approvals.";
            return;
        }

        // Optimistically remove; re-add on failure so the user sees immediate feedback.
        var index = PendingApprovals.IndexOf(item);
        if (index >= 0)
            PendingApprovals.RemoveAt(index);
        _knownApprovalIds.Remove(item.ApprovalId);
        NotifyPendingApprovalsChanged();

        try
        {
            var response = approved
                ? await client.ApproveToolRequestAsync(item.ApprovalId, CancellationToken.None)
                : await client.DenyToolRequestAsync(item.ApprovalId, CancellationToken.None);

            if (response.Success)
            {
                ApprovalsStatus = approved
                    ? $"Approved '{item.ToolName}'."
                    : $"Denied '{item.ToolName}'.";
            }
            else
            {
                ApprovalsStatus = $"Decision failed: {response.Error ?? "server rejected the request"}.";
                if (index >= 0 && index <= PendingApprovals.Count)
                    PendingApprovals.Insert(index, item);
                _knownApprovalIds.Add(item.ApprovalId);
                NotifyPendingApprovalsChanged();
            }
        }
        catch (Exception ex)
        {
            ApprovalsStatus = $"Decision failed: {ex.Message}";
            if (index >= 0 && index <= PendingApprovals.Count)
                PendingApprovals.Insert(index, item);
            _knownApprovalIds.Add(item.ApprovalId);
            NotifyPendingApprovalsChanged();
        }
    }

    private async Task LoadApprovalHistoryInternalAsync()
    {
        using var client = CreateAdminClient(out var error);
        if (client is null)
        {
            ApprovalHistoryStatus = error ?? "Invalid gateway URL.";
            return;
        }

        if (string.IsNullOrWhiteSpace(AuthToken))
        {
            ApprovalHistoryStatus = "Operator token required to load history.";
            return;
        }

        IsHistoryBusy = true;
        try
        {
            var query = new ApprovalHistoryQuery { Limit = 100 };
            var response = await client.GetIntegrationApprovalHistoryAsync(query, CancellationToken.None);
            var items = response.Items.Select(ApprovalHistoryItem.FromEntry).ToList();

            ApprovalHistory.Clear();
            foreach (var item in items)
                ApprovalHistory.Add(item);

            ApprovalHistoryStatus = items.Count == 0
                ? "No history yet."
                : $"{items.Count} decision{(items.Count == 1 ? "" : "s")} loaded.";
        }
        catch (Exception ex)
        {
            ApprovalHistoryStatus = $"History load failed: {ex.Message}";
        }
        finally
        {
            IsHistoryBusy = false;
        }
    }
}

public sealed class PendingApprovalItem
{
    public required string ApprovalId { get; init; }
    public required string ToolName { get; init; }
    public required string ChannelId { get; init; }
    public required string SenderId { get; init; }
    public required string SessionId { get; init; }
    public required string ArgumentsPreview { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public string? Action { get; init; }
    public bool IsMutation { get; init; }
    public string Summary { get; init; } = "";

    public string Origin => string.IsNullOrWhiteSpace(SenderId) ? ChannelId : $"{ChannelId} · {SenderId}";

    public static PendingApprovalItem FromRequest(ToolApprovalRequest request)
        => new()
        {
            ApprovalId = request.ApprovalId,
            ToolName = request.ToolName,
            ChannelId = request.ChannelId,
            SenderId = request.SenderId,
            SessionId = request.SessionId,
            ArgumentsPreview = BuildPreview(request.Arguments),
            CreatedAt = request.CreatedAt,
            Action = request.Action,
            IsMutation = request.IsMutation,
            Summary = request.Summary ?? ""
        };

    internal static string BuildPreview(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
            return "(no arguments)";

        try
        {
            using var doc = JsonDocument.Parse(arguments);
            var pretty = JsonSerializer.Serialize(
                doc.RootElement,
                new JsonSerializerOptions { WriteIndented = true });
            return pretty.Length > 500 ? pretty[..500] + "…" : pretty;
        }
        catch
        {
            return arguments.Length > 500 ? arguments[..500] + "…" : arguments;
        }
    }
}

public sealed class ApprovalHistoryItem
{
    public required string ApprovalId { get; init; }
    public required string ToolName { get; init; }
    public required string Origin { get; init; }
    public required DateTimeOffset TimestampUtc { get; init; }
    public bool? Approved { get; init; }
    public string? Actor { get; init; }
    public string? DecisionSource { get; init; }

    public string OutcomeLabel => Approved switch
    {
        true => "Approved",
        false => "Denied",
        _ => "Recorded"
    };

    public string OutcomeColor => Approved switch
    {
        true => "#30d158",
        false => "#ff453a",
        _ => "#8e8e93"
    };

    public static ApprovalHistoryItem FromEntry(ApprovalHistoryEntry entry)
    {
        var origin = string.IsNullOrWhiteSpace(entry.SenderId)
            ? entry.ChannelId
            : $"{entry.ChannelId} · {entry.SenderId}";
        var actor = string.IsNullOrWhiteSpace(entry.ActorDisplayName)
            ? entry.ActorSenderId ?? entry.ActorChannelId
            : entry.ActorDisplayName;

        return new ApprovalHistoryItem
        {
            ApprovalId = entry.ApprovalId,
            ToolName = entry.ToolName,
            Origin = origin,
            TimestampUtc = entry.DecisionAtUtc ?? entry.TimestampUtc,
            Approved = entry.Approved,
            Actor = actor,
            DecisionSource = entry.DecisionSource
        };
    }
}
