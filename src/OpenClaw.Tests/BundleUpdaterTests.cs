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
    [Theory]
    [InlineData("../outside")]
    [InlineData("/absolute")]
    [InlineData("C:\\escape")]
    public void UnsafeArchiveCannotEscapeStaging(string name)
    {
        Directory.CreateDirectory(_root);
        var zip = Path.Combine(_root, "test.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create)) { using var writer = new StreamWriter(archive.CreateEntry(name).Open()); writer.Write("test"); }
        Assert.Throws<InvalidDataException>(() => BundleUpdater.ExtractBundle(zip, Path.Combine(_root, "staging")));
    }
    [Fact]
    public async Task InstallVerifiesAllComponentsAndRollbackRestoresPreviousPointer()
    {
        if (OperatingSystem.IsWindows()) return; // fixture executables use /bin/sh
        var bundle = Archive(); var handler = new Handler(); using var http = new HttpClient(handler);
        var updater = new BundleUpdater(http, _root);
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
