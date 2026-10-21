using OpenClaw.Core.Models;

namespace OpenClaw.Gateway.Extensions;

/// <summary>
/// Copies inbound media into the media cache and rewrites the marker to point at the copy.
/// </summary>
/// <remarks>
/// <para>
/// <b>The problem this solves.</b> Channels download inbound media into <c>Path.GetTempPath()</c> —
/// <c>openclaw_feishu</c>, <c>openclaw_wecom</c>, <c>openclaw_dingtalk</c> — and emit
/// <c>[IMAGE_PATH:/tmp/…]</c>. Turn those files into attachment references and the session history now
/// stores an absolute path into a directory the OS is entitled to clean whenever it likes. The failure
/// is silent and delayed: the image works today, becomes a placeholder after a reboot or a temp sweep,
/// and nothing in the transcript says why.
/// </para>
/// <para>
/// <b>Why here and not in the runtime.</b> This runs at the gateway boundary, on the message text that is
/// about to be dispatched — the last moment where both the marker and the cache root are available, and
/// the only place that knows the cache root at all. Both runtimes take the same rewritten text, so the
/// guarantee holds for the self-built and the MAF path without either of them knowing about caching.
/// </para>
/// <para>
/// <b>Copy, not move.</b> The channel logs the temp path and may still hold it; moving another
/// component's file is the kind of ownership violation that shows up as a mystery <c>ENOENT</c> later.
/// The cost is one extra copy on disk, which <see cref="OpenClaw.Gateway.MediaCacheStore"/> has no
/// retention policy for yet — growth is a pre-existing operational gap, not something added here.
/// </para>
/// <para>
/// Every failure degrades to "marker keeps its original location". Persisting is an improvement to
/// durability; refusing to dispatch a message because a temp file could not be copied would turn that
/// improvement into an outage.
/// </para>
/// <para>
/// <b>Known cost: the display name.</b> After the rewrite the reference's file name is the cache's
/// (<c>media_0123456789abcdef.png</c>) instead of the channel's (<c>cat.png</c>), because the asset id is
/// recognised from the file name and both want that slot. The original name survives in the cache's
/// sidecar metadata, and the placeholder carries <c>ref=…</c>, so nothing is unrecoverable — but a
/// placeholder reads worse this way. Keeping the channel name would mean dropping the id from the path
/// and looking it up on the read path, which turns every placeholder into a disk read.
/// </para>
/// </remarks>
internal static class InboundAttachmentPersister
{
    /// <summary>
    /// Rewrites path markers in <paramref name="text"/> so they point into <paramref name="cacheRoot"/>.
    /// </summary>
    /// <param name="text">Inbound message text, markers and all.</param>
    /// <param name="mediaCache">Store that owns the cache root.</param>
    /// <param name="cacheRoot">Full path of the cache root; markers already inside it are left alone.</param>
    /// <param name="logger">Diagnostics for the degrade path.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The rewritten text, or the original when nothing needed persisting.</returns>
    public static async Task<string> PersistAsync(
        string text,
        MediaCacheStore mediaCache,
        string cacheRoot,
        ILogger logger,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(mediaCache);
        ArgumentNullException.ThrowIfNull(logger);

        // Cheap rejection first: most messages carry no path marker at all, and extracting on every turn
        // would allocate a marker list for nothing.
        if (!text.Contains("[IMAGE_PATH:", StringComparison.Ordinal) &&
            !text.Contains("[FILE_PATH:", StringComparison.Ordinal))
        {
            return text;
        }

        var (markers, _) = MediaMarkerProtocol.Extract(text);
        var rewritten = text;

        foreach (var marker in markers)
        {
            if (marker.Kind is not (MediaMarkerKind.ImagePath or MediaMarkerKind.FilePath))
                continue;

            if (string.IsNullOrWhiteSpace(marker.Value))
                continue;

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(marker.Value);
            }
            catch (ArgumentException)
            {
                continue;
            }
            catch (NotSupportedException)
            {
                continue;
            }
            catch (PathTooLongException)
            {
                continue;
            }

            if (IsUnderRoot(fullPath, cacheRoot) || !File.Exists(fullPath))
                continue;

            if (!LocalAttachmentFile.TryRead(fullPath, out var bytes))
                continue;

            try
            {
                var mediaType = AttachmentReferences.TryResolveMediaType(fullPath) ?? "application/octet-stream";
                var asset = await mediaCache
                    .SaveAsync(bytes, mediaType, Path.GetFileName(fullPath), ct)
                    .ConfigureAwait(false);

                // Replace the exact marker line rather than re-rendering the message: rebuilding from
                // Extract would move every marker to the end and drop the original line ordering.
                rewritten = rewritten.Replace(
                    MediaMarkerProtocol.Format(marker),
                    MediaMarkerProtocol.Format(new MediaMarker(marker.Kind, asset.Path)),
                    StringComparison.Ordinal);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or MediaAssetTooLargeException)
            {
                logger.LogDebug(
                    exception,
                    "Could not persist inbound attachment {Path}; the marker keeps its original location.",
                    fullPath);
            }
        }

        return rewritten;
    }

    /// <summary>
    /// True when <paramref name="path"/> is the cache root or inside it.
    /// </summary>
    /// <remarks>
    /// Compared on the full path with a trailing separator so that a sibling directory sharing a prefix
    /// (<c>/cache2</c> next to <c>/cache</c>) is not mistaken for the cache itself.
    /// </remarks>
    private static bool IsUnderRoot(string path, string cacheRoot)
    {
        if (string.IsNullOrWhiteSpace(cacheRoot))
            return false;

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var normalizedRoot = cacheRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                             + Path.DirectorySeparatorChar;

        return path.StartsWith(normalizedRoot, comparison);
    }
}
