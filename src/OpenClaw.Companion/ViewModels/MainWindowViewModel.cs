using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenClaw.Companion.Models;
using OpenClaw.Companion.Services;

namespace OpenClaw.Companion.ViewModels;

public sealed partial class MainWindowViewModel : ViewModelBase
{
    private readonly SettingsStore _settingsStore;
    private readonly GatewayWebSocketClient _client;
    private bool _isLoadingSettings;
    private int? _activeAssistantMessageIndex;
    private string? _activeAssistantReplyToMessageId;

    [ObservableProperty]
    private string _serverUrl = "ws://127.0.0.1:18789/ws";

    [ObservableProperty]
    private string _authToken = "";

    [ObservableProperty]
    private bool _rememberToken;

    [ObservableProperty]
    private bool _debugMode;

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _status = "Disconnected";

    [ObservableProperty]
    private string _inputText = "";

    public ObservableCollection<ChatMessage> Messages { get; } = new();

    public MainWindowViewModel()
        : this(new SettingsStore(), new GatewayWebSocketClient())
    {
    }

    public MainWindowViewModel(SettingsStore settingsStore, GatewayWebSocketClient client)
    {
        _settingsStore = settingsStore;
        _client = client;

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
            RememberToken = settings.RememberToken;
            AuthToken = settings.AuthToken ?? "";
            DebugMode = settings.DebugMode;
        }
        finally
        {
            _isLoadingSettings = false;
        }
    }

    private void SaveSettings()
    {
        _settingsStore.Save(new CompanionSettings
        {
            ServerUrl = ServerUrl,
            RememberToken = RememberToken,
            DebugMode = DebugMode,
            AuthToken = string.IsNullOrWhiteSpace(AuthToken) ? null : AuthToken
        });
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

    partial void OnDebugModeChanged(bool value)
    {
        if (_isLoadingSettings)
            return;

        if (value)
            ClearActiveAssistantMessage(replyToMessageId: null);

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
            IsConnected = false;
            Status = "Disconnected";
            IsBusy = false;
        }
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
