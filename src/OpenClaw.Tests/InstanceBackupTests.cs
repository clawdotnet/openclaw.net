using System.Text.Json;
using Microsoft.Data.Sqlite;
using OpenClaw.Core.Backup;
using Xunit;

namespace OpenClaw.Tests;

public sealed class InstanceBackupTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "backup-test-" + Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(_root, "source");
    private string Backup => Path.Combine(_root, "backup");
    private string Restore => Path.Combine(_root, "restored");
    public InstanceBackupTests() { Directory.CreateDirectory(Source); File.WriteAllText(Path.Combine(Source, "sessions.json"), "{\"history\":[]}"); }
    private InstanceBackupPlan Plan => new()
    {
        Roots = new() { ["state"] = Source },
        Categories = new() { ["configuration"] = "state", ["sessions"] = "state", ["goals"] = "state", ["schedules"] = "state", ["governance"] = "state" },
        SecretReferences = ["env:MODEL_API_KEY"]
    };
    [Fact]
    public async Task RoundTripPreservesDurableDataAndReferencesWithoutStartingJobs()
    {
        File.WriteAllText(Path.Combine(Source, "goals.json"), "{\"status\":\"active\"}");
        File.WriteAllText(Path.Combine(Source, "schedules.json"), "{\"enabled\":true}");
        await InstanceBackup.CreateAsync(Plan, Backup, true);
        await InstanceBackup.RestoreAsync(Backup, Restore);
        Assert.Equal(File.ReadAllText(Path.Combine(Source, "goals.json")), File.ReadAllText(Path.Combine(Restore, "state", "goals.json")));
        var manifest = await InstanceBackup.ValidateAsync(Backup);
        Assert.Equal("env:MODEL_API_KEY", Assert.Single(manifest.Plan.SecretReferences));
        Assert.True(File.Exists(Path.Combine(Restore, "RESTORE-REQUIRES-REVIEW.txt")));
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(Restore, "state", "goals.json")));
    }
    [Fact]
    public async Task RefusesLiveModeIncompletePlanAndOverwrites()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => InstanceBackup.CreateAsync(Plan, Backup, false));
        var plan = Plan; plan.Categories.Remove("schedules");
        await Assert.ThrowsAsync<InvalidDataException>(() => InstanceBackup.CreateAsync(plan, Backup, true));
        await InstanceBackup.CreateAsync(Plan, Backup, true);
        Directory.CreateDirectory(Restore); File.WriteAllText(Path.Combine(Restore, "keep"), "original");
        await Assert.ThrowsAsync<IOException>(() => InstanceBackup.RestoreAsync(Backup, Restore));
        Assert.Equal("original", File.ReadAllText(Path.Combine(Restore, "keep")));
    }
    [Fact]
    public async Task CorruptionFailsBeforeDestinationIsPublished()
    {
        await InstanceBackup.CreateAsync(Plan, Backup, true);
        File.AppendAllText(Path.Combine(Backup, "payload", "state", "sessions.json"), "tampered");
        await Assert.ThrowsAsync<InvalidDataException>(() => InstanceBackup.RestoreAsync(Backup, Restore));
        Assert.False(Directory.Exists(Restore));
    }
    [Fact]
    public async Task TraversalAndUnlistedPayloadAreRejected()
    {
        await InstanceBackup.CreateAsync(Plan, Backup, true);
        File.WriteAllText(Path.Combine(Backup, "payload", "unexpected"), "x");
        await Assert.ThrowsAsync<InvalidDataException>(() => InstanceBackup.ValidateAsync(Backup));
        File.Delete(Path.Combine(Backup, "payload", "unexpected"));
        var manifest = await InstanceBackup.ValidateAsync(Backup);
        manifest.Files[0] = manifest.Files[0] with { Path = "state/../../escape" };
        File.WriteAllText(Path.Combine(Backup, "manifest.json"), JsonSerializer.Serialize(manifest, BackupJsonContext.Default.InstanceBackupManifest));
        await Assert.ThrowsAsync<InvalidDataException>(() => InstanceBackup.RestoreAsync(Backup, Restore));
        Assert.False(Directory.Exists(Restore));
    }
    [Fact]
    public async Task SymlinkSourceAndDestinationAreRejected()
    {
        if (OperatingSystem.IsWindows()) return;
        var link = Path.Combine(Source, "link"); File.CreateSymbolicLink(link, Path.Combine(Source, "sessions.json"));
        await Assert.ThrowsAsync<InvalidDataException>(() => InstanceBackup.CreateAsync(Plan, Backup, true));
        File.Delete(link);
        Directory.CreateSymbolicLink(Path.Combine(_root, "alias"), Source);
        await Assert.ThrowsAsync<InvalidDataException>(() => InstanceBackup.CreateAsync(Plan, Path.Combine(_root, "alias", "backup"), true));
    }
    [Fact]
    public async Task ValidatesSqliteInIsolatedRestore()
    {
        using (var db = new SqliteConnection($"Data Source={Path.Combine(Source, "state.db")};Pooling=False"))
        {
            await db.OpenAsync(); using var command = db.CreateCommand();
            command.CommandText = "CREATE TABLE goals(id TEXT); INSERT INTO goals VALUES ('durable');"; await command.ExecuteNonQueryAsync();
        }
        await InstanceBackup.CreateAsync(Plan, Backup, true);
        await InstanceBackup.RestoreAsync(Backup, Restore);
        using var restored = new SqliteConnection($"Data Source={Path.Combine(Restore, "state", "state.db")};Mode=ReadOnly;Pooling=False");
        await restored.OpenAsync(); using var check = restored.CreateCommand(); check.CommandText = "SELECT id FROM goals";
        Assert.Equal("durable", await check.ExecuteScalarAsync());
    }
    [Theory]
    [InlineData("broken.db")]
    [InlineData("broken.sqlite3")]
    public async Task InvalidDatabaseLeavesNoPartialRestore(string fileName)
    {
        File.WriteAllText(Path.Combine(Source, fileName), "not a database");
        await InstanceBackup.CreateAsync(Plan, Backup, true);
        await Assert.ThrowsAsync<SqliteException>(() => InstanceBackup.RestoreAsync(Backup, Restore));
        Assert.False(Directory.Exists(Restore)); Assert.Empty(Directory.GetDirectories(_root, "*.staging-*"));
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
