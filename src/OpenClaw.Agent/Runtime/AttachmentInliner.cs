using Microsoft.Extensions.AI;
using OpenClaw.Core.Models;

namespace OpenClaw.Agent;

/// <summary>
/// Reads attachment bytes from a local path. Exists so the inliner can be tested against a counting
/// double — several of the policy rules are only provable by asserting that <em>no read happened</em>.
/// </summary>
public interface IAttachmentByteSource
{
    /// <summary>
    /// Size of the file, or <see langword="null"/> when it cannot be determined (missing, unreadable).
    /// Implementations must not read the contents: the whole point of asking for the size first is to
    /// reject a 40 MB video without pulling 40 MB through memory.
    /// </summary>
    ValueTask<long?> GetSizeAsync(string path, CancellationToken ct);

    /// <summary>Contents, or <see langword="null"/> when the file is no longer readable.</summary>
    ValueTask<byte[]?> ReadAsync(string path, CancellationToken ct);
}

/// <summary>Default <see cref="IAttachmentByteSource"/> over the local file system.</summary>
public sealed class FileSystemAttachmentByteSource : IAttachmentByteSource
{
    public static FileSystemAttachmentByteSource Instance { get; } = new();

    public ValueTask<long?> GetSizeAsync(string path, CancellationToken ct)
    {
        try
        {
            var info = new FileInfo(path);
            return ValueTask.FromResult(info.Exists ? (long?)info.Length : null);
        }
        catch (IOException)
        {
            return ValueTask.FromResult<long?>(null);
        }
        catch (UnauthorizedAccessException)
        {
            return ValueTask.FromResult<long?>(null);
        }
        catch (ArgumentException)
        {
            return ValueTask.FromResult<long?>(null);
        }
        catch (NotSupportedException)
        {
            return ValueTask.FromResult<long?>(null);
        }
    }

    public async ValueTask<byte[]?> ReadAsync(string path, CancellationToken ct)
    {
        try
        {
            return await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }
}

/// <summary>
/// Raised when an attachment that should have been readable was not, and the policy is
/// <c>FailureMode = "fail"</c>.
/// </summary>
public sealed class AttachmentUnavailableException : InvalidOperationException
{
    public AttachmentUnavailableException(ChatAttachmentRef attachment, string location)
        : base($"Attachment '{Describe(attachment)}' is no longer readable at '{location}'.")
    {
        Attachment = attachment;
    }

    public ChatAttachmentRef Attachment { get; }

    private static string Describe(ChatAttachmentRef attachment)
        => attachment.FileName ?? attachment.Value;
}

internal enum AttachmentSourceKind
{
    /// <summary>A local file the inliner can read and send as bytes.</summary>
    LocalFile,

    /// <summary>An absolute non-file URI; the provider fetches it, so no bytes and no cost here.</summary>
    Remote,

    /// <summary>
    /// Not media the inliner can act on, but the marker path rendered it as its literal value. Kept as
    /// text so behaviour is unchanged for things like channel file ids.
    /// </summary>
    RawText,

    /// <summary>Not media either, and the marker path silently dropped it.</summary>
    Omitted
}

internal readonly record struct AttachmentSource(AttachmentSourceKind Kind, string Location, Uri? Uri)
{
    /// <summary>Identity used for in-request deduplication. Empty when there is nothing to dedupe.</summary>
    public string Key => Kind switch
    {
        AttachmentSourceKind.LocalFile => "file:" + Location,
        AttachmentSourceKind.Remote => "uri:" + Uri!.AbsoluteUri,
        _ => string.Empty
    };
}

/// <summary>
/// Resolves attachment references into provider content for one request, under
/// <see cref="AttachmentInlinePolicy"/>.
/// </summary>
/// <remarks>
/// <para>
/// Responsibilities stop at "one reference in, one <see cref="AIContent"/> out". Roles, ordering and
/// turn assembly belong to <see cref="ChatMessageComposer"/>. That split is what lets the policy rules
/// be tested without a session, a history or a provider.
/// </para>
/// <para>
/// <b>Order of checks</b> (each one short-circuits to a placeholder):
/// outside window → turn limit → duplicate → byte budget → read. The order matters: the cheapest and
/// most deterministic reasons come first, so a failure message tells the operator which ceiling was
/// actually hit.
/// </para>
/// <para>
/// <b>Degradation granularity is one attachment, by position.</b> Three images where the budget fits
/// two must inline the first two and placeholder the third. Sorting by size to inline "the cheapest
/// two" would make the request non-deterministic and would silently drop the image the user listed
/// first.
/// </para>
/// </remarks>
public sealed class AttachmentInliner
{
    private readonly AttachmentInlinePolicy _policy;
    private readonly IAttachmentByteSource _byteSource;

    public AttachmentInliner(AttachmentInlinePolicy policy, IAttachmentByteSource? byteSource = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        _policy = policy;
        _byteSource = CreateByteSource(policy, byteSource);
    }

    /// <summary>
    /// Default reader, wrapped so that a cache asset recorded under an older cache root is still found.
    /// </summary>
    /// <remarks>
    /// Wrapping happens here rather than at the call sites so a caller that supplies a policy with a
    /// <see cref="AttachmentInlinePolicy.CacheRoot"/> gets the behaviour without remembering to pass a
    /// byte source — the two runtimes construct this the same way and neither should have to know.
    /// An explicitly supplied source is wrapped too, rather than replacing the behaviour: "which reader"
    /// and "which root" are independent questions, and silently dropping one because the other was
    /// answered is how a configuration ends up having no effect.
    /// </remarks>
    private static IAttachmentByteSource CreateByteSource(
        AttachmentInlinePolicy policy,
        IAttachmentByteSource? byteSource)
    {
        var inner = byteSource ?? FileSystemAttachmentByteSource.Instance;
        return policy.CacheRoot is null
            ? inner
            : new MediaCacheRelocationByteSource(policy.CacheRoot, inner);
    }

    /// <summary>
    /// Renders one turn's references to content, in order, one entry per reference.
    /// </summary>
    /// <param name="attachments">The turn's references, in the order the channel produced them.</param>
    /// <param name="inWindow">
    /// Whether this turn is inside the inlining window. False means every reference degrades — which is
    /// the common case for older turns, not an error.
    /// </param>
    /// <param name="budget">Shared across the request; see <see cref="AttachmentInlineBudget"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    public async ValueTask<IList<AIContent>> InlineAsync(
        IReadOnlyList<ChatAttachmentRef> attachments,
        bool inWindow,
        AttachmentInlineBudget budget,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(attachments);
        ArgumentNullException.ThrowIfNull(budget);

        var contents = new List<AIContent>(attachments.Count);
        var inlinedThisTurn = 0;

        foreach (var attachment in attachments)
        {
            var source = Classify(attachment);

            // Text-ish references carry no bytes and cost nothing, so the cost policy has nothing to
            // say about them. They render exactly as the marker path always did — including outside
            // the window, where a placeholder would be strictly less informative.
            if (source.Kind is AttachmentSourceKind.RawText)
            {
                contents.Add(new TextContent(attachment.Value));
                continue;
            }

            if (source.Kind is AttachmentSourceKind.Omitted)
                continue;

            if (!inWindow)
            {
                contents.Add(Degrade(attachment, "outside-window", budget));
                continue;
            }

            if (inlinedThisTurn >= _policy.MaxInlineAttachmentsPerTurn)
            {
                contents.Add(Degrade(attachment, "over-turn-limit", budget));
                continue;
            }

            if (budget.IsSourceSeen(source.Key))
            {
                contents.Add(Degrade(attachment, "duplicate", budget));
                continue;
            }

            if (source.Kind is AttachmentSourceKind.Remote)
            {
                // The provider fetches remote URIs itself: no bytes cross this process, so the byte
                // budget does not apply and the reference goes out as it always did.
                //
                // A data: URI is the exception — those bytes *are* the payload, already base64'd into
                // the marker. Charging the encoded length against the budget keeps the ceiling honest
                // without decoding and re-encoding something the provider accepts as-is.
                var payloadBytes = source.Uri!.Scheme.Equals("data", StringComparison.OrdinalIgnoreCase)
                    ? attachment.Value.Length
                    : 0L;

                if (payloadBytes > 0 && !budget.TryConsumeBytes(payloadBytes))
                {
                    contents.Add(Degrade(attachment, "over-byte-budget", budget, payloadBytes));
                    continue;
                }

                budget.ClaimSource(source.Key);
                budget.RecordInlined();
                inlinedThisTurn++;
                contents.Add(new UriContent(source.Uri, AttachmentReferences.MediaClassFor(attachment.Kind)));
                continue;
            }

            var size = attachment.SizeBytes > 0
                ? attachment.SizeBytes
                : await _byteSource.GetSizeAsync(source.Location, ct).ConfigureAwait(false);

            if (size is null)
            {
                contents.Add(HandleUnavailable(attachment, source, budget));
                continue;
            }

            if (!budget.TryConsumeBytes(size.Value))
            {
                contents.Add(Degrade(attachment, "over-byte-budget", budget, size.Value));
                continue;
            }

            var bytes = await _byteSource.ReadAsync(source.Location, ct).ConfigureAwait(false);
            if (bytes is null)
            {
                // The budget was reserved optimistically; give it back so the next attachment is not
                // penalised for a file that vanished between the size probe and the read.
                budget.ReleaseBytes(size.Value);
                contents.Add(HandleUnavailable(attachment, source, budget));
                continue;
            }

            budget.ClaimSource(source.Key);
            budget.RecordInlined();
            inlinedThisTurn++;
            contents.Add(new DataContent(bytes, AttachmentReferences.ResolveMediaType(attachment)));
        }

        return contents;
    }

    private AIContent HandleUnavailable(ChatAttachmentRef attachment, AttachmentSource source, AttachmentInlineBudget budget)
    {
        if (_policy.FailOnUnavailable)
            throw new AttachmentUnavailableException(attachment, source.Location);

        return Degrade(attachment, "unavailable", budget);
    }

    private static TextContent Degrade(ChatAttachmentRef attachment, string reason, AttachmentInlineBudget budget, long knownSize = 0)
    {
        budget.RecordDegraded();
        return new TextContent(Placeholder(attachment, reason, knownSize));
    }

    /// <summary>
    /// The text substituted for an attachment that was not sent.
    /// </summary>
    /// <remarks>
    /// The reason is part of the text on purpose: without it an operator debugging "why can't the model
    /// see my image" has to correlate logs to know whether the window, the per-turn limit, the byte
    /// budget or a missing file was responsible. The model also gets a truthful statement that
    /// something was withheld, rather than a silently shorter prompt.
    /// </remarks>
    internal static string Placeholder(ChatAttachmentRef attachment, string reason, long knownSize = 0)
    {
        var name = attachment.FileName ?? attachment.MimeType ?? "attachment";
        var size = knownSize > 0
            ? $", {FormatSize(knownSize)}"
            : attachment.SizeBytes > 0
                ? $", {FormatSize(attachment.SizeBytes)}"
                : "";

        // Only cache-backed attachments have an id. Printing "ref=" next to one gives an operator
        // something to grep for; inventing an id for a temp path would give them a dead end.
        var reference = string.IsNullOrEmpty(attachment.AssetId) ? "" : $", ref={attachment.AssetId}";

        return $"[attachment not sent ({reason}): {name}{size}{reference}]";
    }

    internal static string FormatSize(long bytes)
        => bytes >= 1024L * 1024
            ? $"{bytes / (1024d * 1024d):0.#} MB"
            : $"{bytes / 1024d:0.#} KB";

    /// <summary>
    /// Decides what an attachment reference actually points at.
    /// </summary>
    /// <remarks>
    /// The path/URL distinction is taken from <see cref="ChatAttachmentRef.Kind"/> first and the URI
    /// scheme second, mirroring what the marker path did: a <c>PATH</c> kind was always resolved with
    /// <see cref="Path.GetFullPath(string)"/> against the process directory, so inlining must resolve
    /// it the same way or it would read a different file than the one that was rendered before.
    /// </remarks>
    internal static AttachmentSource Classify(ChatAttachmentRef attachment)
    {
        if (attachment.Kind is MediaMarkerKind.ImagePath or MediaMarkerKind.FilePath)
        {
            try
            {
                return new AttachmentSource(AttachmentSourceKind.LocalFile, Path.GetFullPath(attachment.Value), null);
            }
            catch (ArgumentException)
            {
                return new AttachmentSource(AttachmentSourceKind.Omitted, attachment.Value, null);
            }
            catch (NotSupportedException)
            {
                return new AttachmentSource(AttachmentSourceKind.Omitted, attachment.Value, null);
            }
            catch (PathTooLongException)
            {
                return new AttachmentSource(AttachmentSourceKind.Omitted, attachment.Value, null);
            }
        }

        if (!Uri.TryCreate(attachment.Value, UriKind.Absolute, out var uri) || !uri.IsAbsoluteUri)
            return new AttachmentSource(AttachmentSourceKind.RawText, attachment.Value, null);

        if (uri.IsFile)
            return new AttachmentSource(AttachmentSourceKind.LocalFile, uri.LocalPath, null);

        // http(s) is fetched by the provider; data: is already an inline payload. Both go out as a URI,
        // as they did before.
        return new AttachmentSource(AttachmentSourceKind.Remote, attachment.Value, uri);
    }
}
