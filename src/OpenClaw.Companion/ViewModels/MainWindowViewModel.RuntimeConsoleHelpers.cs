using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenClaw.Client;
using OpenClaw.Companion.Services;

namespace OpenClaw.Companion.ViewModels;

public sealed partial class MainWindowViewModel
{
    private IConfirmationDialogService _confirmationDialogService = new DenyConfirmationDialogService();

    [ObservableProperty]
    private int _selectedSectionIndex;

    [ObservableProperty]
    private bool _connectionSettingsExpanded;

    public string GatewayStatusBadge => IsConnected ? "Connected" : "Disconnected";
    public string LocalGatewayHealthBadge => LocalGatewayIsHealthy ? "Healthy" : "Local offline";
    public string PendingApprovalsBadge => $"{PendingApprovalsCount} pending";
    public string SetupProviderSummary => string.IsNullOrWhiteSpace(SetupProvider) ? "provider not set" : $"{SetupProvider} / {SetupModel}";
    public bool HasMessages => Messages.Count > 0;
    public bool HasNoMessages => Messages.Count == 0;
    public bool HasEmbeddedLocalModelDisabledReason => !string.IsNullOrWhiteSpace(EmbeddedLocalModelDisabledReason);
    public string EmbeddedLocalModelDisabledReason => IsEmbeddedSetupProvider()
        ? ""
        : "Choose the Embedded provider before using local model package commands.";

    internal void AttachConfirmationDialogService(IConfirmationDialogService dialogService)
        => _confirmationDialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));

    partial void OnIsConnectedChanged(bool value) => OnPropertyChanged(nameof(GatewayStatusBadge));

    partial void OnLocalGatewayIsHealthyChanged(bool value)
    {
        OnPropertyChanged(nameof(LocalGatewayHealthBadge));
        OnPropertyChanged(nameof(DashboardLocalGatewayStatus));
    }

    partial void OnLocalGatewayStatusChanged(string value) => OnPropertyChanged(nameof(DashboardLocalGatewayStatus));

    [RelayCommand]
    private void NavigateToSection(string? section)
    {
        SelectedSectionIndex = section?.Trim().ToLowerInvariant() switch
        {
            "home" => 0,
            "setup" => 1,
            "chat" => 2,
            "canvas" => 3,
            "sessions" => 4,
            "approvals" => 5,
            "workflows" => 6,
            "automations" => 7,
            "runtimeevents" or "runtime events" => 8,
            "plugins" or "plugins & channels" => 9,
            "profiles" or "memory" or "memory & profiles" => 10,
            "providers" or "models" or "models & providers" => 11,
            "admin" => 12,
            "whatsapp" => 13,
            "payments" or "payment lab" => 14,
            _ => SelectedSectionIndex
        };
    }

    private OpenClawHttpClient? CreateIntegrationClient(out string? error)
        => CreateAdminClient(out error);

    private OpenClawHttpClient? RequireIntegrationClient(Action<string> setStatus)
    {
        var client = CreateIntegrationClient(out var error);
        if (client is not null)
            return client;

        setStatus(error ?? "Invalid gateway URL.");
        return null;
    }

    private async Task<bool> ConfirmMutationAsync(string title, string message, string confirmText = "Continue")
        => await _confirmationDialogService.ConfirmAsync(title, message, confirmText, "Cancel", CancellationToken.None);

    private static void ReplaceItems<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
            target.Add(item);
    }

    private static string FormatTimestamp(DateTimeOffset? value)
        => value is null ? "never" : value.Value.ToLocalTime().ToString("g");

    private static string FormatUtc(DateTimeOffset? value)
        => value is null ? "" : value.Value.ToUniversalTime().ToString("u");

    private static string JoinCompact(IEnumerable<string>? values)
    {
        var items = values?.Where(static value => !string.IsNullOrWhiteSpace(value)).ToArray() ?? [];
        return items.Length == 0 ? "none" : string.Join(", ", items);
    }
}
