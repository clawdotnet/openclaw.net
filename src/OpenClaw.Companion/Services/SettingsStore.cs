using System.Diagnostics;
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
    private readonly ProtectedTokenStore _providerKeyStore;
    private readonly string _providerKeyMarkerPath;

    public string SettingsPath { get; }
    public string? LastWarning { get; private set; }

    public SettingsStore(
        string? baseDir = null,
        ProtectedTokenStore? tokenStore = null,
        ProtectedTokenStore? providerKeyStore = null)
    {
        var dir = baseDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OpenClaw",
            "Companion");
        SettingsPath = Path.Combine(dir, "settings.json");
        _tokenStore = tokenStore ?? new ProtectedTokenStore(dir);
        var providerKeyDir = Path.Combine(dir, "provider-key");
        _providerKeyStore = providerKeyStore ?? new ProtectedTokenStore(providerKeyDir);
        var providerKeyMarkerDirectory = Path.GetDirectoryName(_providerKeyStore.FallbackPath) ?? providerKeyDir;
        _providerKeyMarkerPath = Path.Combine(providerKeyMarkerDirectory, "stored.marker");
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
        catch (JsonException ex)
        {
            TraceSettingsLoadFailure(ex);
            return new CompanionSettings();
        }
        catch (IOException ex)
        {
            TraceSettingsLoadFailure(ex);
            return new CompanionSettings();
        }
        catch (UnauthorizedAccessException ex)
        {
            TraceSettingsLoadFailure(ex);
            return new CompanionSettings();
        }
        catch (InvalidOperationException ex)
        {
            TraceSettingsLoadFailure(ex);
            return new CompanionSettings();
        }
        catch (NotSupportedException ex)
        {
            TraceSettingsLoadFailure(ex);
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

    public string? LoadProviderApiKey(bool allowPlaintextFallback)
    {
        if (!File.Exists(_providerKeyMarkerPath))
            return null;

        var providerApiKey = _providerKeyStore.LoadToken(allowPlaintextFallback);
        LastWarning = _providerKeyStore.LastWarning;
        return providerApiKey;
    }

    public bool SaveProviderApiKey(string providerApiKey, bool allowPlaintextFallback)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_providerKeyMarkerPath)!);
        _ = _providerKeyStore.SaveToken(providerApiKey, allowPlaintextFallback, out var warning);
        LastWarning = warning;

        var loadedProviderApiKey = _providerKeyStore.LoadToken(allowPlaintextFallback);
        LastWarning ??= _providerKeyStore.LastWarning;
        if (!string.Equals(loadedProviderApiKey, providerApiKey, StringComparison.Ordinal))
        {
            TryDelete(_providerKeyMarkerPath);
            return false;
        }

        File.WriteAllText(_providerKeyMarkerPath, "stored");
        return true;
    }

    public void ClearProviderApiKey()
    {
        _providerKeyStore.ClearToken();
        TryDelete(_providerKeyMarkerPath);
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
        catch (JsonException ex)
        {
            Trace.TraceWarning("Settings store ignored legacy token JSON error: {0}", ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            Trace.TraceWarning("Settings store ignored legacy token invalid state: {0}", ex.Message);
        }

        return null;
    }

    private static void TraceSettingsLoadFailure(Exception ex)
    {
        Trace.TraceWarning(
            "Settings store ignored settings load {0}: {1}",
            ex.GetType().Name,
            ex.Message);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException ex)
        {
            Trace.TraceWarning("Settings store ignored delete IO error for '{0}': {1}", path, ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            Trace.TraceWarning("Settings store ignored delete access error for '{0}': {1}", path, ex.Message);
        }
    }
}
