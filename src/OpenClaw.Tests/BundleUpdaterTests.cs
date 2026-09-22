using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenClaw.Core.Updates;
using Xunit;
namespace OpenClaw.Tests;
public sealed class BundleUpdaterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "updater-" + Guid.NewGuid().ToString("N"));
    private readonly RSA _key = RSA.Create(3072);
    private byte[] Feed(string version, byte[] asset, DateTimeOffset? expires = null) => JsonSerializer.SerializeToUtf8Bytes(
        new UpdateFeed(1, expires ?? DateTimeOffset.UtcNow.AddDays(1), [new(version, "stable", [new(RuntimeInformation.RuntimeIdentifier, "https://publisher.test/bundle.zip", Convert.ToHexString(SHA256.HashData(asset)), asset.Length)])]), UpdateJsonContext.Default.UpdateFeed);
    private byte[] Sign(byte[] bytes) => _key.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    [Fact]
    public void ModifiedManifestWrongKeyAndExpiredFeedAreRejected()
    {
        var data = Feed("1.0.0", [1]); var signature = Sign(data);
        using var other = RSA.Create(3072);
        Assert.Throws<InvalidDataException>(() => BundleUpdater.VerifyFeed(data, signature, other.ExportSubjectPublicKeyInfoPem(), DateTimeOffset.UtcNow));
        data[0] ^= 1;
        Assert.Throws<InvalidDataException>(() => BundleUpdater.VerifyFeed(data, signature, _key.ExportSubjectPublicKeyInfoPem(), DateTimeOffset.UtcNow));
        data = Feed("1.0.0", [1], DateTimeOffset.UtcNow.AddMinutes(-1));
        Assert.Throws<InvalidDataException>(() => BundleUpdater.VerifyFeed(data, Sign(data), _key.ExportSubjectPublicKeyInfoPem(), DateTimeOffset.UtcNow));
    }
    [Fact]
    public void TrustPersistenceStripsPrivateKeyMaterial()
    {
        using var http = new HttpClient(new Handler());
        var updater = new BundleUpdater(http, _root);
        updater.ConfigureTrust(new("https://publisher.test/feed", _key.ExportRSAPrivateKeyPem()));
        var persisted = File.ReadAllText(updater.TrustPath);
        Assert.Contains("BEGIN PUBLIC KEY", persisted);
        Assert.DoesNotContain("PRIVATE KEY", persisted);
    }
    [Theory]
    [InlineData("../outside")]
    [InlineData("/absolute")]
    [InlineData("C:\\escape")]
    [InlineData("cli/../../outside")]
    public void UnsafeArchiveCannotEscapeStaging(string name)
    {
        Directory.CreateDirectory(_root);
        var zip = Path.Combine(_root, "test.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create)) { using var writer = new StreamWriter(archive.CreateEntry(name).Open()); writer.Write("test"); }
        Assert.Throws<InvalidDataException>(() => BundleUpdater.ExtractBundle(zip, Path.Combine(_root, "staging")));
    }
    [Fact]
    public void SymlinkArchiveEntryIsRejected()
    {
        Directory.CreateDirectory(_root);
        var zip = Path.Combine(_root, "symlink.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("cli/link");
            entry.ExternalAttributes = 0xA000 << 16;
        }
        Assert.Throws<InvalidDataException>(() => BundleUpdater.ExtractBundle(zip, Path.Combine(_root, "staging")));
    }
    [Fact]
    public async Task InstallVerifiesAllComponentsAndRollbackRestoresPreviousPointer()
    {
        var bundle = Archive(); var handler = new Handler(); using var http = new HttpClient(handler);
        var updater = new BundleUpdater(http, _root, smokeCheck: static (_, _) => Task.CompletedTask);
        updater.ConfigureTrust(new("https://publisher.test/feed", _key.ExportSubjectPublicKeyInfoPem()));
        void Publish(string version) { var feed = Feed(version, bundle); handler.Data = new() { ["/feed"] = feed, ["/feed.sig"] = Sign(feed), ["/bundle.zip"] = bundle }; }
        Publish("1.0.0"); var first = await updater.InstallAsync("stable", null, TestContext.Current.CancellationToken);
        Publish("1.0.1"); var second = await updater.InstallAsync("stable", "1.0.1", TestContext.Current.CancellationToken);
        Assert.NotEqual(first, second); Assert.StartsWith(second, updater.GetActiveExecutable("gateway"));
        Assert.Equal(first, updater.Rollback()); Assert.StartsWith(first, updater.GetActiveExecutable("companion"));
        Publish("1.0.2"); handler.Data["/bundle.zip"] = new byte[bundle.Length];
        await Assert.ThrowsAsync<InvalidDataException>(() => updater.InstallAsync("stable", null, TestContext.Current.CancellationToken));
        Assert.StartsWith(first, updater.GetActiveExecutable("cli"));
    }
    [Theory]
    [InlineData("1.2.0-beta.1", "1.2.0-beta.2", -1)]
    [InlineData("1.2.0", "1.2.0-rc.9", 1)]
    [InlineData("2.0.0", "1.99.99", 1)]
    public void SemanticVersionComparisonPreservesPrereleasePrecedence(string left, string right, int expected)
        => Assert.Equal(expected, Math.Sign(BundleUpdater.CompareSemanticVersions(left, right)));

    [Theory]
    [InlineData("1")]
    [InlineData("1.2.3.4")]
    [InlineData("1.02.3")]
    [InlineData("1.2.3-beta.01")]
    public void InvalidSemanticVersionsFailClosed(string value)
        => Assert.Throws<InvalidDataException>(() => BundleUpdater.CompareSemanticVersions(value, "1.0.0"));

    [Fact]
    public async Task FailedPostMoveActivationIsRemovedAndRetrySucceeds()
    {
        var bundle = Archive(); var handler = new Handler(); using var http = new HttpClient(handler);
        var updater = new BundleUpdater(http, _root, smokeCheck: static (_, _) => Task.CompletedTask);
        updater.ConfigureTrust(new("https://publisher.test/feed", _key.ExportSubjectPublicKeyInfoPem()));
        var feed = Feed("1.0.0", bundle);
        handler.Data = new() { ["/feed"] = feed, ["/feed.sig"] = Sign(feed), ["/bundle.zip"] = bundle };
        Directory.CreateDirectory(Path.Combine(_root, "launch.ps1"));
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => updater.InstallAsync("stable", null, TestContext.Current.CancellationToken));
        Assert.True(failure is IOException or UnauthorizedAccessException);
        Directory.Delete(Path.Combine(_root, "launch.ps1"));
        var installed = await updater.InstallAsync("stable", null, TestContext.Current.CancellationToken);
        Assert.True(Directory.Exists(installed));
    }

    [Fact]
    public async Task DowngradesRequireExplicitAuthorizationIncludingPrereleases()
    {
        var bundle = Archive(); var handler = new Handler(); using var http = new HttpClient(handler);
        var updater = new BundleUpdater(http, _root, smokeCheck: static (_, _) => Task.CompletedTask);
        updater.ConfigureTrust(new("https://publisher.test/feed", _key.ExportSubjectPublicKeyInfoPem()));
        void Publish(string version)
        {
            var feed = Feed(version, bundle);
            handler.Data = new() { ["/feed"] = feed, ["/feed.sig"] = Sign(feed), ["/bundle.zip"] = bundle };
        }
        Publish("1.2.0-beta.2");
        await updater.InstallAsync("stable", null, TestContext.Current.CancellationToken);
        Publish("1.2.0-beta.1");
        await Assert.ThrowsAsync<InvalidOperationException>(() => updater.InstallAsync("stable", "1.2.0-beta.1", TestContext.Current.CancellationToken));
        var installed = await updater.InstallAsync("stable", "1.2.0-beta.1", TestContext.Current.CancellationToken, allowDowngrade: true);
        Assert.Contains("1.2.0-beta.1", installed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExplicitlyCheckedVersionIsTheVersionInstalled()
    {
        var bundle = Archive(); var handler = new Handler(); using var http = new HttpClient(handler);
        var updater = new BundleUpdater(http, _root, smokeCheck: static (_, _) => Task.CompletedTask);
        updater.ConfigureTrust(new("https://publisher.test/feed", _key.ExportSubjectPublicKeyInfoPem()));
        var asset = new UpdateAsset(RuntimeInformation.RuntimeIdentifier, "https://publisher.test/bundle.zip",
            Convert.ToHexString(SHA256.HashData(bundle)), bundle.Length);
        var feed = JsonSerializer.SerializeToUtf8Bytes(new UpdateFeed(1, DateTimeOffset.UtcNow.AddDays(1),
            [new("2.0.0", "stable", [asset]), new("1.5.0", "stable", [asset])]), UpdateJsonContext.Default.UpdateFeed);
        handler.Data = new() { ["/feed"] = feed, ["/feed.sig"] = Sign(feed), ["/bundle.zip"] = bundle };
        var installed = await updater.InstallAsync("stable", "1.5.0", TestContext.Current.CancellationToken);
        Assert.Contains("1.5.0", installed, StringComparison.Ordinal);
    }
    [Fact]
    public void ExplicitVersionAndChannelDoNotFallBack()
    {
        var data = Feed("1.0.0", [1]);
        var feed = BundleUpdater.VerifyFeed(data, Sign(data), _key.ExportSubjectPublicKeyInfoPem(), DateTimeOffset.UtcNow);
        Assert.Throws<InvalidDataException>(() => BundleUpdater.SelectRelease(feed, "beta", null));
        Assert.Throws<InvalidDataException>(() => BundleUpdater.SelectRelease(feed, "stable", "2.0.0"));
    }
    private static byte[] Archive()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
            foreach (var file in new[] { "cli/openclaw", "gateway/OpenClaw.Gateway", "companion/OpenClaw.Companion" })
            { using var writer = new StreamWriter(archive.CreateEntry(file).Open(), new UTF8Encoding(false)); writer.Write("#!/bin/sh\nexit 0\n"); }
        return stream.ToArray();
    }
    private sealed class Handler : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Data = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(Data[request.RequestUri!.AbsolutePath]) });
    }
    public void Dispose() { _key.Dispose(); if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
