using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenClaw.Core.Models;

namespace OpenClaw.Companion.ViewModels;

public sealed partial class ConfigurationEdit : ObservableObject
{
    public required string Key { get; init; }
    public required JsonValueKind Kind { get; init; }
    public string Label => char.ToUpperInvariant(Key[0]) + System.Text.RegularExpressions.Regex.Replace(Key[1..], "([a-z])([A-Z])", "$1 $2");
    public string Hint => ConfigurationSettingChoices.For(Key) is { Count: > 0 } choices ? string.Join(", ", choices) : Kind is JsonValueKind.True or JsonValueKind.False ? "true or false" : Kind == JsonValueKind.Number ? "Number" : "Text";
    [ObservableProperty] private string _value = "";
    public JsonElement ToJson()
    {
        if (Kind == JsonValueKind.Null && string.IsNullOrEmpty(Value))
        {
            using var empty = JsonDocument.Parse("null");
            return empty.RootElement.Clone();
        }
        if (Kind is JsonValueKind.String or JsonValueKind.Null)
            return JsonSerializer.SerializeToElement(Value, ConfigurationJsonContext.Default.String);
        using var document = JsonDocument.Parse(Kind is JsonValueKind.True or JsonValueKind.False ? Value.ToLowerInvariant() : Value);
        return document.RootElement.Clone();
    }
}

public sealed partial class MainWindowViewModel
{
    [ObservableProperty] private bool _isConfigurationMode;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowChatWelcome))] private bool _isConfigurationOpen;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowChatWelcome))] private bool _isLocalSetupOpen;
    [ObservableProperty] private bool _isConfigurationBusy;
    [ObservableProperty] private string _configurationStatus = "Describe a change or choose a setting. Review before applying.";
    [ObservableProperty] private string? _selectedConfigurationKey;
    private ConfigurationState? _configurationState;
    public bool ShowChatWelcome => HasNoMessages && !IsConfigurationOpen && !IsLocalSetupOpen;
    public ObservableCollection<ConfigurationEdit> ConfigurationEdits { get; } = [];
    public ObservableCollection<string> ConfigurationKeys { get; } = [];

    [RelayCommand]
    private void BeginChatSetup()
    {
        SelectedSectionIndex = 2;
        IsConfigurationOpen = false;
        IsLocalSetupOpen = true;
        ConfigurationStatus = "Choose your provider and model. Your API key is entered only in the secure field.";
    }

    [RelayCommand]
    private async Task OpenConfigurationAsync()
    {
        if (IsConfigurationBusy) return;
        SelectedSectionIndex = 2;
        IsLocalSetupOpen = false;
        IsConfigurationOpen = true;
        IsConfigurationBusy = true;
        try { await LoadConfigurationAsync(); }
        finally { IsConfigurationBusy = false; }
    }

    private async Task LoadConfigurationAsync()
    {
        using var client = RequireIntegrationClient(s => ConfigurationStatus = s);
        if (client is null) return;
        try
        {
            _configurationState = await client.GetConfigurationAsync();
            ConfigurationEdits.Clear();
            ReplaceItems(ConfigurationKeys, _configurationState.Values.Keys.Order());
            SelectedConfigurationKey = ConfigurationKeys.FirstOrDefault();
            ConfigurationStatus = _configurationState.Message;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidOperationException or OperationCanceledException)
        {
            _configurationState = null;
            ConfigurationKeys.Clear();
            ConfigurationEdits.Clear();
            ConfigurationStatus = "Could not load settings. Check the gateway connection and admin token, or use local setup.";
        }
    }

    [RelayCommand]
    private void AddConfigurationField()
    {
        if (_configurationState is null || SelectedConfigurationKey is not { } key || ConfigurationEdits.Any(e => e.Key == key)
            || !_configurationState.Values.TryGetValue(key, out var value)) return;
        ConfigurationEdits.Add(new() { Key = key, Kind = value.ValueKind, Value = value.ValueKind == JsonValueKind.Null ? "" : value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText() });
    }

    private async Task ProposeConfigurationAsync(string instruction)
    {
        IsLocalSetupOpen = false;
        IsConfigurationOpen = true;
        IsConfigurationBusy = true;
        try
        {
            // Keep the revision and pending edits together; the server rejects a stale draft.
            if (_configurationState is null) await LoadConfigurationAsync();
            if (_configurationState is null) return;
            using var client = RequireIntegrationClient(s => ConfigurationStatus = s);
            if (client is null) return;
            var result = await client.PreviewConfigurationAsync(new() { Instruction = instruction, Revision = _configurationState.Revision });
            ConfigurationStatus = string.Join(" ", new[] { result.Message }.Concat(result.Errors));
            if (!result.Success) return;
            foreach (var (key, value) in result.Changes)
            {
                var edited = ConfigurationEdits.FirstOrDefault(edit => edit.Key == key);
                var text = value.ValueKind == JsonValueKind.Null ? "" : value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText();
                if (edited is not null) edited.Value = text;
                else ConfigurationEdits.Add(new() { Key = key, Kind = value.ValueKind, Value = text });
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidOperationException or OperationCanceledException) { ConfigurationStatus = "The request could not be completed. Retry or choose a setting below."; }
        finally { IsConfigurationBusy = false; }
    }

    [RelayCommand]
    private async Task ApplyConfigurationAsync()
    {
        if (IsConfigurationBusy || _configurationState is null || ConfigurationEdits.Count == 0) return;
        IsConfigurationBusy = true;
        try
        {
            var request = new ConfigurationRequest
            {
                Revision = _configurationState.Revision,
                Changes = ConfigurationEdits.ToDictionary(e => e.Key, e => e.ToJson())
            };
            using var client = RequireIntegrationClient(s => ConfigurationStatus = s);
            if (client is null) return;
            var result = await client.ApplyConfigurationAsync(request);
            ConfigurationStatus = string.Join(" ", new[] { result.Message }.Concat(result.Errors));
            if (!result.Success) return;
            _configurationState = result;
            ConfigurationEdits.Clear();
            IsConfigurationMode = false;
            AddSystemMessage(result.Message + (result.RestartRequired ? " Pending: " + string.Join(", ", result.RestartRequiredFields) : ""));
        }
        catch (JsonException) { ConfigurationStatus = "Enter valid numbers or true/false for the indicated fields."; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidOperationException or OperationCanceledException) { ConfigurationStatus = ex is HttpRequestException ? ex.Message : "Could not confirm the save. Reload settings before retrying."; }
        finally { IsConfigurationBusy = false; }
    }

    [RelayCommand]
    private void CancelConfiguration()
    {
        ConfigurationEdits.Clear();
        _configurationState = null;
        IsConfigurationOpen = false;
        IsLocalSetupOpen = false;
        IsConfigurationMode = false;
    }

    private async Task<bool> TryHandleConfigurationChatAsync(string text)
    {
        var lower = text.ToLowerInvariant().TrimEnd('.', '!', '?');
        if (lower is "set up my assistant" or "setup" or "set up" or "/setup")
        {
            InputText = "";
            BeginChatSetup();
            return true;
        }
        if (lower is "use dark mode" or "switch to dark mode" or "use light mode" or "switch to light mode")
        {
            IsDarkTheme = lower.Contains("dark");
            InputText = "";
            AddSystemMessage($"Switched to {(IsDarkTheme ? "dark" : "light")} mode.");
            return true;
        }
        if (IsConfigurationMode || text.StartsWith("/configure ", StringComparison.OrdinalIgnoreCase))
        {
            InputText = "";
            await ProposeConfigurationAsync(text.StartsWith("/configure ", StringComparison.OrdinalIgnoreCase) ? text[11..] : text);
            return true;
        }
        return false;
    }
}
