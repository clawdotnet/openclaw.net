using OpenClaw.Core.Models;

namespace OpenClaw.Agent;

/// <summary>
/// Runtime form of <see cref="AttachmentConfig"/>: everything the inliner needs to decide whether a
/// given attachment is worth sending to the provider on this request.
/// </summary>
/// <remarks>
/// <para>
/// Kept as a value object separate from the config type so the inliner and the composer can be tested
/// without building a whole <see cref="GatewayConfig"/>, and so the runtime never reads a mutable
/// config object while a turn is in flight.
/// </para>
/// <para>
/// <b>Degradation vs failure.</b> Running out of window, out of count, out of bytes, or hitting a
/// duplicate is <em>policy</em> — the intended outcome, always a placeholder. Only a source that
/// should have been readable but was not (missing file, permission denied) counts as a failure, and
/// only then does <see cref="FailureMode"/> decide between a placeholder and an exception.
/// </para>
/// </remarks>
public sealed record AttachmentInlinePolicy
{
    /// <summary>Policy used when nobody supplied a config: reference mode on, documented defaults.</summary>
    public static AttachmentInlinePolicy Default { get; } = new();

    /// <summary>
    /// Policy that reproduces the pre-reference behaviour exactly: no extraction, no inlining, marker
    /// text rendered the way it always was. Used when <see cref="AttachmentConfig.ReferenceMode"/> is
    /// false.
    /// </summary>
    public static AttachmentInlinePolicy Legacy { get; } = new()
    {
        ReferenceMode = false,
        InlineRecentUserTurns = 0
    };

    /// <summary>See <see cref="AttachmentConfig.ReferenceMode"/>.</summary>
    public bool ReferenceMode { get; init; } = true;

    /// <summary>
    /// How many of the most recent user turns have their attachments considered for inlining. Zero
    /// means "never inline anything", which is a deliberate kill switch rather than a sensible
    /// setting.
    /// </summary>
    public int InlineRecentUserTurns { get; init; } = 2;

    /// <summary>Byte ceiling for locally-read attachment data in one request.</summary>
    public long MaxInlineBytesPerRequest { get; init; } = 20L * 1024 * 1024;

    /// <summary>How many attachments from a single turn may be inlined.</summary>
    public int MaxInlineAttachmentsPerTurn { get; init; } = 4;

    /// <summary>Inline a repeated source once per request.</summary>
    public bool DeduplicateBySource { get; init; } = true;

    /// <summary>"placeholder" (default) or "fail".</summary>
    public string FailureMode { get; init; } = "placeholder";

    /// <summary>
    /// Full path of the media-cache root currently in use, or <see langword="null"/> when the caller did
    /// not supply one (tests, and configurations with no cache).
    /// </summary>
    /// <remarks>
    /// Only used to retry a cache-shaped reference that no longer exists at its recorded location — see
    /// <see cref="MediaCacheRelocationByteSource"/>. It is not a general search path: the retry is
    /// restricted to file names that match the asset-id contract, and only the file name is carried over.
    /// </remarks>
    public string? CacheRoot { get; init; }

    /// <summary>
    /// True when an unreadable attachment must abort the turn instead of degrading to a placeholder.
    /// </summary>
    public bool FailOnUnavailable =>
        string.Equals(FailureMode?.Trim(), "fail", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Maps configuration onto a policy. Negative or nonsensical values are clamped rather than
    /// rejected: a bad config should soften the cost ceiling, not break every turn.
    /// </summary>
    /// <param name="config">Attachment settings; absent means <see cref="Default"/>.</param>
    /// <param name="mediaCacheRoot">
    /// <see cref="MultimodalConfig.MediaCachePath"/> as configured. Resolved to a full path here, and
    /// dropped when it cannot be resolved — an unusable cache root must not stop construction, and it
    /// only costs the relocation retry, not correctness.
    /// </param>
    public static AttachmentInlinePolicy FromConfig(AttachmentConfig? config, string? mediaCacheRoot = null)
    {
        var cacheRoot = NormalizeRoot(mediaCacheRoot);

        if (config is null)
            return cacheRoot is null ? Default : Default with { CacheRoot = cacheRoot };

        return new AttachmentInlinePolicy
        {
            ReferenceMode = config.ReferenceMode,
            InlineRecentUserTurns = Math.Max(0, config.InlineRecentUserTurns),
            MaxInlineBytesPerRequest = Math.Max(0, config.MaxInlineBytesPerRequest),
            MaxInlineAttachmentsPerTurn = Math.Max(0, config.MaxInlineAttachmentsPerTurn),
            DeduplicateBySource = config.DeduplicateBySource,
            FailureMode = string.IsNullOrWhiteSpace(config.FailureMode)
                ? Default.FailureMode
                : config.FailureMode.Trim(),
            CacheRoot = cacheRoot
        };
    }

    private static string? NormalizeRoot(string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
            return null;

        try
        {
            return Path.GetFullPath(root);
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

/// <summary>
/// Budget shared across every turn of a single <see cref="ChatMessageComposer"/> pass.
/// </summary>
/// <remarks>
/// Mutable on purpose: the composer hands the same instance to each turn so that the newest turn can
/// spend first (the composer visits turns newest-first) and older turns see what is left. A per-turn
/// budget would let N turns each spend the full ceiling.
/// </remarks>
public sealed class AttachmentInlineBudget
{
    private readonly AttachmentInlinePolicy _policy;
    private readonly HashSet<string> _seenSources = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private long _remainingBytes;
    private int _inlined;
    private int _degraded;

    public AttachmentInlineBudget(AttachmentInlinePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        _policy = policy;
        _remainingBytes = policy.MaxInlineBytesPerRequest;
    }

    /// <summary>Bytes still available to this request.</summary>
    public long RemainingBytes => _remainingBytes;

    /// <summary>Attachments actually sent (bytes or URI) in this request.</summary>
    public int InlinedCount => _inlined;

    /// <summary>Attachments replaced by a placeholder in this request.</summary>
    public int DegradedCount => _degraded;

    /// <summary>True when this source was already inlined in this request.</summary>
    public bool IsSourceSeen(string sourceKey)
        => _policy.DeduplicateBySource && _seenSources.Contains(sourceKey);

    /// <summary>
    /// Records that <paramref name="sourceKey"/> was inlined. Only call after the attachment has been
    /// successfully emitted — claiming a source that was then dropped for being too large would make
    /// a later, smaller occurrence look like a duplicate.
    /// </summary>
    public void ClaimSource(string sourceKey)
    {
        if (_policy.DeduplicateBySource)
            _seenSources.Add(sourceKey);
    }

    /// <summary>Takes <paramref name="bytes"/> from the budget when they fit.</summary>
    public bool TryConsumeBytes(long bytes)
    {
        if (bytes < 0 || bytes > _remainingBytes)
            return false;

        _remainingBytes -= bytes;
        return true;
    }

    /// <summary>
    /// Returns bytes that were reserved but never sent, e.g. a file that disappeared between the size
    /// probe and the read.
    /// </summary>
    public void ReleaseBytes(long bytes)
    {
        if (bytes <= 0)
            return;

        _remainingBytes += bytes;
    }

    public void RecordInlined() => _inlined++;

    public void RecordDegraded() => _degraded++;
}
