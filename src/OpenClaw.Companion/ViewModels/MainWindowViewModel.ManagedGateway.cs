using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenClaw.Companion.Services;
using OpenClaw.Core.Setup;

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
    [NotifyPropertyChangedFor(nameof(CanRunEmbeddedLocalModelCommands))]
    private bool _localGatewayCanRunSetup;

    [ObservableProperty]
    private bool _localGatewayIsHealthy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRunLocalGatewaySetup))]
    [NotifyPropertyChangedFor(nameof(CanRunEmbeddedLocalModelCommands))]
    private bool _isManagedGatewayBusy;

    [ObservableProperty]
    private bool _autoStartLocalGateway = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRunEmbeddedLocalModelCommands))]
    private string _setupProvider = "openai";

    [ObservableProperty]
    private string _setupModel = "gpt-4o";

    [ObservableProperty]
    private string _setupModelPreset = "";

    [ObservableProperty]
    private string _setupWorkspacePath = "";

    [ObservableProperty]
    private string _setupApiKey = "";

    [ObservableProperty]
    private string _setupLocalModelPath = "";

    [ObservableProperty]
    private string _embeddedLocalModelStatus = "Local model status not checked. Video uses sampled frames; LiteRT packages require an experimental adapter.";

    public bool CanRunLocalGatewaySetup => LocalGatewayCanRunSetup && !IsManagedGatewayBusy;

    public bool CanRunEmbeddedLocalModelCommands =>
        CanRunLocalGatewaySetup && IsEmbeddedSetupProvider();

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
    private async Task RefreshEmbeddedLocalModelStatusAsync()
    {
        if (IsManagedGatewayBusy)
            return;

        if (!IsEmbeddedSetupProvider())
        {
            EmbeddedLocalModelStatus = "Choose the Embedded provider before running local model commands.";
            return;
        }

        IsManagedGatewayBusy = true;
        try
        {
            var result = await _managedGateway.RunLocalModelCommandAsync(
                "status",
                ResolveEmbeddedPackageId(),
                modelPath: null,
                CancellationToken.None);
            EmbeddedLocalModelStatus = result.Message.Trim();
            if (!result.IsSuccess)
                AddSystemMessageCore($"Local model status failed: {result.Message}");
        }
        finally
        {
            IsManagedGatewayBusy = false;
        }
    }

    [RelayCommand]
    private async Task InstallEmbeddedLocalModelAsync()
    {
        if (IsManagedGatewayBusy)
            return;

        if (!IsEmbeddedSetupProvider())
        {
            EmbeddedLocalModelStatus = "Choose the Embedded provider before installing a local model.";
            return;
        }

        if (string.IsNullOrWhiteSpace(SetupLocalModelPath))
        {
            EmbeddedLocalModelStatus = "Choose a local model file path (.gguf or .litertlm) before installing.";
            return;
        }

        IsManagedGatewayBusy = true;
        try
        {
            SaveSettings();
            var result = await _managedGateway.RunLocalModelCommandAsync(
                "install",
                ResolveEmbeddedPackageId(),
                SetupLocalModelPath,
                CancellationToken.None);
            EmbeddedLocalModelStatus = result.Message.Trim();
            AddSystemMessageCore(result.IsSuccess
                ? "Embedded local model installed."
                : $"Embedded local model install failed: {result.Message}");
        }
        finally
        {
            IsManagedGatewayBusy = false;
        }
    }

    [RelayCommand]
    private async Task VerifyEmbeddedLocalModelAsync()
    {
        if (IsManagedGatewayBusy)
            return;

        if (!IsEmbeddedSetupProvider())
        {
            EmbeddedLocalModelStatus = "Choose the Embedded provider before verifying a local model.";
            return;
        }

        IsManagedGatewayBusy = true;
        try
        {
            var result = await _managedGateway.RunLocalModelCommandAsync(
                "verify",
                ResolveEmbeddedPackageId(),
                modelPath: null,
                CancellationToken.None);
            EmbeddedLocalModelStatus = result.Message.Trim();
            AddSystemMessageCore(result.IsSuccess
                ? "Embedded local model verified."
                : $"Embedded local model verification failed: {result.Message}");
        }
        finally
        {
            IsManagedGatewayBusy = false;
        }
    }

    [RelayCommand]
    private async Task RemoveEmbeddedLocalModelAsync()
    {
        if (IsManagedGatewayBusy)
            return;

        if (!IsEmbeddedSetupProvider())
        {
            EmbeddedLocalModelStatus = "Choose the Embedded provider before removing a local model.";
            return;
        }

        IsManagedGatewayBusy = true;
        try
        {
            var result = await _managedGateway.RunLocalModelCommandAsync(
                "remove",
                ResolveEmbeddedPackageId(),
                modelPath: null,
                CancellationToken.None);
            EmbeddedLocalModelStatus = result.Message.Trim();
            AddSystemMessageCore(result.IsSuccess
                ? "Embedded local model removed."
                : $"Embedded local model removal failed: {result.Message}");
        }
        finally
        {
            IsManagedGatewayBusy = false;
        }
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

    private string ResolveEmbeddedPackageId()
    {
        if (TryResolveEmbeddedPackageId(SetupModelPreset, out var presetPackageId))
            return presetPackageId;

        if (TryResolveEmbeddedPackageId(SetupModel, out var modelPackageId))
            return modelPackageId;

        return "gemma-local-small-q4";
    }

    private static bool TryResolveEmbeddedPackageId(string? value, out string packageId)
    {
        packageId = "";
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var id = value.Trim();
        if (!LocalModelPackageCatalog.TryGet(id, out var package) || package is null)
            return false;

        packageId = package.Id;
        return true;
    }

    private bool IsEmbeddedSetupProvider()
        => SetupProvider.Equals("embedded", StringComparison.OrdinalIgnoreCase);

    partial void OnAutoStartLocalGatewayChanged(bool value)
    {
        if (!_isLoadingSettings)
            SaveSettings();
    }

    partial void OnSetupProviderChanged(string value)
    {
        if (value.Equals("ollama", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(SetupModel) ||
                SetupModel.Equals("gpt-4o", StringComparison.OrdinalIgnoreCase) ||
                SetupModel.Equals("gemma-local-small-q4", StringComparison.OrdinalIgnoreCase))
                SetupModel = "llama3.2";
            if (string.IsNullOrWhiteSpace(SetupModelPreset))
                SetupModelPreset = "ollama-general";
        }
        else if (value.Equals("embedded", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(SetupModel) ||
                SetupModel.Equals("gpt-4o", StringComparison.OrdinalIgnoreCase) ||
                SetupModel.Equals("llama3.2", StringComparison.OrdinalIgnoreCase))
                SetupModel = "gemma-local-small-q4";
            if (string.IsNullOrWhiteSpace(SetupModelPreset) ||
                SetupModelPreset.Equals("ollama-general", StringComparison.OrdinalIgnoreCase))
                SetupModelPreset = "embedded-gemma-small-q4";
            SetupApiKey = "";
        }
        else if (string.IsNullOrWhiteSpace(SetupModel) ||
                 SetupModel.Equals("llama3.2", StringComparison.OrdinalIgnoreCase) ||
                 SetupModel.Equals("gemma-local-small-q4", StringComparison.OrdinalIgnoreCase))
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

    partial void OnSetupLocalModelPathChanged(string value)
    {
        if (!_isLoadingSettings)
            SaveSettings();
    }
}
