using System.Text.Json;
using OpenClaw.Core.Models;
using Xunit;

namespace OpenClaw.Tests;

/// <summary>
/// <see cref="MediaMarkerKind"/> is persisted inside session history, so both its names and its numbers
/// are part of the storage format. The names are what every session carries now; the numbers are what
/// sessions written before the string-enum converter still carry, and the converter reads both. Nothing
/// else in the codebase would notice a change to either: the marker text protocol uses names, the
/// reference path uses the enum, and both keep working after a renumber or a rename. Only an
/// already-written session would be misread — as a different media class, silently, on the next load.
/// These assertions are the thing that notices instead.
/// </summary>
/// <remarks>
/// <para>
/// Each contract fails in its own way. <b>Numbers</b>: renumbering — "tidying" the list to be
/// contiguous after a kind is retired — moves a number old sessions still carry; inserting a kind
/// mid-list without a value does not shift its neighbours but aliases whichever kind already owns
/// <c>previous + 1</c>, making two kinds interchangeable. Pinning catches the first, distinctness the
/// second. <b>Names</b>: renaming a member changes the stored value for every future write while the
/// compiler stays silent, because it only ever sees the identifier. Pinning the name list catches that,
/// and <see cref="History_written_before_the_switch_still_reads_its_ordinal"/> keeps the old form
/// honest so the two do not quietly diverge.
/// </para>
/// <para>
/// When adding a kind: append it with the next free number and give it a name you are willing to keep
/// forever.
/// </para>
/// </remarks>
public sealed class MediaMarkerKindPersistenceTests
{
    [Fact]
    public void Every_marker_kind_keeps_the_ordinal_older_history_carries()
    {
        Assert.Equal(0, (int)MediaMarkerKind.ImageUrl);
        Assert.Equal(1, (int)MediaMarkerKind.ImagePath);
        Assert.Equal(2, (int)MediaMarkerKind.FileUrl);
        Assert.Equal(3, (int)MediaMarkerKind.FilePath);
        Assert.Equal(4, (int)MediaMarkerKind.TelegramImageFileId);
        Assert.Equal(5, (int)MediaMarkerKind.TelegramVideoFileId);
        Assert.Equal(6, (int)MediaMarkerKind.TelegramAudioFileId);
        Assert.Equal(7, (int)MediaMarkerKind.TelegramDocumentFileId);
        Assert.Equal(8, (int)MediaMarkerKind.TelegramStickerFileId);
        Assert.Equal(9, (int)MediaMarkerKind.VideoUrl);
        Assert.Equal(10, (int)MediaMarkerKind.AudioUrl);
        Assert.Equal(11, (int)MediaMarkerKind.DocumentUrl);
        Assert.Equal(12, (int)MediaMarkerKind.StickerUrl);
    }

    [Fact]
    public void No_two_kinds_share_a_number()
    {
        // Explicit values defend the stored sessions, but they introduce a new way to get it wrong: a
        // member inserted without a value takes "previous + 1" and quietly aliases whichever kind
        // already owns that number. C# permits the duplicate, no stored value changes, and the two
        // kinds become interchangeable — the media class then depends on which name the compiler
        // happened to resolve. Distinctness is the check that notices.
        var values = Enum.GetValues<MediaMarkerKind>().Select(kind => (int)kind).ToArray();

        Assert.Equal(values.Length, values.Distinct().Count());
    }

    [Fact]
    public void Every_marker_kind_keeps_its_name()
    {
        // A rename is a storage-format change that the compiler cannot see. Pinning the whole list also
        // pins the order, which is the declaration order and therefore the ordinal assignment.
        Assert.Equal(
            [
                "ImageUrl", "ImagePath", "FileUrl", "FilePath",
                "TelegramImageFileId", "TelegramVideoFileId", "TelegramAudioFileId",
                "TelegramDocumentFileId", "TelegramStickerFileId",
                "VideoUrl", "AudioUrl", "DocumentUrl", "StickerUrl"
            ],
            Enum.GetNames<MediaMarkerKind>());
    }

    [Fact]
    public void A_persisted_reference_names_its_kind()
    {
        var json = Serialize(CreateTurn());

        // The whole point of the converter: an operator reading a session file, or an out-of-process
        // consumer, sees the media class instead of a number whose meaning lives in a mapping table.
        Assert.Contains("\"kind\":\"ImagePath\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"kind\":1", json, StringComparison.Ordinal);
    }

    [Fact]
    public void History_written_before_the_switch_still_reads_its_ordinal()
    {
        // Simulates a session file from a build that predated the converter: same document, ordinal
        // instead of the name. If this ever stops loading, upgrading silently orphans every existing
        // session — which is the one thing the format change must not do.
        var legacy = Serialize(CreateTurn()).Replace("\"kind\":\"ImagePath\"", "\"kind\":1", StringComparison.Ordinal);

        var turn = Assert.Single(Deserialize(legacy));
        var attachment = Assert.Single(turn.Attachments!);
        Assert.Equal(MediaMarkerKind.ImagePath, attachment.Kind);
        Assert.Equal("/var/openclaw/media-cache/media_0123456789abcdef.png", attachment.Value);
    }

    [Fact]
    public void A_persisted_reference_round_trips()
    {
        var turn = Assert.Single(Deserialize(Serialize(CreateTurn())));

        var attachment = Assert.Single(turn.Attachments!);
        Assert.Equal(MediaMarkerKind.ImagePath, attachment.Kind);
        // The reference carries the media *class* derived from the kind, not a resolved mime type:
        // resolving a concrete type needs a file name, and writing a reference must not touch disk.
        Assert.Equal("image/*", attachment.MimeType);
    }

    private static ChatTurn CreateTurn()
        => AttachmentReferences.CreateUserTurn(
            "look\n[IMAGE_PATH:/var/openclaw/media-cache/media_0123456789abcdef.png]");

    private static string Serialize(ChatTurn turn)
        => JsonSerializer.Serialize(new List<ChatTurn> { turn }, CoreJsonContext.Default.ListChatTurn);

    private static List<ChatTurn> Deserialize(string json)
        => JsonSerializer.Deserialize(json, CoreJsonContext.Default.ListChatTurn)
           ?? throw new InvalidOperationException("Deserialization returned null.");
}
