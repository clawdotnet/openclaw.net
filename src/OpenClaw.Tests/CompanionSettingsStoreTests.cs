using OpenClaw.Companion.Models;
using OpenClaw.Companion.Services;
using Xunit;

namespace OpenClaw.Tests;

public sealed class CompanionSettingsStoreTests
{
    [Fact]
    public void Save_DoesNotPersistAuthTokenInSettingsJson_AndLoadsProtectedToken()
    {
        var baseDir = CreateTempDir();
        try
        {
            var store = new SettingsStore(baseDir, new ProtectedTokenStore(baseDir, new InMemorySecretStore()));
            store.Save(new CompanionSettings
            {
                ServerUrl = "ws://127.0.0.1:18789/ws",
                RememberToken = true,
                AuthToken = "top-secret"
            });

            var json = File.ReadAllText(store.SettingsPath);
            Assert.DoesNotContain("top-secret", json, StringComparison.Ordinal);
            Assert.DoesNotContain("AuthToken", json, StringComparison.Ordinal);

            var loaded = store.Load();
            Assert.True(loaded.RememberToken);
            Assert.Equal("top-secret", loaded.AuthToken);
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
        }
    }

    [Fact]
    public void Save_SecureStoreUnavailableWithoutOptIn_DoesNotWritePlaintextFallback()
    {
        var baseDir = CreateTempDir();
        try
        {
            var store = new SettingsStore(baseDir, new ProtectedTokenStore(baseDir, new UnavailableTestSecretStore()));
            store.Save(new CompanionSettings
            {
                ServerUrl = "ws://127.0.0.1:18789/ws",
                RememberToken = true,
                AllowPlaintextTokenFallback = false,
                AuthToken = "top-secret"
            });

            Assert.False(File.Exists(Path.Combine(baseDir, "token.txt")));
            Assert.Contains("not saved", store.LastWarning, StringComparison.OrdinalIgnoreCase);

            var loaded = store.Load();
            Assert.True(loaded.RememberToken);
            Assert.Null(loaded.AuthToken);
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
        }
    }

    [Fact]
    public void Save_SecureStoreUnavailableWithOptIn_WritesAndLoadsPlaintextFallback()
    {
        var baseDir = CreateTempDir();
        try
        {
            var store = new SettingsStore(baseDir, new ProtectedTokenStore(baseDir, new UnavailableTestSecretStore()));
            store.Save(new CompanionSettings
            {
                ServerUrl = "ws://127.0.0.1:18789/ws",
                RememberToken = true,
                AllowPlaintextTokenFallback = true,
                AuthToken = "fallback-secret"
            });

            var json = File.ReadAllText(store.SettingsPath);
            Assert.DoesNotContain("fallback-secret", json, StringComparison.Ordinal);
            Assert.Contains("\"allowPlaintextTokenFallback\": true", json, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("fallback-secret", File.ReadAllText(Path.Combine(baseDir, "token.txt")));

            var loaded = store.Load();
            Assert.True(loaded.AllowPlaintextTokenFallback);
            Assert.Equal("fallback-secret", loaded.AuthToken);
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
        }
    }

    [Fact]
    public void ProviderApiKey_UsesProtectedStoreAndDoesNotPersistInSettingsJson()
    {
        var baseDir = CreateTempDir();
        try
        {
            var store = new SettingsStore(
                baseDir,
                new ProtectedTokenStore(baseDir, new InMemorySecretStore()),
                new ProtectedTokenStore(Path.Combine(baseDir, "provider-secret-store"), new InMemorySecretStore()));
            store.Save(new CompanionSettings
            {
                ServerUrl = "ws://127.0.0.1:18789/ws"
            });

            var saved = store.SaveProviderApiKey("provider-secret", allowPlaintextFallback: false);
            var json = File.ReadAllText(store.SettingsPath);

            Assert.True(saved);
            Assert.DoesNotContain("provider-secret", json, StringComparison.Ordinal);
            Assert.Equal("provider-secret", store.LoadProviderApiKey(allowPlaintextFallback: false));

            store.ClearProviderApiKey();

            Assert.Null(store.LoadProviderApiKey(allowPlaintextFallback: false));
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
        }
    }

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "openclaw-companion-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class InMemorySecretStore : ICompanionSecretStore
    {
        private string? _secret;

        public string StorageDescription => "memory";

        public bool IsAvailable => true;

        public string? LoadSecret(out string? warning)
        {
            warning = null;
            return _secret;
        }

        public bool SaveSecret(string secret, out string? warning)
        {
            _secret = secret;
            warning = null;
            return true;
        }

        public void ClearSecret()
        {
            _secret = null;
        }
    }

    private sealed class UnavailableTestSecretStore : ICompanionSecretStore
    {
        public string StorageDescription => "unavailable";

        public bool IsAvailable => false;

        public string? LoadSecret(out string? warning)
        {
            warning = "Secure token storage is unavailable on this system.";
            return null;
        }

        public bool SaveSecret(string secret, out string? warning)
        {
            warning = "Secure token storage is unavailable on this system.";
            return false;
        }

        public void ClearSecret()
        {
        }
    }
}
