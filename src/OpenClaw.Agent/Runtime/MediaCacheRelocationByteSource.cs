using OpenClaw.Core.Models;

namespace OpenClaw.Agent;

/// <summary>
/// Finds a media-cache asset under the <em>current</em> cache root when the location recorded in
/// session history no longer resolves.
/// </summary>
/// <remarks>
/// <para>
/// <b>The failure this addresses.</b> Attachment references store the payload's absolute path
/// (<see cref="ChatAttachmentRef.Value"/>), and <see cref="MultimodalConfig.MediaCachePath"/> is a
/// configurable path. Move the deployment — or copy <c>./memory/media-cache</c> to a data volume — and
/// every historical reference points at a directory that no longer exists. The turn still runs, but the
/// image silently becomes an <c>unavailable</c> placeholder even though the bytes are sitting in the new
/// root under the same file name.
/// </para>
/// <para>
/// <b>Why a fallback and not a migration.</b> Rewriting history would have to touch every session file
/// and would still be wrong for the case where the files are genuinely gone; a read-time retry costs one
/// <see cref="File.Exists(string)"/> on the miss path and needs no schema change. This matches the
/// decision already recorded for older sessions ("no migration, read-time fallback only").
/// </para>
/// <para>
/// <b>The guard is the asset-id contract.</b> Relocation is attempted only when the recorded path is
/// cache-shaped (<see cref="AttachmentReferences.TryGetMediaAssetId"/>) — exact <c>media_</c> prefix,
/// exactly sixteen lowercase hex characters. Only the file <em>name</em> is carried over, never the
/// directory, so a recorded path cannot steer a read outside the cache root. A user file that merely
/// happens to share a name is not relocated; it degrades to a placeholder the way it always did.
/// </para>
/// <para>
/// <b>It does not mask unreadable-but-present assets.</b> When the recorded path exists, the retry is
/// skipped entirely: a locked or permission-denied file stays a read failure rather than silently
/// turning into "read some other copy".
/// </para>
/// </remarks>
public sealed class MediaCacheRelocationByteSource : IAttachmentByteSource
{
    private readonly IAttachmentByteSource _inner;
    private readonly string _cacheRoot;

    /// <param name="cacheRoot">Full path of the cache root in use now.</param>
    /// <param name="inner">
    /// Source that actually reads. Defaults to the local file system; a test double here makes "one read,
    /// not two" observable.
    /// </param>
    public MediaCacheRelocationByteSource(string cacheRoot, IAttachmentByteSource? inner = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        _cacheRoot = Path.GetFullPath(cacheRoot);
        _inner = inner ?? FileSystemAttachmentByteSource.Instance;
    }

    /// <summary>Full path of the cache root this source retries under.</summary>
    public string CacheRoot => _cacheRoot;

    public async ValueTask<long?> GetSizeAsync(string path, CancellationToken ct)
    {
        var size = await _inner.GetSizeAsync(path, ct).ConfigureAwait(false);
        if (size is not null)
            return size;

        var relocated = RelocatedPath(path);
        return relocated is null
            ? null
            : await _inner.GetSizeAsync(relocated, ct).ConfigureAwait(false);
    }

    public async ValueTask<byte[]?> ReadAsync(string path, CancellationToken ct)
    {
        var bytes = await _inner.ReadAsync(path, ct).ConfigureAwait(false);
        if (bytes is not null)
            return bytes;

        var relocated = RelocatedPath(path);
        return relocated is null
            ? null
            : await _inner.ReadAsync(relocated, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Name of the asset to retry under the current root, or <see langword="null"/> when relocating would
    /// be wrong or pointless.
    /// </summary>
    /// <remarks>
    /// Called only after a read already failed, so the <see cref="File.Exists(string)"/> probe here is not
    /// on the hot path. It distinguishes "the asset is gone" (relocate) from "the asset is there but
    /// unreadable" (do not) — those are different operational problems and must not report the same way.
    /// </remarks>
    private string? RelocatedPath(string path)
    {
        if (string.IsNullOrEmpty(path) || !AttachmentReferences.TryGetMediaAssetId(path, out _))
            return null;

        try
        {
            if (File.Exists(path))
                return null;

            var fileName = Path.GetFileName(path);
            if (string.IsNullOrEmpty(fileName))
                return null;

            var candidate = Path.Combine(_cacheRoot, fileName);
            return File.Exists(candidate) ? candidate : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
        catch (PathTooLongException)
        {
            return null;
        }
    }
}
