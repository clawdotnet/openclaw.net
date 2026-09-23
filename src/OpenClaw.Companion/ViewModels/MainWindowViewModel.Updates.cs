using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenClaw.Core.Updates;
namespace OpenClaw.Companion.ViewModels;
public partial class MainWindowViewModel
{
    [ObservableProperty] private string _updateManifestUrl = "";
    [ObservableProperty] private string _updatePublicKeyPath = "";
    [ObservableProperty] private string _updateChannel = "stable";
    [ObservableProperty] private string _updateVersion = "";
    [ObservableProperty] private string _updateStatus = "Configure the publisher manifest and independently verified public key once. Updates install as complete, recoverable bundles.";
    private (string Channel, string Version)? _checkedUpdate;
    partial void OnUpdateChannelChanged(string value) => _checkedUpdate = null;
    partial void OnUpdateVersionChanged(string value) => _checkedUpdate = null;
    internal Func<HttpClient> UpdateHttpClientFactory { get; set; } = () => new() { Timeout = TimeSpan.FromMinutes(15) };
    internal string UpdateStorageRoot { get; set; } = BundleUpdater.DefaultRoot;
    private HttpClient CreateUpdateHttpClient() => UpdateHttpClientFactory();

    [RelayCommand]
    private async Task ConfigureUpdateTrustAsync()
    {
        try
        {
            if (!await ConfirmMutationAsync("Trust update publisher", "Only continue after independently verifying this publisher's public key.", "Trust publisher")) return;
            using var http = CreateUpdateHttpClient();
            new BundleUpdater(http, UpdateStorageRoot).ConfigureTrust(new(UpdateManifestUrl, await File.ReadAllTextAsync(UpdatePublicKeyPath)));
            _checkedUpdate = null;
            UpdateStatus = "Publisher configured.";
        }
        catch (Exception ex) when (IsUserFacingOperationError(ex)) { UpdateStatus = ex.Message; }
    }
    [RelayCommand]
    private async Task CheckBundleUpdateAsync()
    {
        try
        {
            _checkedUpdate = null;
            var channel = UpdateChannel;
            var pin = UpdateVersion;
            using var http = CreateUpdateHttpClient();
            var release = await new BundleUpdater(http, UpdateStorageRoot).CheckAsync(channel, EmptyToNull(pin), CancellationToken.None);
            if (channel != UpdateChannel || pin != UpdateVersion) return;
            _checkedUpdate = (release.Channel, release.Version);
            UpdateStatus = $"Verified {release.Channel} release {release.Version}. Install keeps the previous bundle for rollback.";
        }
        catch (Exception ex) when (IsUserFacingOperationError(ex)) { UpdateStatus = ex.Message; }
    }
    [RelayCommand]
    private async Task InstallBundleUpdateAsync()
    {
        try
        {
            var selection = _checkedUpdate ?? throw new InvalidOperationException("Check for updates first to select a version.");
            if (!await ConfirmMutationAsync("Install update", $"Install version {selection.Version}? Restart afterward to use the new bundle.", "Install")) return;
            using var http = CreateUpdateHttpClient();
            UpdateStatus = "Downloading and verifying the complete bundle…";
            await new BundleUpdater(http, UpdateStorageRoot).InstallAsync(selection.Channel, selection.Version, CancellationToken.None);
            UpdateStatus = "Installed. Restart into the active bundle when ready. Your configuration and data were preserved.";
        }
        catch (Exception ex) when (IsUserFacingOperationError(ex)) { UpdateStatus = ex.Message; }
    }
    [RelayCommand]
    private async Task RollbackBundleUpdateAsync()
    {
        try
        {
            if (!await ConfirmMutationAsync("Roll back update", "Activate the previous bundle? Restart afterward. This does not downgrade your saved configuration or data.", "Roll back")) return;
            using var http = CreateUpdateHttpClient();
            new BundleUpdater(http, UpdateStorageRoot).Rollback();
            UpdateStatus = "Previous bundle activated. Restart when ready.";
        }
        catch (Exception ex) when (IsUserFacingOperationError(ex)) { UpdateStatus = ex.Message; }
    }
    [RelayCommand]
    private async Task RestartUpdatedCompanionAsync()
    {
        try
        {
            if (!await ConfirmMutationAsync("Restart Companion", "Stop the managed gateway and restart Companion using the active bundle?", "Restart")) return;
            using var http = CreateUpdateHttpClient();
            var executable = new BundleUpdater(http, UpdateStorageRoot).GetActiveExecutable("companion");
            await _managedGateway.StopAsync(CancellationToken.None);
            Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false });
            if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown();
        }
        catch (Exception ex) when (IsUserFacingOperationError(ex)) { UpdateStatus = ex.Message; }
    }
}
