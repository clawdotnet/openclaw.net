using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace OpenClaw.Core.Backup;

public sealed class InstanceBackupPlan
{
    public Dictionary<string, string> Roots { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Each required category maps to one declared root, even when several share that root.</summary>
    public Dictionary<string, string> Categories { get; set; } = new(StringComparer.Ordinal);
    /// <summary>References only. Never resolved or copied from a credential provider.</summary>
    public string[] SecretReferences { get; set; } = [];
}
public sealed class InstanceBackupManifest
{
    public int SchemaVersion { get; set; } = 1;
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public InstanceBackupPlan Plan { get; set; } = new();
    public List<InstanceBackupFile> Files { get; set; } = [];
}
public sealed record InstanceBackupFile(string Path, long Length, string Sha256);
[JsonSerializable(typeof(InstanceBackupPlan))]
[JsonSerializable(typeof(InstanceBackupManifest))]
public partial class BackupJsonContext : JsonSerializerContext;

/// <summary>Offline snapshots only. Never constructs a host, connects to providers, or dispatches jobs.</summary>
public static class InstanceBackup
{
    private static readonly string[] RequiredCategories = ["configuration", "sessions", "goals", "schedules", "governance"];
    private const long MaxBytes = 100L * 1024 * 1024 * 1024;
    private const int MaxFiles = 100_000;

    public static async Task CreateAsync(InstanceBackupPlan plan, string destination, bool offline, CancellationToken ct = default)
    {
        if (!offline) throw new InvalidOperationException("Stop every writer and confirm offline mode before creating a backup.");
        ValidatePlan(plan);
        destination = Path.GetFullPath(destination);
        foreach (var root in plan.Roots.Values)
            if (Within(Path.GetFullPath(root), destination) || Within(destination, Path.GetFullPath(root)))
                throw new InvalidOperationException("Backup destination must be outside every source root.");
        EnsureNewDestination(destination);
        var staging = destination + ".staging-" + Guid.NewGuid().ToString("N");
        try
        {
            PrivateDirectory(staging);
            var manifest = new InstanceBackupManifest { Plan = plan };
            long bytes = 0;
            foreach (var (name, rawRoot) in plan.Roots.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                var root = Path.GetFullPath(rawRoot);
                foreach (var file in EnumerateSafe(root))
                {
                    ct.ThrowIfCancellationRequested();
                    var relative = name + "/" + Path.GetRelativePath(root, file).Replace('\\', '/');
                    var length = new FileInfo(file).Length;
                    bytes = checked(bytes + length);
                    if (bytes > MaxBytes || manifest.Files.Count >= MaxFiles) throw new InvalidDataException("Backup size limit exceeded.");
                    var target = SafePath(Path.Combine(staging, "payload"), relative);
                    PrivateDirectory(Path.GetDirectoryName(target)!);
                    await CopyPrivateAsync(file, target, ct);
                    manifest.Files.Add(new(relative, length, await DigestAsync(target, ct)));
                }
            }
            // Check both bytes and the file inventory again; changes invalidate the entire snapshot.
            var currentPaths = plan.Roots.SelectMany(p => EnumerateSafe(Path.GetFullPath(p.Value))
                .Select(f => p.Key + "/" + Path.GetRelativePath(Path.GetFullPath(p.Value), f).Replace('\\', '/'))).Order().ToArray();
            if (!currentPaths.SequenceEqual(manifest.Files.Select(f => f.Path).Order()))
                throw new IOException("Source inventory changed during backup. Stop all writers and retry.");
            foreach (var file in manifest.Files)
            {
                var parts = file.Path.Split('/', 2);
                var source = SafePath(Path.GetFullPath(plan.Roots[parts[0]]), parts[1]);
                if (new FileInfo(source).Length != file.Length || await DigestAsync(source, ct) != file.Sha256)
                    throw new IOException("Source changed during backup. Stop all writers and retry.");
            }
            var manifestPath = Path.Combine(staging, "manifest.json");
            await WritePrivateAsync(manifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest, BackupJsonContext.Default.InstanceBackupManifest), ct);
            await ValidateAsync(staging, ct);
            Directory.Move(staging, destination);
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }

    public static async Task<InstanceBackupManifest> ValidateAsync(string backup, CancellationToken ct = default)
    {
        backup = Path.GetFullPath(backup); RejectLinks(backup);
        var manifestPath = SafePath(backup, "manifest.json");
        if (new FileInfo(manifestPath).Length > 32 * 1024 * 1024) throw new InvalidDataException("Manifest too large.");
        var manifest = JsonSerializer.Deserialize(await File.ReadAllTextAsync(manifestPath, ct), BackupJsonContext.Default.InstanceBackupManifest)
            ?? throw new InvalidDataException("Missing manifest.");
        if (manifest.SchemaVersion != 1 || manifest.Files.Count > MaxFiles) throw new InvalidDataException("Unsupported backup manifest.");
        ValidatePlan(manifest.Plan, requireSources: false);
        var payload = Path.Combine(backup, "payload");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long bytes = 0;
        foreach (var file in manifest.Files)
        {
            ct.ThrowIfCancellationRequested();
            if (!names.Add(file.Path) || !manifest.Plan.Roots.ContainsKey(file.Path.Split('/')[0]) || file.Length < 0)
                throw new InvalidDataException("Duplicate or invalid backup path.");
            bytes = checked(bytes + file.Length);
            if (bytes > MaxBytes) throw new InvalidDataException("Backup size limit exceeded.");
            var path = SafePath(payload, file.Path);
            if (new FileInfo(path).Length != file.Length || await DigestAsync(path, ct) != file.Sha256)
                throw new InvalidDataException($"Backup checksum mismatch: {file.Path}");
        }
        var actual = Directory.Exists(payload) ? EnumerateSafe(payload).Select(p => Path.GetRelativePath(payload, p).Replace('\\', '/')).ToArray() : [];
        if (actual.Length != names.Count || actual.Any(p => !names.Contains(p))) throw new InvalidDataException("Unexpected backup payload files.");
        return manifest;
    }

    public static async Task RestoreAsync(string backup, string destination, CancellationToken ct = default)
    {
        var manifest = await ValidateAsync(backup, ct);
        destination = Path.GetFullPath(destination); EnsureNewDestination(destination);
        if (Within(Path.GetFullPath(backup), destination) || Within(destination, Path.GetFullPath(backup)))
            throw new InvalidOperationException("Restore destination must be outside the backup.");
        var staging = destination + ".staging-" + Guid.NewGuid().ToString("N");
        try
        {
            PrivateDirectory(staging);
            foreach (var name in manifest.Plan.Roots.Keys) PrivateDirectory(Path.Combine(staging, name));
            foreach (var file in manifest.Files)
            {
                var target = SafePath(staging, file.Path); PrivateDirectory(Path.GetDirectoryName(target)!);
                await CopyPrivateAsync(SafePath(Path.Combine(Path.GetFullPath(backup), "payload"), file.Path), target, ct);
                if (await DigestAsync(target, ct) != file.Sha256) throw new InvalidDataException("Backup changed during restore.");
            }
            // SQLite checks run only on the isolated copy, including any captured WAL. Nothing starts the gateway.
            foreach (var file in manifest.Files.Where(f => f.Path.EndsWith(".db", StringComparison.OrdinalIgnoreCase) || f.Path.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase)))
            {
                using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = SafePath(staging, file.Path), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
                await connection.OpenAsync(ct);
                using var command = connection.CreateCommand(); command.CommandText = "PRAGMA quick_check;";
                if (!Equals(await command.ExecuteScalarAsync(ct), "ok")) throw new InvalidDataException($"SQLite validation failed: {file.Path}");
            }
            await WritePrivateAsync(Path.Combine(staging, "RESTORE-REQUIRES-REVIEW.txt"),
                System.Text.Encoding.UTF8.GetBytes("Offline restore validated. Do not start until paths and secret references have been reviewed. Original absolute paths are not rewritten. No schedules, tools, or providers were started.\n"), ct);
            await WritePrivateAsync(Path.Combine(staging, "restore-manifest.json"), JsonSerializer.SerializeToUtf8Bytes(manifest, BackupJsonContext.Default.InstanceBackupManifest), ct);
            Directory.Move(staging, destination);
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }

    private static void ValidatePlan(InstanceBackupPlan plan, bool requireSources = true)
    {
        if (plan.Roots.Count == 0 || RequiredCategories.Any(c => !plan.Categories.TryGetValue(c, out var root) || !plan.Roots.ContainsKey(root)))
            throw new InvalidDataException("Plan must map configuration, sessions, goals, schedules, and governance to declared roots.");
        foreach (var (name, root) in plan.Roots)
        {
            if (name.Length == 0 || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_')) throw new InvalidDataException("Invalid root name.");
            if (string.IsNullOrWhiteSpace(root)) throw new InvalidDataException("Missing root path.");
            if (requireSources) { if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root); RejectLinks(Path.GetFullPath(root)); }
        }
        if (plan.SecretReferences.Any(r => string.IsNullOrWhiteSpace(r) || !r.Contains(':') || r.Contains('\n')))
            throw new InvalidDataException("Secret references must be provider-qualified references, never credential values.");
    }
    private static IEnumerable<string> EnumerateSafe(string root)
    {
        RejectLinks(root);
        foreach (var entry in Directory.EnumerateFileSystemEntries(root).Order(StringComparer.Ordinal))
        {
            RejectLinks(entry);
            if (Directory.Exists(entry)) { foreach (var file in EnumerateSafe(entry)) yield return file; }
            else yield return entry;
        }
    }
    private static bool Within(string root, string path) => path.Equals(root, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static string SafePath(string root, string relative)
    {
        if (relative.Contains('\\') || relative.Contains(':') || relative.Split('/').Any(p => p is "" or "." or "..") || Path.IsPathRooted(relative))
            throw new InvalidDataException("Unsafe backup path.");
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!Within(Path.GetFullPath(root), path)) throw new InvalidDataException("Backup path escapes payload.");
        RejectLinks(path); return path;
    }
    private static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("Backup and restore paths must not traverse symbolic links or reparse points.");
    }
    private static void EnsureNewDestination(string path)
    { RejectLinks(path); if (File.Exists(path) || Directory.Exists(path)) throw new IOException("Destination already exists; restore never overwrites data."); }
    private static void PrivateDirectory(string path)
    { if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path); else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    private static FileStream PrivateFile(string path)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(path, options);
    }
    private static async Task CopyPrivateAsync(string source, string target, CancellationToken ct)
    { RejectLinks(source); using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read); using var output = PrivateFile(target); await input.CopyToAsync(output, ct); output.Flush(true); }
    private static async Task WritePrivateAsync(string path, byte[] bytes, CancellationToken ct)
    { using var stream = PrivateFile(path); await stream.WriteAsync(bytes, ct); stream.Flush(true); }
    private static async Task<string> DigestAsync(string path, CancellationToken ct)
    { using var stream = File.OpenRead(path); return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)); }
}
