using OpenClaw.Core.Models;
using Xunit;

namespace OpenClaw.Tests;

/// <summary>
/// <see cref="MediaMarkerKind"/> is persisted as an ordinal inside session history, so its numbers are
/// part of the storage format. Nothing else in the codebase would notice a change to them: the marker
/// text protocol uses names, the reference path uses the enum, and both keep working after a renumber.
/// Only an already-written session would be misread — as a different media class, silently, on the next
/// load. These assertions are the thing that notices instead.
/// </summary>
/// <remarks>
/// The failure mode this guards is narrow and easy to reintroduce, and it comes in two shapes now that
/// the values are explicit. Renumbering — "tidying" the list to be contiguous after a kind is retired —
/// moves a number that old sessions still carry. Inserting a kind mid-list without a value does not
/// shift its neighbours; it aliases whichever kind already owns <c>previous + 1</c>, which makes two
/// kinds interchangeable. Pinning the numbers catches the first; distinctness catches the second.
/// When adding a kind, append it with the next free number.
/// </remarks>
public sealed class MediaMarkerKindPersistenceTests
{
    [Fact]
    public void Every_marker_kind_keeps_the_ordinal_it_was_persisted_with()
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
}
