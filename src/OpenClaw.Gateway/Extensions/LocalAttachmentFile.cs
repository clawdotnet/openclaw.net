namespace OpenClaw.Gateway.Extensions;

/// <summary>
/// The one place in the Gateway that opens an attachment file.
/// </summary>
/// <remarks>
/// <para>
/// Two callers need the bytes and neither is the right home for the read: <c>MediaPayloadAdapter</c>
/// formats them for a provider, and <c>InboundAttachmentPersister</c> copies them into durable storage.
/// Duplicating the read would also duplicate its failure modes — a missing file, a permission error and
/// a locked file all have to degrade rather than throw, and that judgement is easier to keep consistent
/// in one method than in two.
/// </para>
/// <para>
/// Returning a bool instead of a nullable keeps the caller's shape simple: "not readable" is the common
/// case on the legacy path, not an exceptional one.
/// </para>
/// <para>
/// Reads are bounded. <see cref="MaxReadBytes"/> is a guard against a pathological path, not a policy: the
/// cache decides what it will store (<c>MediaCacheConfig.MaxFileBytes</c>) and the inliner decides what it
/// will send (<c>AttachmentConfig.MaxInlineBytesPerRequest</c>), but neither of them runs before this
/// method does, so without a ceiling here a stray multi-gigabyte path is read into memory in full before
/// anything gets a chance to refuse it. The limit is deliberately generous — it should never be the thing
/// that rejects a legitimate attachment.
/// </para>
/// </remarks>
internal static class LocalAttachmentFile
{
    /// <summary>
    /// Ceiling for a single read. Chosen to be far above any attachment this product carries, because its
    /// job is to stop an unbounded read rather than to express a preference.
    /// </summary>
    internal const long MaxReadBytes = 256L * 1024 * 1024;

    /// <summary>Reads the file at <paramref name="path"/>, or returns false if it cannot be read.</summary>
    public static bool TryRead(string? path, out byte[] bytes) => TryRead(path, MaxReadBytes, out bytes);

    /// <summary>
    /// Reads the file at <paramref name="path"/>, or returns false if it cannot be read or is larger than
    /// <paramref name="maxBytes"/>.
    /// </summary>
    public static bool TryRead(string? path, long maxBytes, out byte[] bytes)
    {
        bytes = [];

        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            if (!File.Exists(path))
                return false;

            // Checked before the read, because the read is the thing being bounded. Checked again after,
            // because the file can grow between the two statements and the first check would then have
            // only proved that it used to fit.
            if (new FileInfo(path).Length > maxBytes)
                return false;

            var read = File.ReadAllBytes(path);
            if (read.LongLength > maxBytes)
                return false;

            bytes = read;
            return true;
        }
        catch (IOException)
        {
            bytes = [];
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            bytes = [];
            return false;
        }
        catch (ArgumentException)
        {
            bytes = [];
            return false;
        }
        catch (NotSupportedException)
        {
            bytes = [];
            return false;
        }
    }

    /// <summary>Reads the file behind a <c>file:</c> URI; false for any other scheme.</summary>
    public static bool TryRead(Uri? uri, out byte[] bytes)
    {
        bytes = [];
        if (uri is null || !uri.IsFile)
            return false;

        return TryRead(uri.LocalPath, out bytes);
    }
}
