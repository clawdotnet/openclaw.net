using Microsoft.Extensions.AI;
using OpenClaw.Agent;
using OpenClaw.Core.Models;
using Xunit;

namespace OpenClaw.Tests;

/// <summary>
/// The media cache root is configuration, so a deployment that moves it leaves every historical
/// attachment reference pointing at a directory that no longer exists. These tests pin the read-time
/// recovery: a cache-shaped reference that is missing at its recorded location is retried under the
/// current root, and nothing else is.
/// </summary>
/// <remarks>
/// <para>
/// Everything here runs through <see cref="ChatMessageComposer"/> with no byte source supplied, because
/// the wiring is half of what is being tested — a fallback that only exists when a caller remembers to
/// construct it is not a fix. The files are real: the miss path is decided by
/// <see cref="File.Exists(string)"/>, which a fake would answer by agreeing with itself.
/// </para>
/// <para>
/// See docs/superpowers/specs/2026-10-19-attachment-by-reference-design.md, "P4 遗留".
/// </para>
/// </remarks>
public sealed class MediaCacheRelocationTests : IDisposable
{
    /// <summary>Name shape the cache generates; <see cref="AttachmentReferences.TryGetMediaAssetId"/> accepts it.</summary>
    private const string AssetName = "media_0123456789abcdef.png";

    private readonly string _workspace;
    private readonly string _previousRoot;
    private readonly string _currentRoot;

    public MediaCacheRelocationTests()
    {
        _workspace = Path.Join(Path.GetTempPath(), $"openclaw-cache-move-{Guid.NewGuid():N}");
        _previousRoot = Path.Join(_workspace, "old-root");
        _currentRoot = Path.Join(_workspace, "new-root");
        Directory.CreateDirectory(_previousRoot);
        Directory.CreateDirectory(_currentRoot);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workspace, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not a test failure.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }

    [Fact]
    public async Task A_cache_asset_is_found_under_the_current_root_after_the_deployment_moved()
    {
        WriteAsset(_currentRoot, AssetName, "moved");

        // The recorded path is the old root's; the file only exists under the new one.
        var contents = await ComposeAsync(RecordedPath(AssetName));

        var data = Assert.Single(contents.OfType<DataContent>());
        Assert.Equal("moved", Text(data));
        Assert.DoesNotContain(contents, content => IsPlaceholder(content, "unavailable"));
    }

    [Fact]
    public async Task A_recorded_location_that_still_exists_wins_over_the_current_root()
    {
        WriteAsset(_previousRoot, AssetName, "original");
        WriteAsset(_currentRoot, AssetName, "some-other-copy");

        var contents = await ComposeAsync(RecordedPath(AssetName));

        // The reference is not moved because it is resolvable; relocating first would silently read a
        // different file than the one the session actually recorded.
        var data = Assert.Single(contents.OfType<DataContent>());
        Assert.Equal("original", Text(data));
    }

    [Fact]
    public async Task A_path_that_is_not_cache_shaped_is_not_relocated()
    {
        // Same file name in the current root, but the reference is an ordinary user path. Carrying the
        // name over would hand the model a file the user never attached.
        WriteAsset(_currentRoot, "cat.png", "moved");

        var contents = await ComposeAsync(Path.Join(_previousRoot, "cat.png"));

        Assert.Contains(contents, content => IsPlaceholder(content, "unavailable"));
        Assert.Empty(contents.OfType<DataContent>());
    }

    [Fact]
    public async Task An_asset_missing_from_every_root_is_still_a_placeholder()
    {
        // Nothing to find: the fallback must not invent one, and must not change the reason reported.
        var contents = await ComposeAsync(RecordedPath(AssetName));

        Assert.Contains(contents, content => IsPlaceholder(content, "unavailable"));
        Assert.Empty(contents.OfType<DataContent>());
    }

    [Fact]
    public void FromConfig_carries_the_cache_root_and_drops_one_it_cannot_resolve()
    {
        var resolved = AttachmentInlinePolicy.FromConfig(new AttachmentConfig(), _currentRoot);
        Assert.Equal(Path.GetFullPath(_currentRoot), resolved.CacheRoot);

        // A blank root is "no cache configured", not "the process directory".
        Assert.Null(AttachmentInlinePolicy.FromConfig(new AttachmentConfig(), "   ").CacheRoot);
        Assert.Null(AttachmentInlinePolicy.FromConfig(new AttachmentConfig()).CacheRoot);

        // An absent attachment section must not silently discard the root: the two settings come from
        // the same config object, so losing one while reading the other is a wiring bug, not a default.
        Assert.Equal(
            Path.GetFullPath(_currentRoot),
            AttachmentInlinePolicy.FromConfig(null, _currentRoot).CacheRoot);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private string RecordedPath(string fileName) => Path.Join(_previousRoot, fileName);

    private static void WriteAsset(string root, string fileName, string content)
        => File.WriteAllText(Path.Join(root, fileName), content);

    private static string Text(DataContent data) => System.Text.Encoding.UTF8.GetString(data.Data.Span);

    private async Task<List<AIContent>> ComposeAsync(string recordedPath)
    {
        var session = new Session
        {
            Id = "sess-cache-move",
            ChannelId = "websocket",
            SenderId = "user"
        };
        session.History.Add(AttachmentReferences.CreateUserTurn($"look at this\n[IMAGE_PATH:{recordedPath}]"));

        var composer = new ChatMessageComposer(
            AttachmentInlinePolicy.FromConfig(new AttachmentConfig(), _currentRoot));

        var messages = await composer.ComposeHistoryAsync(
            session,
            maxHistoryTurns: 50,
            exactLatestToolBatch: false,
            TestContext.Current.CancellationToken);

        return [.. Assert.Single(messages).Contents];
    }

    private static bool IsPlaceholder(AIContent content, string reason)
        => content is TextContent text
            && text.Text is not null
            && text.Text.Contains($"[attachment not sent ({reason}):", StringComparison.Ordinal);
}
