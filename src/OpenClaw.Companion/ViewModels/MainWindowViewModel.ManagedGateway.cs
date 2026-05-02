using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenClaw.Companion.Services;

namespace OpenClaw.Companion.ViewModels;

public sealed partial class MainWindowViewModel
{
    [ObservableProperty]
    private string _localGatewayStatus = "Local gateway not checked.";

    [ObservableProperty]
    private string _localGatewayAvailability = "";

    [ObservableProperty]
    private string _localGatewayConfigPath = "";

    [ObservableProperty]
    private bool _localGatewayConfigExists;

    [ObservableProperty]
    private bool _localGatewayCanStart;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRunLocalGatewaySetup))]
    private bool _localGatewayCanRunSetup;

    [ObservableProperty]
    private bool _localGatewayIsHealthy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRunLocalGatewaySetup))]
    private bool _isManagedGatewayBusy;

    [ObservableProperty]
    private bool _autoStartLocalGateway = true;

    [ObservableProperty]
    private string _setupProvider = "openai";

    [ObservableProperty]
    private string _setupModel = "gpt-4o";

    [ObservableProperty]
    private string _setupModelPreset = "";

    [ObservableProperty]
    private string _setupWorkspacePath = "";

    [ObservableProperty]
    private string _setupApiKey = "";

    public bool CanRunLocalGatewaySetup => LocalGatewayCanRunSetup && !IsManagedGatewayBusy;

    public async Task InitializeLocalGatewayAsync()
    {
        await RefreshLocalGatewayAsync();
        if (AutoStartLocalGateway && LocalGatewayConfigExists && LocalGatewayCanStart && !IsConnected)
            await StartLocalGatewayCoreAsync(connectAfterStart: true);
    }

    [RelayCommand]
    private async Task RefreshLocalGatewayAsync()
    {
        RefreshManagedGatewayStateCore();
        LocalGatewayIsHealthy = await _managedGateway.IsHealthyAsync(AuthToken, CancellationToken.None);
        LocalGatewayStatus = LocalGatewayIsHealthy
            ? $"Local gateway is running at {_managedGateway.BaseUrl}."
            : LocalGatewayConfigExists
                ? "Local gateway is configured but not running."
                : "Local gateway is not set up.";
    }

    [RelayCommand]
    private async Task SetupAndStartLocalGatewayAsync()
    {
        if (IsManagedGatewayBusy)
            return;

        IsManagedGatewayBusy = true;
        try
        {
            SaveSettings();
            var setupApiKey = string.IsNullOrWhiteSpace(SetupApiKey) ? null : SetupApiKey;
            LocalGatewayStatus = "Writing local setup...";
            var result = await _managedGateway.RunSetupAsync(new ManagedGatewaySetupRequest(
                SetupProvider,
                SetupModel,
                setupApiKey,
                string.IsNullOrWhiteSpace(SetupModelPreset) ? null : SetupModelPreset,
                string.IsNullOrWhiteSpace(SetupWorkspacePath) ? _managedGateway.WorkspacePath : SetupWorkspacePath,
                _managedGateway.ConfigPath), CancellationToken.None);

            if (!result.IsSuccess)
            {
                LocalGatewayStatus = "Local setup failed.";
                AddSystemMessageCore($"Local setup failed: {result.Message}");
                return;
            }

            SetupApiKey = "";
            if (setupApiKey is null)
                _settingsStore.ClearProviderApiKey();
            else
                _settingsStore.SaveProviderApiKey(setupApiKey, AllowPlaintextTokenFallback);
            RefreshManagedGatewayStateCore();
            ShowSettingsWarningIfNeeded();
            AddSystemMessageCore("Local setup completed.");
            await StartLocalGatewayCoreAsync(connectAfterStart: true);
        }
        finally
        {
            IsManagedGatewayBusy = false;
        }
    }

    [RelayCommand]
    private async Task StartLocalGatewayAsync()
    {
        if (IsManagedGatewayBusy)
            return;

        IsManagedGatewayBusy = true;
        try
        {
            await StartLocalGatewayCoreAsync(connectAfterStart: true);
        }
        finally
        {
            IsManagedGatewayBusy = false;
        }
    }

    [RelayCommand]
    private async Task StopLocalGatewayAsync()
    {
        if (IsManagedGatewayBusy)
            return;

        IsManagedGatewayBusy = true;
        try
        {
            if (IsConnected)
                await DisconnectAsync();
            await _managedGateway.StopAsync(CancellationToken.None);
            LocalGatewayIsHealthy = false;
            LocalGatewayStatus = "Local gateway stopped.";
        }
        finally
        {
            IsManagedGatewayBusy = false;
        }
    }

    private async Task StartLocalGatewayCoreAsync(bool connectAfterStart)
    {
        RefreshManagedGatewayStateCore();
        if (!LocalGatewayCanStart)
        {
            LocalGatewayStatus = "Bundled gateway is unavailable.";
            AddSystemMessageCore("The bundled OpenClaw gateway was not found in this Companion build.");
            return;
        }

        if (!LocalGatewayConfigExists)
        {
            LocalGatewayStatus = "Local setup is required before the gateway can start.";
            return;
        }

        LocalGatewayStatus = "Starting local gateway...";
        var result = await _managedGateway.StartAsync(AuthToken, CancellationToken.None);
        LocalGatewayStatus = result.Message;
        if (!result.IsSuccess)
        {
            AddSystemMessageCore(result.Message);
            return;
        }

        LocalGatewayIsHealthy = true;
        ServerUrl = _managedGateway.WebSocketUrl;
        SaveSettings();

        if (connectAfterStart && !IsConnected)
            await ConnectAsync();
    }

    private void RefreshManagedGatewayStateCore()
    {
        LocalGatewayCanStart = _managedGateway.CanStartGateway;
        LocalGatewayCanRunSetup = _managedGateway.CanRunSetup;
        LocalGatewayConfigExists = _managedGateway.HasConfig;
        LocalGatewayConfigPath = _managedGateway.ConfigPath;
        LocalGatewayAvailability = _managedGateway.DescribeAvailability();
        if (string.IsNullOrWhiteSpace(SetupWorkspacePath))
            SetupWorkspacePath = _managedGateway.WorkspacePath;
    }

    partial void OnAutoStartLocalGatewayChanged(bool value)
    {
        if (!_isLoadingSettings)
            SaveSettings();
    }

    partial void OnSetupProviderChanged(string value)
    {
        if (value.Equals("ollama", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(SetupModel) || SetupModel.Equals("gpt-4o", StringComparison.OrdinalIgnoreCase))
                SetupModel = "llama3.2";
            if (string.IsNullOrWhiteSpace(SetupModelPreset))
                SetupModelPreset = "ollama-general";
        }
        else if (string.IsNullOrWhiteSpace(SetupModel) || SetupModel.Equals("llama3.2", StringComparison.OrdinalIgnoreCase))
        {
            SetupModel = "gpt-4o";
            SetupModelPreset = "";
        }

        if (!_isLoadingSettings)
            SaveSettings();
    }

    partial void OnSetupModelChanged(string value)
    {
        if (!_isLoadingSettings)
            SaveSettings();
    }

    partial void OnSetupModelPresetChanged(string value)
    {
        if (!_isLoadingSettings)
            SaveSettings();
    }

    partial void OnSetupWorkspacePathChanged(string value)
    {
        if (!_isLoadingSettings)
            SaveSettings();
    }
}
