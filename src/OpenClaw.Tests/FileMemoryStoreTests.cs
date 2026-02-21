using System.Text.Json;
using OpenClaw.Core.Memory;
using OpenClaw.Core.Models;
using Xunit;

namespace OpenClaw.Tests;

public sealed class FileMemoryStoreTests
{
    [Fact]
    public async Task NoteKey_WithPathTraversal_DoesNotEscapeBasePath()
    {
        var root = CreateTempDir();
        var store = new FileMemoryStore(root, maxCachedSessions: 4);

        var key = "../evil";
        await store.SaveNoteAsync(key, "x", CancellationToken.None);

        var escapedLegacy = Path.GetFullPath(Path.Combine(root, "notes", $"{key}.md"));
        Assert.False(File.Exists(escapedLegacy));

        var notesDir = Path.Combine(root, "notes");
        Assert.True(Directory.EnumerateFiles(notesDir, "*.md").Any());
    }

    [Fact]
    public async Task SessionId_WithPathTraversal_DoesNotEscapeBasePath()
    {
        var root = CreateTempDir();
        var store = new FileMemoryStore(root, maxCachedSessions: 4);

        var id = "../evil";
        var session = new Session { Id = id, ChannelId = "c", SenderId = "s" };
        await store.SaveSessionAsync(session, CancellationToken.None);

        var escapedLegacy = Path.GetFullPath(Path.Combine(root, "sessions", $"{id}.json"));
        Assert.False(File.Exists(escapedLegacy));

        var sessionsDir = Path.Combine(root, "sessions");
        Assert.True(Directory.EnumerateFiles(sessionsDir, "*.json").Any());
    }

    [Fact]
    public async Task LegacySessionFile_MigratesToEncodedAndDeletesLegacy()
    {
        var root = CreateTempDir();
        var store = new FileMemoryStore(root, maxCachedSessions: 4);

        var id = "websocket:sender";
        var legacyPath = Path.Combine(root, "sessions", $"{id}.json");
        var legacySession = new Session { Id = id, ChannelId = "websocket", SenderId = "sender" };

        await using (var stream = File.Create(legacyPath))
        {
            await JsonSerializer.SerializeAsync(stream, legacySession, CoreJsonContext.Default.Session, CancellationToken.None);
        }

        var loaded = await store.GetSessionAsync(id, CancellationToken.None);
        Assert.NotNull(loaded);

        Assert.False(File.Exists(legacyPath));
        Assert.Contains(
            Directory.EnumerateFiles(Path.Combine(root, "sessions"), "*.json"),
            p => !string.Equals(p, legacyPath, StringComparison.Ordinal));
    }

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "openclaw-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(path);
        return path;
    }
}
