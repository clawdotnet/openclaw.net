using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenClaw.Client;
using OpenClaw.Companion.Models;
using OpenClaw.Companion.Services;
using OpenClaw.Core.Models;

namespace OpenClaw.Companion.ViewModels;

public sealed partial class MainWindowViewModel : ViewModelBase
{
    private readonly SettingsStore _settingsStore;
    private readonly GatewayWebSocketClient _client;
    private bool _isLoadingSettings;
    private int? _activeAssistantMessageIndex;
    private string? _activeAssistantReplyToMessageId;
    private string? _lastSettingsWarning;

    [ObservableProperty]
    private string _serverUrl = "ws://127.0.0.1:18789/ws";

    [ObservableProperty]
    private string _username = "";

    [ObservableProperty]
    private string _password = "";

    [ObservableProperty]
    private string _operatorTokenLabel = "companion";

    [ObservableProperty]
    private string _authToken = "";

    [ObservableProperty]
    private bool _rememberToken;

    [ObservableProperty]
    private bool _allowPlaintextTokenFallback;

    [ObservableProperty]
    private bool _debugMode;

    [ObservableProperty]
    private bool _approvalDesktopNotificationsEnabled = true;

    [ObservableProperty]
    private bool _approvalDesktopNotificationsOnlyWhenUnfocused = true;

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _status = "Disconnected";

    [ObservableProperty]
    private string _operatorIdentity = "No operator account token loaded.";

    [ObservableProperty]
    private string _operatorRole = OperatorRoleNames.Viewer;

    [ObservableProperty]
    private string _operatorAuthMode = "account_token";

    [ObservableProperty]
    private bool _isBootstrapAdmin;

    [ObservableProperty]
    private bool _canManageAdmin;

    [ObservableProperty]
    private string _adminStatus = "Admin status not loaded.";

    [ObservableProperty]
    private string _deploymentStatus = "Deployment status not loaded.";

    [ObservableProperty]
    private string _inputText = "";

    public ObservableCollection<ChatMessage> Messages { get; } = new();

    public MainWindowViewModel()
        : this(new SettingsStore(), new GatewayWebSocketClient(), null)
    {
    }

    public MainWindowViewModel(
        SettingsStore settingsStore,
        GatewayWebSocketClient client,
        Func<string, string?, OpenClawHttpClient>? adminClientFactory = null)
    {
        _settingsStore = settingsStore;
        _client = client;
        _adminClientFactory = adminClientFactory ?? ((baseUrl, authToken) => new OpenClawHttpClient(baseUrl, authToken));

        _client.OnTextMessage += HandleInboundText;
        _client.OnError += err => AddSystemMessage($"Error: {err}");

        LoadSettings();
    }

    private void LoadSettings()
    {
        _isLoadingSettings = true;
        var settings = _settingsStore.Load();
        try
        {
            ServerUrl = settings.ServerUrl;
            Username = settings.Username;
            OperatorTokenLabel = string.IsNullOrWhiteSpace(settings.OperatorTokenLabel) ? "companion" : settings.OperatorTokenLabel;
            RememberToken = settings.RememberToken;
            AllowPlaintextTokenFallback = settings.AllowPlaintextTokenFallback;
            AuthToken = settings.AuthToken ?? "";
            DebugMode = settings.DebugMode;
            ApprovalDesktopNotificationsEnabled = settings.ApprovalDesktopNotificationsEnabled;
            ApprovalDesktopNotificationsOnlyWhenUnfocused = settings.ApprovalDesktopNotificationsOnlyWhenUnfocused;
        }
        finally
        {
            _isLoadingSettings = false;
        }

        ShowSettingsWarningIfNeeded();
    }

    private void SaveSettings()
    {
        _settingsStore.Save(new CompanionSettings
        {
            ServerUrl = ServerUrl,
            Username = Username,
            OperatorTokenLabel = OperatorTokenLabel,
            RememberToken = RememberToken,
            AllowPlaintextTokenFallback = AllowPlaintextTokenFallback,
            DebugMode = DebugMode,
            ApprovalDesktopNotificationsEnabled = ApprovalDesktopNotificationsEnabled,
            ApprovalDesktopNotificationsOnlyWhenUnfocused = ApprovalDesktopNotificationsOnlyWhenUnfocused,
            AuthToken = string.IsNullOrWhiteSpace(AuthToken) ? null : AuthToken
        });
        ShowSettingsWarningIfNeeded();
    }

    private void HandleInboundText(string payload)
    {
        if (DebugMode)
        {
            Dispatcher.UIThread.Post(() => AddAssistantMessage(payload));
            return;
        }

        if (TryParseEnvelope(payload, out var envelope))
        {
            Dispatcher.UIThread.Post(() => ApplyEnvelope(envelope));
            return;
        }

        Dispatcher.UIThread.Post(() => AddAssistantMessage(payload));
    }

    private void ApplyEnvelope(InboundEnvelope envelope)
    {
        switch (envelope.Type)
        {
            case "typing_start":
                return;

            case "typing_stop":
            case "assistant_done":
                ClearActiveAssistantMessage(envelope.InReplyToMessageId);
                return;

            case "assistant_chunk":
            case "text_delta":
                AppendAssistantChunk(envelope.Text, envelope.InReplyToMessageId);
                return;

            case "assistant_message":
                SetAssistantMessage(envelope.Text, envelope.InReplyToMessageId);
                return;

            case "error":
                ClearActiveAssistantMessage(envelope.InReplyToMessageId);
                AddSystemMessageCore(string.IsNullOrWhiteSpace(envelope.Text)
                    ? "An unknown error occurred."
                    : envelope.Text);
                return;

            case "tool_start":
                if (!string.IsNullOrWhiteSpace(envelope.Text))
                    AddSystemMessageCore($"Agent invoked tool: {envelope.Text}");
                return;

            case "tool_approval_required":
                AddSystemMessageCore("Tool approval is required in the web client.");
                return;

            default:
                if (!string.IsNullOrWhiteSpace(envelope.Text))
                    AddSystemMessageCore(envelope.Text);
                return;
        }
    }

    private void AppendAssistantChunk(string? text, string? inReplyToMessageId)
    {
        if (string.IsNullOrEmpty(text))
            return;

        var index = EnsureActiveAssistantMessage(inReplyToMessageId);
        var current = Messages[index];
        Messages[index] = current with { Text = current.Text + text };
    }

    private void SetAssistantMessage(string? text, string? inReplyToMessageId)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            ClearActiveAssistantMessage(inReplyToMessageId);
            return;
        }

        if (TryGetActiveAssistantMessageIndex(inReplyToMessageId, out var index))
        {
            Messages[index] = Messages[index] with { Text = text };
            ClearActiveAssistantMessage(inReplyToMessageId);
            return;
        }

        AddAssistantMessage(text);
    }

    private void AddAssistantMessage(string text)
    {
        Messages.Add(new ChatMessage { Role = ChatRole.Assistant, Text = text });
        ClearActiveAssistantMessage(replyToMessageId: null);
    }

    private int EnsureActiveAssistantMessage(string? inReplyToMessageId)
    {
        if (TryGetActiveAssistantMessageIndex(inReplyToMessageId, out var index))
            return index;

        Messages.Add(new ChatMessage { Role = ChatRole.Assistant, Text = string.Empty });
        _activeAssistantMessageIndex = Messages.Count - 1;
        _activeAssistantReplyToMessageId = NormalizeReplyToMessageId(inReplyToMessageId);
        return _activeAssistantMessageIndex.Value;
    }

    private bool TryGetActiveAssistantMessageIndex(string? inReplyToMessageId, out int index)
    {
        if (_activeAssistantMessageIndex is int candidate
            && candidate >= 0
            && candidate < Messages.Count
            && Messages[candidate].Role == ChatRole.Assistant
            && string.Equals(_activeAssistantReplyToMessageId, NormalizeReplyToMessageId(inReplyToMessageId), StringComparison.Ordinal))
        {
            index = candidate;
            return true;
        }

        index = -1;
        return false;
    }

    private void ClearActiveAssistantMessage(string? replyToMessageId)
    {
        if (replyToMessageId is not null
            && !string.Equals(_activeAssistantReplyToMessageId, NormalizeReplyToMessageId(replyToMessageId), StringComparison.Ordinal))
        {
            return;
        }

        _activeAssistantMessageIndex = null;
        _activeAssistantReplyToMessageId = null;
    }

    private static string? NormalizeReplyToMessageId(string? replyToMessageId) =>
        string.IsNullOrWhiteSpace(replyToMessageId) ? null : replyToMessageId;

    private static bool TryParseEnvelope(string payload, out InboundEnvelope envelope)
    {
        envelope = new InboundEnvelope(string.Empty, string.Empty, string.Empty);

        if (payload.Length == 0 || payload[0] != '{')
            return false;

        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;

            if (!root.TryGetProperty("type", out var typeProp))
                return false;

            var type = typeProp.GetString();
            if (string.IsNullOrWhiteSpace(type))
                return false;

            var text = root.TryGetProperty("text", out var textProp)
                ? textProp.GetString()
                : root.TryGetProperty("content", out var contentProp)
                    ? contentProp.GetString()
                    : null;
            var inReplyToMessageId = root.TryGetProperty("inReplyToMessageId", out var replyProp)
                ? replyProp.GetString()
                : null;

            envelope = new InboundEnvelope(type, text, inReplyToMessageId);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void AddSystemMessage(string text)
    {
        Dispatcher.UIThread.Post(() => AddSystemMessageCore(text));
    }

    private void AddSystemMessageCore(string text)
    {
        Messages.Add(new ChatMessage { Role = ChatRole.System, Text = text });
    }

    private void ShowSettingsWarningIfNeeded()
    {
        var warning = _settingsStore.LastWarning;
        if (string.IsNullOrWhiteSpace(warning) || string.Equals(_lastSettingsWarning, warning, StringComparison.Ordinal))
            return;

        _lastSettingsWarning = warning;
        AddSystemMessageCore(warning);
    }

    partial void OnDebugModeChanged(bool value)
    {
        if (_isLoadingSettings)
            return;

        if (value)
            ClearActiveAssistantMessage(replyToMessageId: null);

        SaveSettings();
    }

    partial void OnApprovalDesktopNotificationsEnabledChanged(bool value)
    {
        if (_isLoadingSettings)
            return;
        SaveSettings();
    }

    partial void OnApprovalDesktopNotificationsOnlyWhenUnfocusedChanged(bool value)
    {
        if (_isLoadingSettings)
            return;
        SaveSettings();
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (IsBusy)
            return;

        IsBusy = true;
        try
        {
            if (!Uri.TryCreate(ServerUrl, UriKind.Absolute, out var uri))
            {
                AddSystemMessage("Invalid server URL.");
                return;
            }

            SaveSettings();

            Status = "Connecting…";
            await _client.ConnectAsync(uri, string.IsNullOrWhiteSpace(AuthToken) ? null : AuthToken, CancellationToken.None);
            IsConnected = true;
            Status = "Connected";
            await LoadAdminStatusAsyncInternal();
            await LoadWhatsAppSetupAsync();
        }
        catch (Exception ex)
        {
            IsConnected = false;
            Status = "Disconnected";
            AddSystemMessage($"Connect failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        if (IsBusy)
            return;

        IsBusy = true;
        try
        {
            await _client.DisconnectAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            AddSystemMessage($"Disconnect failed: {ex.Message}");
        }
        finally
        {
            CancelWhatsAppAuthStream();
            IsConnected = false;
            Status = "Disconnected";
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task IssueOperatorTokenAsync()
    {
        if (IsBusy)
            return;

        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Password))
        {
            AddSystemMessage("Username and password are required to issue an operator token.");
            return;
        }

        IsBusy = true;
        try
        {
            using var client = CreateAdminClient(authToken: null, out var error);
            if (client is null)
            {
                AddSystemMessage(error ?? "Invalid gateway URL.");
                return;
            }

            var issued = await client.ExchangeOperatorTokenAsync(new OperatorTokenExchangeRequest
            {
                Username = Username,
                Password = Password,
                Label = string.IsNullOrWhiteSpace(OperatorTokenLabel) ? "companion" : OperatorTokenLabel
            }, CancellationToken.None);

            AuthToken = issued.Token;
            RememberToken = true;
            Password = "";
            SaveSettings();
            ApplyOperatorIdentity(
                issued.AuthMode,
                issued.Account?.Role ?? OperatorRoleNames.Viewer,
                issued.Account?.DisplayName,
                issued.Account?.Username,
                isBootstrapAdmin: false);
            AdminStatus = $"Issued operator token '{issued.TokenInfo?.Id ?? "unknown"}'.";
            AddSystemMessage($"Issued operator token for {issued.Account?.Username ?? Username}.");
            await LoadAdminStatusAsyncInternal();
        }
        catch (Exception ex)
        {
            AddSystemMessage($"Operator token exchange failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task LoadAdminStatusAsync()
        => await LoadAdminStatusAsyncInternal();

    private async Task LoadAdminStatusAsyncInternal()
    {
        using var client = CreateAdminClient(out var error);
        if (client is null)
        {
            AdminStatus = error ?? "Invalid gateway URL.";
            DeploymentStatus = "Deployment status unavailable.";
            return;
        }

        if (string.IsNullOrWhiteSpace(AuthToken))
        {
            ApplyOperatorIdentity("account_token", OperatorRoleNames.Viewer, displayName: null, username: Username, isBootstrapAdmin: false);
            AdminStatus = "No operator token loaded. Exchange credentials for a token to use admin surfaces.";
            DeploymentStatus = "Deployment status unavailable until a token is loaded.";
            return;
        }

        try
        {
            var auth = await client.GetAuthSessionAsync(CancellationToken.None);
            var setup = await client.GetSetupStatusAsync(CancellationToken.None);
            ApplyOperatorIdentity(auth.AuthMode, auth.Role, auth.DisplayName, auth.Username, auth.IsBootstrapAdmin);
            AdminStatus = auth.IsBootstrapAdmin
                ? "Using bootstrap/breakglass admin auth."
                : $"Authenticated as {auth.DisplayName ?? auth.Username ?? "operator"} via {auth.AuthMode}.";
            DeploymentStatus = string.Join(
                Environment.NewLine,
                [
                    $"Profile: {setup.Profile}",
                    $"Reachable base URL: {setup.ReachableBaseUrl}",
                    $"Bind: {setup.BindAddress}:{setup.Port}",
                    $"Bootstrap token enabled: {setup.BootstrapTokenEnabled}",
                    $"Allowed auth modes: {string.Join(", ", setup.AllowedAuthModes)}",
                    $"Minimum plugin trust: {setup.MinimumPluginTrustLevel}",
                    $"Warnings: {(setup.Warnings.Count == 0 ? "none" : string.Join("; ", setup.Warnings))}"
                ]);
        }
        catch (Exception ex)
        {
            AdminStatus = $"Admin status load failed: {ex.Message}";
            DeploymentStatus = "Deployment status unavailable.";
        }
    }

    private void ApplyOperatorIdentity(string authMode, string role, string? displayName, string? username, bool isBootstrapAdmin)
    {
        OperatorAuthMode = authMode;
        OperatorRole = role;
        IsBootstrapAdmin = isBootstrapAdmin;
        CanManageAdmin = isBootstrapAdmin || OperatorRoleNames.CanAccess(role, OperatorRoleNames.Operator);
        OperatorIdentity = isBootstrapAdmin
            ? "Bootstrap admin"
            : string.IsNullOrWhiteSpace(displayName)
                ? (string.IsNullOrWhiteSpace(username) ? "Authenticated operator" : username)
                : $"{displayName} ({username ?? "operator"})";
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        if (IsBusy)
            return;

        var text = InputText.Trim();
        if (text.Length == 0)
            return;

        if (!_client.IsConnected)
        {
            AddSystemMessage("Not connected.");
            return;
        }

        InputText = "";
        Messages.Add(new ChatMessage { Role = ChatRole.User, Text = text });

        try
        {
            var msgId = Guid.NewGuid().ToString("n");
            await _client.SendUserMessageAsync(text, msgId, replyToMessageId: null, CancellationToken.None);
        }
        catch (Exception ex)
        {
            AddSystemMessage($"Send failed: {ex.Message}");
        }
    }

    private sealed record InboundEnvelope(string Type, string? Text, string? InReplyToMessageId);
}
