using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenClaw.Core.Pipeline;

namespace OpenClaw.Companion.ViewModels;

public sealed partial class MainWindowViewModel
{
    private CancellationTokenSource? _approvalsPollCts;
    private readonly SemaphoreSlim _approvalsRefreshLock = new(1, 1);

    [ObservableProperty]
    private bool _isApprovalsBusy;

    [ObservableProperty]
    private string _approvalsStatus = "Approvals queue not loaded.";

    [ObservableProperty]
    private DateTimeOffset? _approvalsLastPolled;

    public ObservableCollection<PendingApprovalItem> PendingApprovals { get; } = [];

    public int PendingApprovalsCount => PendingApprovals.Count;

    public bool HasPendingApprovals => PendingApprovals.Count > 0;

    partial void OnIsApprovalsBusyChanged(bool value) => RefreshApprovalsCommand.NotifyCanExecuteChanged();

    private void NotifyPendingApprovalsChanged()
    {
        OnPropertyChanged(nameof(PendingApprovalsCount));
        OnPropertyChanged(nameof(HasPendingApprovals));
    }

    [RelayCommand(CanExecute = nameof(CanInteractWithApprovals))]
    private async Task RefreshApprovalsAsync()
    {
        await RefreshApprovalsInternalAsync(CancellationToken.None);
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

    public void StartApprovalsPolling(TimeSpan interval)
    {
        StopApprovalsPolling();
        if (interval <= TimeSpan.Zero)
            return;

        _approvalsPollCts = new CancellationTokenSource();
        var token = _approvalsPollCts.Token;

        _ = Task.Run(async () =>
        {
            await RefreshApprovalsInternalAsync(token);
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(interval, token);
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

    private bool CanInteractWithApprovals() => !IsApprovalsBusy;

    private async Task RefreshApprovalsInternalAsync(CancellationToken ct)
    {
        // Skip if another refresh (poll tick or manual click) is already in flight.
        // Pass CancellationToken.None because TimeSpan.Zero never blocks; we just want
        // a non-throwing try-acquire.
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
                    NotifyPendingApprovalsChanged();
                });
                return;
            }

            var response = await client.GetIntegrationApprovalsAsync(channelId: null, senderId: null, ct);
            var items = response.Items
                .Select(PendingApprovalItem.FromRequest)
                .ToList();

            Dispatcher.UIThread.Post(() =>
            {
                MergePendingApprovals(items);
                ApprovalsLastPolled = DateTimeOffset.UtcNow;
                ApprovalsStatus = items.Count == 0
                    ? "No pending approvals."
                    : $"{items.Count} pending approval{(items.Count == 1 ? "" : "s")}.";
                NotifyPendingApprovalsChanged();
            });
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
                NotifyPendingApprovalsChanged();
            }
        }
        catch (Exception ex)
        {
            ApprovalsStatus = $"Decision failed: {ex.Message}";
            if (index >= 0 && index <= PendingApprovals.Count)
                PendingApprovals.Insert(index, item);
            NotifyPendingApprovalsChanged();
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

    private static string BuildPreview(string arguments)
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
