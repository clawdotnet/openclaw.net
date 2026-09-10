using System.Diagnostics;
using System.Text.Json;
using System.Text;
using OpenClaw.Companion.Models;

namespace OpenClaw.Companion.Services;

public sealed class SettingsStore
{
    private readonly ProtectedTokenStore _tokenStore;
    private readonly ProtectedTokenStore _providerKeyStore;
    private readonly string _providerKeyMarkerPath;
    private readonly string _tokenUpdateMarkerPath;

    public string SettingsPath { get; }
    public string? LastWarning { get; private set; }

    public SettingsStore(
        string? baseDir = null,
        ProtectedTokenStore? tokenStore = null,
        ProtectedTokenStore? providerKeyStore = null)
    {
        var defaultDir = Path.Join(
            Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenClaw"),
            "Companion");
        var dir = baseDir ?? defaultDir;
        SettingsPath = Path.Join(dir, "settings.json");
        _tokenUpdateMarkerPath = Path.Join(dir, "token-update.pending");
        _tokenStore = tokenStore ?? new ProtectedTokenStore(dir);
        var providerKeyDir = Path.Join(dir, "provider-key");
        _providerKeyStore = providerKeyStore ?? new ProtectedTokenStore(providerKeyDir);
        var providerKeyMarkerDirectory = Path.GetDirectoryName(_providerKeyStore.FallbackPath) ?? providerKeyDir;
        _providerKeyMarkerPath = Path.Join(providerKeyMarkerDirectory, "stored.marker");
    }

    public CompanionSettings Load()
    {
        LastWarning = null;
        try
        {
            if (!File.Exists(SettingsPath))
                return new CompanionSettings();

            var json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize(json, CompanionJsonContext.Default.CompanionSettings) ?? new CompanionSettings();
            if (!settings.RememberToken)
                return settings;
            if (File.Exists(_tokenUpdateMarkerPath))
            {
                LastWarning = "A previous token update is incomplete. No saved token was loaded; re-enter the intended token and save, or turn off Remember token.";
                return settings;
            }
            try
            {
                settings.AuthToken = _tokenStore.LoadToken(settings.AllowPlaintextTokenFallback);
                LastWarning = _tokenStore.LastWarning;
                MigrateLegacyToken(settings, json);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                or System.Security.Cryptography.CryptographicException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                LastWarning = "Credentials could not be loaded or migrated. Existing storage was preserved; unlock secure storage and retry.";
            }
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
            SetupWorkspacePath = settings.SetupWorkspacePath,
            SetupLocalModelPath = settings.SetupLocalModelPath
        };

        if (settings.RememberToken && string.IsNullOrWhiteSpace(settings.AuthToken))
        {
            LastWarning = "No token was supplied; existing credentials and settings were retained. Re-enter the intended token or turn off Remember token. Token migration is still pending if secure storage is unavailable.";
            return;
        }
        string? originalJson;
        KeyValuePair<string, JsonElement>[] legacy;
        try
        {
            originalJson = File.Exists(SettingsPath) ? File.ReadAllText(SettingsPath) : null;
            using var original = originalJson is null ? null : JsonDocument.Parse(originalJson);
            legacy = original?.RootElement.EnumerateObject()
                .Where(p => p.Name.Equals("authToken", StringComparison.OrdinalIgnoreCase))
                .Select(p => new KeyValuePair<string, JsonElement>(p.Name, p.Value.Clone()))
                .ToArray() ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            LastWarning = "Existing settings could not be read; settings and credentials were retained for recovery.";
            return;
        }
        var persisted = false;

        if (settings.RememberToken && !string.IsNullOrWhiteSpace(settings.AuthToken))
        {
            var hadMarker = File.Exists(_tokenUpdateMarkerPath);
            var readableBefore = _tokenStore.TryReadProtected(out var before);
            persisted = readableBefore && string.Equals(before, settings.AuthToken, StringComparison.Ordinal);
            if (!persisted)
            {
                // Only a credential change needs an incomplete-update marker.
                CompanionFilePersistence.WriteAtomically(_tokenUpdateMarkerPath, "pending"u8);
                persisted = _tokenStore.SaveToken(settings.AuthToken, settings.AllowPlaintextTokenFallback, out var warning);
                LastWarning = warning;
                if (!persisted && !hadMarker && readableBefore && !settings.AllowPlaintextTokenFallback
                    && _tokenStore.TryReadProtected(out var after) && string.Equals(before, after, StringComparison.Ordinal))
                    ClearTokenUpdateMarker();
            }
            if (!persisted && settings.AllowPlaintextTokenFallback)
            {
                try { persisted = string.Equals(_tokenStore.LoadToken(true), settings.AuthToken, StringComparison.Ordinal); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                    or System.Security.Cryptography.CryptographicException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    persisted = false;
                }
                if (!persisted)
                    LastWarning = $"{LastWarning} The new token could not be reloaded; existing credential copies were retained.".Trim();
            }
            if (!persisted && legacy.Length > 0)
            {
                LastWarning = $"{LastWarning} Settings were not changed because the legacy token could not be stored safely.".Trim();
                return;
            }
        }

        var json = JsonSerializer.Serialize(toSave, CompanionJsonContext.Default.CompanionSettings);
        if (settings.RememberToken && legacy.Length > 0)
        {
            using var content = new MemoryStream();
            using var current = JsonDocument.Parse(json);
            using (var writer = new Utf8JsonWriter(content))
            {
                writer.WriteStartObject();
                foreach (var property in current.RootElement.EnumerateObject()) property.WriteTo(writer);
                foreach (var property in legacy)
                        if (property.Value.ValueKind != JsonValueKind.Null
                            && !(persisted && property.Value.ValueKind == JsonValueKind.String
                                && string.Equals(property.Value.GetString(), settings.AuthToken, StringComparison.Ordinal)))
                        {
                            writer.WritePropertyName(property.Key);
                            property.Value.WriteTo(writer);
                            LastWarning = $"{LastWarning} A legacy token field was preserved for recovery.".Trim();
                        }
                writer.WriteEndObject();
            }
            json = Encoding.UTF8.GetString(content.ToArray());
        }
        CompanionFilePersistence.WriteAtomically(SettingsPath, Encoding.UTF8.GetBytes(json));
        if (!settings.RememberToken)
        {
            _tokenStore.ClearToken();
            LastWarning = _tokenStore.LastWarning;
            ClearTokenUpdateMarker();
        }
        else if (persisted)
            ClearTokenUpdateMarker();
    }

    private void ClearTokenUpdateMarker()
    {
        try { File.Delete(_tokenUpdateMarkerPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastWarning = $"{LastWarning} The incomplete-update marker could not be removed. Saved-token loading remains disabled until a successful save.".Trim();
        }
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
            // A failed replacement must not hide a previously stored provider key.
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

    private void MigrateLegacyToken(CompanionSettings settings, string originalJson)
    {
        using var document = JsonDocument.Parse(originalJson);
        var properties = document.RootElement.EnumerateObject()
            .Where(property => property.Name.Equals("authToken", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (properties.Length == 0) return;
        if (properties.All(p => p.Value.ValueKind == JsonValueKind.Null))
        {
            using var content = new MemoryStream();
            using (var writer = new Utf8JsonWriter(content))
            {
                writer.WriteStartObject();
                foreach (var property in document.RootElement.EnumerateObject()
                    .Where(p => !p.Name.Equals("authToken", StringComparison.OrdinalIgnoreCase))) property.WriteTo(writer);
                writer.WriteEndObject();
            }
            if (File.ReadAllText(SettingsPath) == originalJson)
                CompanionFilePersistence.WriteAtomically(SettingsPath, content.ToArray());
            return;
        }
        var tokens = properties.Where(p => p.Value.ValueKind == JsonValueKind.String)
            .Select(p => p.Value.GetString()).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).ToArray();
        if (tokens.Length != 1 || properties.Any(p => p.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)))
        {
            LastWarning = "Legacy token fields are empty, conflicting, or invalid; they were preserved for manual review.";
            return;
        }
        var legacy = tokens[0]!;
        var protectedCopy = _tokenStore.LastLoadWasProtected && string.Equals(settings.AuthToken, legacy, StringComparison.Ordinal);
        if (!protectedCopy)
        {
            if (!string.IsNullOrWhiteSpace(settings.AuthToken) && !string.Equals(settings.AuthToken, legacy, StringComparison.Ordinal))
            {
                LastWarning = "A different stored token is in use; the legacy token was preserved for recovery.";
                return;
            }
            if (!_tokenStore.TryMigrateToken(legacy, out var warning))
            {
                LastWarning = $"{warning} Legacy token remains in settings."
                    + (settings.AllowPlaintextTokenFallback ? " Plaintext fallback is enabled." : " It was not loaded because plaintext fallback is disabled.");
                if (settings.AllowPlaintextTokenFallback) settings.AuthToken = legacy;
                return;
            }
            LastWarning = warning;
            settings.AuthToken = legacy;
        }
        try
        {
            if (!string.Equals(File.ReadAllText(SettingsPath), originalJson, StringComparison.Ordinal))
            {
                LastWarning = "Token is protected, but settings changed during migration; plaintext cleanup was deferred.";
                return;
            }
            using var content = new MemoryStream();
            using (var writer = new Utf8JsonWriter(content, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                foreach (var property in document.RootElement.EnumerateObject())
                    if (!property.Name.Equals("authToken", StringComparison.OrdinalIgnoreCase)) property.WriteTo(writer);
                writer.WriteEndObject();
            }
            CompanionFilePersistence.WriteAtomically(SettingsPath, content.ToArray());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastWarning = "Token is protected, but the legacy plaintext field could not be removed. Migration will retry on next load.";
        }
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
