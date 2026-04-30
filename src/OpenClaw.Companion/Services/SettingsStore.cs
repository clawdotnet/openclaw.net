using System.Text.Json;
using OpenClaw.Companion.Models;

namespace OpenClaw.Companion.Services;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly ProtectedTokenStore _tokenStore;

    public string SettingsPath { get; }
    public string? LastWarning { get; private set; }

    public SettingsStore(string? baseDir = null, ProtectedTokenStore? tokenStore = null)
    {
        var dir = baseDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OpenClaw",
            "Companion");
        SettingsPath = Path.Combine(dir, "settings.json");
        _tokenStore = tokenStore ?? new ProtectedTokenStore(dir);
    }

    public CompanionSettings Load()
    {
        LastWarning = null;
        try
        {
            if (!File.Exists(SettingsPath))
                return new CompanionSettings();

            var json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<CompanionSettings>(json, JsonOptions) ?? new CompanionSettings();
            settings.AuthToken = _tokenStore.LoadToken(settings.AllowPlaintextTokenFallback)
                ?? TryReadLegacyAuthToken(json, settings.RememberToken);
            LastWarning = _tokenStore.LastWarning;
            return settings;
        }
        catch
        {
            return new CompanionSettings();
        }
    }

    public void Save(CompanionSettings settings)
    {
        LastWarning = null;
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);

        var toSave = new CompanionSettings
        {
            ServerUrl = settings.ServerUrl,
            Username = settings.Username,
            OperatorTokenLabel = settings.OperatorTokenLabel,
            RememberToken = settings.RememberToken,
            AllowPlaintextTokenFallback = settings.AllowPlaintextTokenFallback,
            DebugMode = settings.DebugMode,
            ApprovalDesktopNotificationsEnabled = settings.ApprovalDesktopNotificationsEnabled,
            ApprovalDesktopNotificationsOnlyWhenUnfocused = settings.ApprovalDesktopNotificationsOnlyWhenUnfocused,
            AutoStartLocalGateway = settings.AutoStartLocalGateway,
            SetupProvider = settings.SetupProvider,
            SetupModel = settings.SetupModel,
            SetupModelPreset = settings.SetupModelPreset,
            SetupWorkspacePath = settings.SetupWorkspacePath
        };

        var json = JsonSerializer.Serialize(toSave, JsonOptions);
        var tmp = SettingsPath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, SettingsPath, overwrite: true);

        if (!settings.RememberToken || string.IsNullOrWhiteSpace(settings.AuthToken))
        {
            _tokenStore.ClearToken();
            return;
        }

        _tokenStore.SaveToken(
            settings.AuthToken,
            settings.AllowPlaintextTokenFallback,
            out var warning);
        LastWarning = warning;
    }

    private static string? TryReadLegacyAuthToken(string json, bool rememberToken)
    {
        if (!rememberToken)
            return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.TryGetProperty("AuthToken", out var authToken) && authToken.ValueKind == JsonValueKind.String)
                return authToken.GetString();
            if (root.TryGetProperty("authToken", out authToken) && authToken.ValueKind == JsonValueKind.String)
                return authToken.GetString();
        }
        catch
        {
        }

        return null;
    }
}
