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
    private HttpClient CreateUpdateHttpClient() => new() { Timeout = TimeSpan.FromMinutes(15) };

    [RelayCommand]
    private async Task ConfigureUpdateTrustAsync()
    {
        try
        {
            if (!await ConfirmMutationAsync("Trust update publisher", "Only continue after independently verifying this publisher's public key.", "Trust publisher")) return;
            using var http = CreateUpdateHttpClient();
            new BundleUpdater(http, BundleUpdater.DefaultRoot).ConfigureTrust(new(UpdateManifestUrl, await File.ReadAllTextAsync(UpdatePublicKeyPath)));
            UpdateStatus = "Publisher configured.";
        }
        catch (Exception ex) { UpdateStatus = ex.Message; }
    }
    [RelayCommand]
    private async Task CheckBundleUpdateAsync()
    {
        try
        {
            using var http = CreateUpdateHttpClient();
            var release = await new BundleUpdater(http, BundleUpdater.DefaultRoot).CheckAsync(UpdateChannel, EmptyToNull(UpdateVersion), CancellationToken.None);
            UpdateVersion = release.Version;
            UpdateStatus = $"Verified {release.Channel} release {release.Version}. Install keeps the previous bundle for rollback.";
        }
        catch (Exception ex) { UpdateStatus = ex.Message; }
    }
    [RelayCommand]
    private async Task InstallBundleUpdateAsync()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(UpdateVersion)) throw new InvalidOperationException("Check for updates first to select a version.");
            if (!await ConfirmMutationAsync("Install update", $"Install version {UpdateVersion}? Restart afterward to use the new bundle.", "Install")) return;
            using var http = CreateUpdateHttpClient();
            UpdateStatus = "Downloading and verifying the complete bundle…";
            await new BundleUpdater(http, BundleUpdater.DefaultRoot).InstallAsync(UpdateChannel, UpdateVersion, CancellationToken.None);
            UpdateStatus = "Installed. Restart into the active bundle when ready. Your configuration and data were preserved.";
        }
        catch (Exception ex) { UpdateStatus = ex.Message; }
    }
    [RelayCommand]
    private async Task RollbackBundleUpdateAsync()
    {
        try
        {
            if (!await ConfirmMutationAsync("Roll back update", "Activate the previous bundle? Restart afterward. This does not downgrade your saved configuration or data.", "Roll back")) return;
            using var http = CreateUpdateHttpClient();
            new BundleUpdater(http, BundleUpdater.DefaultRoot).Rollback();
            UpdateStatus = "Previous bundle activated. Restart when ready.";
        }
        catch (Exception ex) { UpdateStatus = ex.Message; }
    }
    [RelayCommand]
    private async Task RestartUpdatedCompanionAsync()
    {
        try
        {
            if (!await ConfirmMutationAsync("Restart Companion", "Stop the managed gateway and restart Companion using the active bundle?", "Restart")) return;
            using var http = CreateUpdateHttpClient();
            var executable = new BundleUpdater(http, BundleUpdater.DefaultRoot).GetActiveExecutable("companion");
            await _managedGateway.StopAsync(CancellationToken.None);
            Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false });
            if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown();
        }
        catch (Exception ex) { UpdateStatus = ex.Message; }
    }
}
