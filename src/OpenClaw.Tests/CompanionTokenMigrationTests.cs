using System.Text;
using System.Text.Json;
using OpenClaw.Companion.Models;
using OpenClaw.Companion.Services;
using Xunit;

namespace OpenClaw.Tests;

public sealed class CompanionTokenMigrationTests : IDisposable
{
    private readonly string _directory = Path.Join(Path.GetTempPath(), "openclaw-token-migration-" + Guid.NewGuid().ToString("N"));
    private readonly FakeSecretStore _secure = new();
    private SettingsStore Store() => new(_directory, new ProtectedTokenStore(_directory, _secure),
        new ProtectedTokenStore(Path.Join(_directory, "provider"), new FakeSecretStore()));
    private string SettingsPath => Path.Join(_directory, "settings.json");
    private string FallbackPath => Path.Join(_directory, "token.txt");

    private void Legacy(bool allowFallback = false, bool remember = true, string property = "authToken")
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["rememberToken"] = remember, ["allowPlaintextTokenFallback"] = allowFallback,
            [property] = "legacy-secret", ["serverUrl"] = "ws://example.invalid/ws",
            ["futureSetting"] = new { keep = 42 }
        }));
    }

    [Theory]
    [InlineData("authToken")]
    [InlineData("AuthToken")]
    [InlineData("AUTHTOKEN")]
    public void Load_MigratesLegacyTokenAndPreservesUnrelatedSettings(string property)
    {
        Legacy(property: property);
        var store = Store(); var loaded = store.Load();
        Assert.Equal("legacy-secret", loaded.AuthToken);
        Assert.Equal("legacy-secret", _secure.Secret);
        Assert.DoesNotContain("legacy-secret", File.ReadAllText(SettingsPath));
        using var json = JsonDocument.Parse(File.ReadAllText(SettingsPath));
        Assert.Equal(42, json.RootElement.GetProperty("futureSetting").GetProperty("keep").GetInt32());
        Assert.Equal("ws://example.invalid/ws", loaded.ServerUrl);
        Assert.Null(store.LastWarning);
        var saves = _secure.SaveCount;
        Assert.Equal("legacy-secret", store.Load().AuthToken);
        Assert.Equal(saves, _secure.SaveCount);
    }

    [Fact]
    public void FailedTokenReplacementCannotPairOldSecretWithNewServer()
    {
        var store = Store();
        store.Save(new CompanionSettings { RememberToken = true, AuthToken = "old", ServerUrl = "ws://old.invalid/ws" });
        _secure.FailSave = true;
        store.Save(new CompanionSettings { RememberToken = true, AuthToken = "new", ServerUrl = "ws://new.invalid/ws" });
        var loaded = store.Load();
        Assert.Equal("old", loaded.AuthToken);
        Assert.Equal("ws://old.invalid/ws", loaded.ServerUrl);
    }

    [Fact]
    public void OrdinarySavePreservesConflictingRecoveryCopiesWithoutSecureRewrite()
    {
        Legacy(); _secure.Secret = "protected-secret";
        File.WriteAllText(FallbackPath, "different-fallback");
        var store = Store(); var settings = store.Load();
        settings.DebugMode = true; _secure.FailSave = true;
        store.Save(settings);
        Assert.Equal(0, _secure.SaveCount);
        Assert.Contains("legacy-secret", File.ReadAllText(SettingsPath));
        Assert.Equal("different-fallback", File.ReadAllText(FallbackPath));
        Assert.False(File.Exists(Path.Join(_directory, "token-update.pending")));
        Assert.Equal("protected-secret", store.Load().AuthToken);
    }

    [Fact]
    public void SuccessfulSavePreservesDifferentFallback()
    {
        var store = new ProtectedTokenStore(_directory, _secure);
        File.WriteAllText(FallbackPath, "recovery-copy");
        Assert.True(store.SaveToken("new-secret", false, out var warning));
        Assert.Equal("recovery-copy", File.ReadAllText(FallbackPath));
        Assert.Contains("preserved", warning);
    }

    [Fact]
    public void MalformedSettingsSaveDoesNotChangeCredentials()
    {
        Legacy(); File.WriteAllText(SettingsPath, "{");
        var store = Store(); store.Save(new CompanionSettings { RememberToken = true, AuthToken = "new" });
        Assert.Equal("{", File.ReadAllText(SettingsPath));
        Assert.Equal(0, _secure.SaveCount);
        Assert.NotNull(store.LastWarning);
    }

    [Fact]
    public void NullLegacyFieldDoesNotPreventMigrationOrWarnForever()
    {
        Legacy(); File.WriteAllText(SettingsPath, """{"rememberToken":true,"authToken":null}""");
        var store = Store(); store.Load();
        Assert.DoesNotContain("authToken", File.ReadAllText(SettingsPath));
        Assert.Null(store.LastWarning);
    }

    [Fact]
    public void FailedUnchangedWriteDoesNotStrandMigrationMarker()
    {
        Legacy(); _secure.FailSave = true;
        var store = Store(); store.Save(new CompanionSettings { RememberToken = true, AuthToken = "new" });
        Assert.False(File.Exists(Path.Join(_directory, "token-update.pending")));
        _secure.FailSave = false;
        Assert.Equal("legacy-secret", store.Load().AuthToken);
    }

    [Fact]
    public void Load_MigratesPascalCaseLegacySettings()
    {
        Legacy(); File.WriteAllText(SettingsPath, """{"RememberToken":true,"AuthToken":"legacy-secret","ServerUrl":"ws://example.invalid/ws"}""");
        var loaded = Store().Load();
        Assert.Equal("legacy-secret", loaded.AuthToken);
        Assert.Equal("ws://example.invalid/ws", loaded.ServerUrl);
        Assert.DoesNotContain("legacy-secret", File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void Load_UnexpectedStorageErrorRetainsSettingsAndLegacyToken()
    {
        Legacy(); _secure.ThrowLoad = true;
        var store = Store(); var loaded = store.Load();
        Assert.True(loaded.RememberToken);
        Assert.Equal("ws://example.invalid/ws", loaded.ServerUrl);
        Assert.Null(loaded.AuthToken);
        Assert.Contains("legacy-secret", File.ReadAllText(SettingsPath));
        Assert.Contains("preserved", store.LastWarning);
    }

    [Fact]
    public void Load_DoesNotOverwriteSettingsChangedDuringMigration()
    {
        Legacy();
        _secure.AfterSave = () => File.WriteAllText(SettingsPath, """{"rememberToken":true,"authToken":"legacy-secret","futureSetting":"changed"}""");
        var store = Store();
        Assert.Equal("legacy-secret", store.Load().AuthToken);
        Assert.Contains("changed", File.ReadAllText(SettingsPath));
        Assert.Contains("deferred", store.LastWarning);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Load_UnavailableSecureStorePreservesLegacyAndHonorsPlaintextOptIn(bool allow)
    {
        Legacy(allow); _secure.IsAvailable = false;
        var original = File.ReadAllText(SettingsPath); var store = Store();
        var loaded = store.Load();
        Assert.Equal(allow ? "legacy-secret" : null, loaded.AuthToken);
        Assert.Equal(original, File.ReadAllText(SettingsPath));
        Assert.NotNull(store.LastWarning);
        Assert.DoesNotContain("legacy-secret", store.LastWarning);
    }

    [Fact]
    public void Load_DoesNotLoadOrMigrateWhenRememberTokenIsFalse()
    {
        Legacy(remember: false); _secure.Secret = "protected-secret";
        var original = File.ReadAllText(SettingsPath);
        Assert.Null(Store().Load().AuthToken);
        Assert.Equal(0, _secure.LoadCount);
        Assert.Equal(0, _secure.SaveCount);
        Assert.Equal(original, File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void Load_FailedReadbackPreservesLegacyForRetry()
    {
        Legacy(); _secure.FailReadAfterSave = true; var original = File.ReadAllText(SettingsPath); var store = Store();
        Assert.Null(store.Load().AuthToken);
        Assert.Equal(original, File.ReadAllText(SettingsPath));
        Assert.Contains("verified", store.LastWarning);
        _secure.FailReadAfterSave = false;
        Assert.Equal("legacy-secret", store.Load().AuthToken);
        Assert.DoesNotContain("legacy-secret", File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void Load_LockedStoreIsNotOverwrittenDuringMigration()
    {
        Legacy(); _secure.Secret = "protected-secret"; _secure.Locked = true;
        var store = Store(); Assert.Null(store.Load().AuthToken);
        Assert.Equal(0, _secure.SaveCount);
        Assert.Equal("protected-secret", _secure.Secret);
        Assert.Contains("legacy-secret", File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void Load_ConflictingProtectedAndLegacyTokensArePreserved()
    {
        Legacy(); _secure.Secret = "protected-secret";
        var store = Store(); Assert.Equal("protected-secret", store.Load().AuthToken);
        Assert.Contains("legacy-secret", File.ReadAllText(SettingsPath));
        Assert.Equal(0, _secure.SaveCount);
        Assert.Contains("different", store.LastWarning);
    }

    [Fact]
    public void Load_ConflictingLegacyFieldsAreNotSilentlyDeleted()
    {
        Legacy(); File.WriteAllText(SettingsPath, """{"rememberToken":true,"authToken":"one","AuthToken":"two"}""");
        var store = Store(); Assert.Null(store.Load().AuthToken);
        Assert.Contains("conflicting", store.LastWarning);
        Assert.Equal(0, _secure.SaveCount);
        Assert.Contains("two", File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void Load_MigratesFallbackEvenWhenPlaintextUseIsDisabled()
    {
        var protectedStore = new ProtectedTokenStore(_directory, _secure);
        File.WriteAllText(FallbackPath, "fallback-secret");
        Assert.Equal("fallback-secret", protectedStore.LoadToken(false));
        Assert.True(protectedStore.LastLoadWasProtected);
        Assert.Equal("fallback-secret", _secure.Secret);
        Assert.False(File.Exists(FallbackPath));
    }

    [Fact]
    public void Load_FailedFallbackMigrationKeepsOriginalFile()
    {
        var store = new ProtectedTokenStore(_directory, _secure); _secure.FailReadAfterSave = true;
        File.WriteAllText(FallbackPath, "fallback-secret");
        Assert.Null(store.LoadToken(false));
        Assert.Equal("fallback-secret", File.ReadAllText(FallbackPath));
        Assert.False(store.LastLoadWasProtected);
    }

    [Fact]
    public void Load_DifferentFallbackIsNotDeletedWhenProtectedTokenExists()
    {
        var store = new ProtectedTokenStore(_directory, _secure); _secure.Secret = "protected-secret";
        File.WriteAllText(FallbackPath, "different-secret");
        Assert.Equal("protected-secret", store.LoadToken(false));
        Assert.Equal("different-secret", File.ReadAllText(FallbackPath));
        Assert.Contains("different", store.LastWarning);
    }

    [Fact]
    public void Save_FailureDoesNotDeleteExistingFallback()
    {
        var store = new ProtectedTokenStore(_directory, _secure); _secure.IsAvailable = false;
        File.WriteAllText(FallbackPath, "old-secret");
        Assert.False(store.SaveToken("new-secret", false, out _));
        Assert.Equal("old-secret", File.ReadAllText(FallbackPath));
    }

    [Fact]
    public void Save_FailureDoesNotStripOnlyLegacyCopy()
    {
        Legacy(); _secure.FailSave = true; var original = File.ReadAllText(SettingsPath); var store = Store();
        store.Save(new CompanionSettings { RememberToken = true, AuthToken = "new-secret", DebugMode = true });
        Assert.Equal(original, File.ReadAllText(SettingsPath));
        Assert.Contains("not changed", store.LastWarning);
    }

    [Fact]
    public void Save_AfterBlockedLoadDoesNotForgetCredentials()
    {
        Legacy(); _secure.IsAvailable = false; var original = File.ReadAllText(SettingsPath); var store = Store();
        var loaded = store.Load(); loaded.DebugMode = true; store.Save(loaded);
        Assert.Equal(original, File.ReadAllText(SettingsPath));
        Assert.Contains("migration is still pending", store.LastWarning);
    }

    [Fact]
    public void Save_EmptyInMemoryTokenDoesNotClearProtectedStorage()
    {
        var store = Store(); _secure.Secret = "protected-secret";
        store.Save(new CompanionSettings { RememberToken = true });
        Assert.Equal("protected-secret", _secure.Secret);
        Assert.Equal(0, _secure.ClearCount);
    }

    [Fact]
    public void Save_ExplicitForgetClearsProtectedFallbackAndLegacyCopies()
    {
        Legacy(); var store = Store(); _secure.Secret = "protected-secret";
        File.WriteAllText(FallbackPath, "fallback-secret");
        store.Save(new CompanionSettings { RememberToken = false });
        Assert.Null(_secure.Secret);
        Assert.False(File.Exists(FallbackPath));
        Assert.DoesNotContain("legacy-secret", File.ReadAllText(SettingsPath));
        Assert.Null(store.LastWarning);
    }

    [Fact]
    public void ProviderKey_FailedReplacementKeepsExistingMarkerAndKey()
    {
        var provider = new FakeSecretStore();
        var store = new SettingsStore(_directory, new ProtectedTokenStore(_directory, _secure),
            new ProtectedTokenStore(Path.Join(_directory, "provider"), provider));
        Assert.True(store.SaveProviderApiKey("old-key", false));
        provider.FailSave = true;
        Assert.False(store.SaveProviderApiKey("new-key", false));
        Assert.Equal("old-key", store.LoadProviderApiKey(false));
    }

    [Fact]
    public void AtomicFilesHavePrivatePermissionsAndLeaveNoTemporaryCopies()
    {
        var path = Path.Join(_directory, "private.txt");
        CompanionFilePersistence.WriteAtomically(path, Encoding.UTF8.GetBytes("secret"));
        Assert.Equal("secret", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp-*"));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        CompanionFilePersistence.WriteAtomically(path, Encoding.UTF8.GetBytes("replacement"));
        Assert.Equal("replacement", File.ReadAllText(path));
    }

    [Fact]
    public void AtomicWriteFailurePreservesDestinationAndCleansTemporaryFile()
    {
        var destination = Path.Join(_directory, "existing-directory");
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Join(destination, "keep"), "original");
        var error = Record.Exception(() => CompanionFilePersistence.WriteAtomically(destination, Encoding.UTF8.GetBytes("secret")));
        Assert.True(error is IOException or UnauthorizedAccessException);
        Assert.Equal("original", File.ReadAllText(Path.Join(destination, "keep")));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp-*"));
    }

    [Fact]
    public void Save_ThrowingReadbackPreservesFallback()
    {
        var store = new ProtectedTokenStore(_directory, _secure);
        File.WriteAllText(FallbackPath, "original"); _secure.ThrowLoad = true;
        Assert.False(store.SaveToken("new-secret", false, out var warning));
        Assert.Equal("original", File.ReadAllText(FallbackPath));
        Assert.Contains("verified", warning);
    }

    [Fact]
    public void Clear_ReportsProtectedStoreVerificationFailure()
    {
        var store = new ProtectedTokenStore(_directory, _secure); _secure.ThrowLoad = true;
        store.ClearToken();
        Assert.Contains("could not be cleared or verified", store.LastWarning);
    }

    [Fact]
    public void Save_SettingsFailureBlocksAutomaticUseUntilSuccessfulRetry()
    {
        var store = Store();
        Directory.CreateDirectory(SettingsPath);
        var update = new CompanionSettings { RememberToken = true, AuthToken = "new-server-secret", ServerUrl = "ws://new.invalid/ws" };
        var error = Record.Exception(() => store.Save(update));
        Assert.True(error is IOException or UnauthorizedAccessException);
        Assert.Equal("new-server-secret", _secure.Secret);
        Assert.True(File.Exists(Path.Join(_directory, "token-update.pending")));
        Directory.Delete(SettingsPath);
        File.WriteAllText(SettingsPath, """{"rememberToken":true,"serverUrl":"ws://old.invalid/ws"}""");
        var loaded = store.Load();
        Assert.Null(loaded.AuthToken);
        Assert.Contains("incomplete", store.LastWarning);
        store.Save(update);
        Assert.False(File.Exists(Path.Join(_directory, "token-update.pending")));
        loaded = store.Load();
        Assert.Equal("new-server-secret", loaded.AuthToken);
        Assert.Equal("ws://new.invalid/ws", loaded.ServerUrl);
    }

    [Fact]
    public void Save_EmptyTokenCannotPairOldCredentialWithNewServer()
    {
        var store = Store();
        store.Save(new CompanionSettings { RememberToken = true, AuthToken = "old-secret", ServerUrl = "ws://old.invalid/ws" });
        store.Save(new CompanionSettings { RememberToken = true, ServerUrl = "ws://new.invalid/ws" });
        Assert.Equal("ws://old.invalid/ws", store.Load().ServerUrl);
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

    private sealed class FakeSecretStore : ICompanionSecretStore
    {
        public string? Secret { get; set; }
        public bool IsAvailable { get; set; } = true;
        public bool FailSave { get; set; }
        public bool FailReadAfterSave { get; set; }
        public bool Locked { get; set; }
        public bool ThrowLoad { get; set; }
        public Action? AfterSave { get; set; }
        public int SaveCount { get; private set; }
        public int LoadCount { get; private set; }
        public int ClearCount { get; private set; }
        public string StorageDescription => "test-secret-store";
        public string? LoadSecret(out string? warning)
        {
            LoadCount++;
            if (ThrowLoad) throw new IOException("Test store unavailable");
            warning = Locked || (FailReadAfterSave && SaveCount > 0) ? "Secure store is locked." : null;
            return warning is null ? Secret : null;
        }
        public bool SaveSecret(string secret, out string? warning)
        {
            SaveCount++;
            warning = FailSave ? "Secure save failed." : null;
            if (!FailSave) { Secret = secret; AfterSave?.Invoke(); }
            return !FailSave;
        }
        public void ClearSecret() { ClearCount++; Secret = null; }
    }
}
