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

    [ObservableProperty]
    private string _serverUrl = "ws://127.0.0.1:18789/ws";

    [ObservableProperty]
    private string _authToken = "";

    [ObservableProperty]
    private bool _rememberToken;

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
        var settings = _settingsStore.Load();
        ServerUrl = settings.ServerUrl;
        RememberToken = settings.RememberToken;
        AuthToken = settings.AuthToken ?? "";
    }

    private void SaveSettings()
    {
        _settingsStore.Save(new CompanionSettings
        {
            ServerUrl = ServerUrl,
            RememberToken = RememberToken,
            AuthToken = string.IsNullOrWhiteSpace(AuthToken) ? null : AuthToken
        });
    }

    private void HandleInboundText(string payload)
    {
        // The gateway may reply in raw text mode or JSON envelope mode.
        // Prefer extracting assistant_message.text when possible.
        var text = TryExtractAssistantText(payload) ?? payload;
        Dispatcher.UIThread.Post(() => Messages.Add(new ChatMessage { Role = ChatRole.Assistant, Text = text }));
    }

    private static string? TryExtractAssistantText(string payload)
    {
        if (payload.Length == 0 || payload[0] != '{')
            return null;

        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            if (!root.TryGetProperty("type", out var typeProp))
                return null;
            if (!string.Equals(typeProp.GetString(), "assistant_message", StringComparison.Ordinal))
                return null;

            return root.TryGetProperty("text", out var textProp) ? textProp.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    private void AddSystemMessage(string text)
    {
        Dispatcher.UIThread.Post(() => Messages.Add(new ChatMessage { Role = ChatRole.System, Text = text }));
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
}
