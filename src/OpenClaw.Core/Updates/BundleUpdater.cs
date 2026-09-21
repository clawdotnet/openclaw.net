using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenClaw.Core.Updates;

public sealed record UpdateAsset(string Rid, string Url, string Sha256, long Size);
public sealed record UpdateRelease(string Version, string Channel, UpdateAsset[] Assets);
public sealed record UpdateFeed(int SchemaVersion, DateTimeOffset ExpiresAtUtc, UpdateRelease[] Releases);
public sealed record UpdateTrust(string ManifestUrl, string PublicKeyPem);
public sealed record BundleActivation(string Current, string? Previous);
[JsonSerializable(typeof(UpdateFeed))]
[JsonSerializable(typeof(UpdateTrust))]
[JsonSerializable(typeof(BundleActivation))]
public partial class UpdateJsonContext : JsonSerializerContext { }

/// <summary>Verified, side-by-side desktop bundles. Activation never overwrites a running binary.</summary>
public sealed class BundleUpdater(HttpClient http, string root, TimeProvider? timeProvider = null)
{
    private readonly string _root = Path.GetFullPath(root);
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".openclaw", "updates");
    public string TrustPath => Path.Combine(_root, "trust.json");
    private string ActivationPath => Path.Combine(_root, "active.json");
    public BundleActivation? ReadActivation() => File.Exists(ActivationPath)
        ? JsonSerializer.Deserialize(File.ReadAllText(ActivationPath), UpdateJsonContext.Default.BundleActivation) : null;

    public void ConfigureTrust(UpdateTrust trust)
    {
        RequireHttps(trust.ManifestUrl);
        using var rsa = RSA.Create();
        rsa.ImportFromPem(trust.PublicKeyPem);
        if (rsa.KeySize < 3072) throw new InvalidDataException("Update signing keys must be RSA 3072 bits or larger.");
        Directory.CreateDirectory(_root);
        WriteAtomic(TrustPath, JsonSerializer.Serialize(trust, UpdateJsonContext.Default.UpdateTrust));
    }

    public async Task<UpdateRelease> CheckAsync(string channel, string? version, CancellationToken ct)
    {
        if (channel is not ("stable" or "beta")) throw new ArgumentException("Channel must be stable or beta.");
        if (!File.Exists(TrustPath)) throw new InvalidOperationException("Configure a manifest URL and independently verified publisher public key first.");
        var trust = JsonSerializer.Deserialize(File.ReadAllText(TrustPath), UpdateJsonContext.Default.UpdateTrust)
            ?? throw new InvalidDataException("Invalid update trust configuration.");
        RequireHttps(trust.ManifestUrl);
        var manifest = await DownloadBytesAsync(trust.ManifestUrl, 1024 * 1024, ct);
        var signature = await DownloadBytesAsync(trust.ManifestUrl + ".sig", 16 * 1024, ct);
        return SelectRelease(VerifyFeed(manifest, signature, trust.PublicKeyPem, _clock.GetUtcNow()), channel, version);
    }

    public static UpdateFeed VerifyFeed(byte[] manifest, byte[] signature, string pem, DateTimeOffset now)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(pem);
        if (rsa.KeySize < 3072 || !rsa.VerifyData(manifest, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            throw new InvalidDataException("Update manifest signature verification failed.");
        var feed = JsonSerializer.Deserialize(manifest, UpdateJsonContext.Default.UpdateFeed);
        if (feed is null || feed.SchemaVersion != 1 || feed.ExpiresAtUtc <= now || feed.Releases is null)
            throw new InvalidDataException("Update manifest is invalid or expired.");
        return feed;
    }

    public static UpdateRelease SelectRelease(UpdateFeed feed, string channel, string? version)
    {
        // Publishers order newest first. An explicit version is never silently substituted.
        var release = feed.Releases.FirstOrDefault(x => x.Channel == channel && (version is null || x.Version == version))
            ?? throw new InvalidDataException("The signed feed has no matching release.");
        ValidateVersion(release.Version);
        return release;
    }

    public async Task<string> InstallAsync(string channel, string? version, CancellationToken ct)
    {
        Directory.CreateDirectory(_root);
        using var lease = new FileStream(Path.Combine(_root, "update.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var release = await CheckAsync(channel, version, ct);
        var activeId = ReadActivation()?.Current;
        if (version is null && activeId is { Length: > 65 })
        {
            var previousVersion = activeId[..^65];
            if (Version.TryParse(previousVersion.Split('-')[0], out var previous) && Version.TryParse(release.Version.Split('-')[0], out var offered) && offered < previous)
                throw new InvalidOperationException("The feed offers an older version. Select --version explicitly or use rollback.");
        }
        var asset = release.Assets.SingleOrDefault(x => x.Rid == RuntimeInformation.RuntimeIdentifier)
            ?? throw new InvalidDataException("No update for this operating system and architecture.");
        RequireHttps(asset.Url);
        if (asset.Size <= 0 || asset.Size > 1024L * 1024 * 1024 || asset.Sha256.Length != 64)
            throw new InvalidDataException("Invalid update asset metadata.");
        var id = release.Version + "-" + asset.Sha256.ToLowerInvariant();
        var destination = BundlePath(id);
        var staging = Path.Combine(_root, ".staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var archive = Path.Combine(staging, "bundle.zip");
            await DownloadFileAsync(asset.Url, archive, asset.Size, ct);
            await using (var stream = File.OpenRead(archive))
            {
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
                if (stream.Length != asset.Size || !hash.Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Update asset size or SHA-256 does not match the signed manifest.");
            }
            var bundle = Path.Combine(staging, "bundle");
            ExtractBundle(archive, bundle);
            await SmokeCheckAsync(bundle, ct);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            // Never trust a pre-existing installation instead of the freshly verified archive.
            if (Directory.Exists(destination))
                throw new InvalidOperationException("This bundle is already installed. Use update launch or rollback; existing files are not overwritten.");
            Directory.Move(bundle, destination);
            var previous = ReadActivation()?.Current;
            WriteLaunchers();
            WriteAtomic(ActivationPath, JsonSerializer.Serialize(new BundleActivation(id, previous), UpdateJsonContext.Default.BundleActivation));
            return destination;
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }

    public string Rollback()
    {
        using var lease = new FileStream(Path.Combine(_root, "update.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var active = ReadActivation() ?? throw new InvalidOperationException("No managed bundle is active.");
        var previous = active.Previous ?? throw new InvalidOperationException("No previous bundle is available.");
        var path = BundlePath(previous);
        ValidateBundle(path);
        WriteAtomic(ActivationPath, JsonSerializer.Serialize(new BundleActivation(previous, active.Current), UpdateJsonContext.Default.BundleActivation));
        return path;
    }

    public string GetActiveExecutable(string component)
    {
        var current = ReadActivation()?.Current ?? throw new InvalidOperationException("No managed bundle is active.");
        return Executable(BundlePath(current), component);
    }

    private string BundlePath(string id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 160 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-')) || id.Contains(".."))
            throw new InvalidDataException("Invalid bundle identifier.");
        return Path.Combine(_root, "bundles", id);
    }
    private static void ValidateVersion(string version)
    {
        if (string.IsNullOrEmpty(version) || version.Length > 80 || version.Contains("..") || version.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-')))
            throw new InvalidDataException("Invalid release version.");
    }
    public static void ExtractBundle(string archivePath, string destination)
    {
        Directory.CreateDirectory(destination);
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > 20000) throw new InvalidDataException("Too many update files.");
        long total = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (name.StartsWith('/') || name.Contains(':') || name.Split('/').Any(x => x is ".." or ".")
                || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || !seen.Add(name))
                throw new InvalidDataException("Unsafe update archive path.");
            total = checked(total + entry.Length);
            if (total > 4L * 1024 * 1024 * 1024) throw new InvalidDataException("Expanded update is too large.");
            var path = Path.Combine(destination, name);
            if (name.EndsWith('/')) { Directory.CreateDirectory(path); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var source = entry.Open();
            using var target = new FileStream(path, FileMode.CreateNew);
            source.CopyTo(target);
        }
        ValidateBundle(destination);
        if (!OperatingSystem.IsWindows())
            foreach (var component in new[] { "cli", "gateway", "companion" })
                File.SetUnixFileMode(Executable(destination, component), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    private static void ValidateBundle(string directory)
    {
        foreach (var component in new[] { "cli", "gateway", "companion" })
            if (!File.Exists(Executable(directory, component))) throw new InvalidDataException("The update is missing " + component + ".");
    }
    private static string Executable(string directory, string component)
    {
        var name = component switch { "cli" => "openclaw", "gateway" => "OpenClaw.Gateway", "companion" => "OpenClaw.Companion", _ => throw new ArgumentException("Unknown component.") };
        return Path.Combine(directory, component, name + (OperatingSystem.IsWindows() ? ".exe" : ""));
    }
    private static async Task SmokeCheckAsync(string bundle, CancellationToken ct)
    {
        await RunCheckAsync(Executable(bundle, "cli"), ["version"], bundle, ct);
        var smokeConfig = Path.Combine(Path.GetDirectoryName(bundle)!, "smoke.json");
        using (var stream = File.Create(smokeConfig))
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject(); writer.WriteStartObject("OpenClaw");
            writer.WriteString("BindAddress", "127.0.0.1"); writer.WriteNumber("Port", 19899);
            writer.WriteStartObject("Memory"); writer.WriteString("StoragePath", Path.Combine(Path.GetDirectoryName(bundle)!, "smoke-memory")); writer.WriteEndObject();
            writer.WriteStartObject("Llm"); writer.WriteString("Provider", "ollama"); writer.WriteString("Model", "llama3.2"); writer.WriteEndObject();
            writer.WriteEndObject(); writer.WriteEndObject();
        }
        await RunCheckAsync(Executable(bundle, "gateway"), ["--config", smokeConfig, "--doctor"], bundle, ct);
    }
    private static async Task RunCheckAsync(string executable, string[] arguments, string workingDirectory, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = workingDirectory };
        foreach (var key in info.Environment.Keys.Where(k => k.StartsWith("OPENCLAW", StringComparison.OrdinalIgnoreCase)).ToArray()) info.Environment.Remove(key);
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("Cannot start staged component.");
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token); await Task.WhenAll(stdout, stderr); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        if (process.ExitCode != 0) throw new InvalidDataException("Staged component failed its startup check; current bundle is unchanged.");
    }
    private async Task<byte[]> DownloadBytesAsync(string url, long maximum, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        RequireHttps(response.RequestMessage?.RequestUri?.AbsoluteUri ?? url);
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        await CopyBoundedAsync(input, output, maximum, ct);
        return output.ToArray();
    }
    private async Task DownloadFileAsync(string url, string path, long maximum, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        RequireHttps(response.RequestMessage?.RequestUri?.AbsoluteUri ?? url);
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        await CopyBoundedAsync(input, output, maximum, ct);
    }
    private static async Task CopyBoundedAsync(Stream input, Stream output, long maximum, CancellationToken ct)
    {
        var buffer = new byte[81920]; long count = 0; int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            count += read;
            if (count > maximum) throw new InvalidDataException("Update download exceeds its allowed size.");
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }
    private static void RequireHttps(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidDataException("Update downloads require HTTPS without embedded credentials.");
    }
    private static void WriteAtomic(string path, string text)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N");
        try { File.WriteAllText(temporary, text); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private void WriteLaunchers()
    {
        if (!OperatingSystem.IsWindows())
        {
            var launcher = Path.Combine(_root, "launch");
            File.WriteAllText(launcher, """
                #!/bin/sh
                set -eu
                root=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
                component=${1:-companion}
                if [ "$#" -gt 0 ]; then shift; fi
                case "$component" in
                  cli) binary=openclaw ;;
                  gateway) binary=OpenClaw.Gateway ;;
                  companion) binary=OpenClaw.Companion ;;
                  *) echo "Expected cli, gateway or companion" >&2; exit 2 ;;
                esac
                id=$(sed -n 's/.*"Current":"\([^" ]*\)".*/\1/p' "$root/active.json")
                case "$id" in ''|*[!a-zA-Z0-9.-]*|*..*) echo "Invalid activation" >&2; exit 2 ;; esac
                exec "$root/bundles/$id/$component/$binary" "$@"
                """);
            File.SetUnixFileMode(launcher, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        // PowerShell reads the pointer on every launch, including after rollback.
        File.WriteAllText(Path.Combine(_root, "launch.ps1"), "param([ValidateSet('cli','gateway','companion')][string]$Component='companion')\n$active=Get-Content (Join-Path $PSScriptRoot 'active.json')|ConvertFrom-Json\n$names=@{cli='openclaw';gateway='OpenClaw.Gateway';companion='OpenClaw.Companion'}\n$exe=Join-Path $PSScriptRoot ('bundles/'+$active.Current+'/'+$Component+'/'+$names[$Component])\nif($IsWindows -or $env:OS -eq 'Windows_NT'){$exe+='.exe'}\n& $exe @args\nexit $LASTEXITCODE\n");
    }
}
