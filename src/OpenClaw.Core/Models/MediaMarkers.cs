using System.Text.Json.Serialization;

namespace OpenClaw.Core.Models;

/// <summary>
/// Marker kinds. Both the names and the numbers are persisted — see the remarks.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ChatAttachmentRef.Kind"/> goes into session history as its <em>name</em>
/// (<c>"kind": "ImagePath"</c>), because this converter is applied to the type. History written before
/// the converter existed carries the ordinal instead (<c>"kind": 1</c>), and
/// <see cref="JsonStringEnumConverter{TEnum}"/> still reads that form, so both load.
/// </para>
/// <para>
/// That makes <b>two</b> on-disk contracts, and they fail differently. The numbers only matter for
/// reading old sessions, but nothing would notice a change to them: the marker text protocol uses
/// names, the reference path uses the enum, and both keep working after a renumber — only an
/// already-written session would be misread, as the wrong media class, silently, on the next load. The
/// names now matter for every session written from here on, and renaming a member is the quiet way to
/// break them, since the compiler only sees the identifier.
/// </para>
/// <para>
/// So: append new kinds, never renumber, never reuse a retired number, never rename an existing member.
/// <c>MediaMarkerKindPersistenceTests</c> pins all of it. Note what an explicit value does to the usual
/// mistake: a mid-list insertion written without a value does not shift its neighbours, it becomes an
/// <em>alias</em> of whichever kind already owns <c>previous + 1</c>. That is still wrong — the two
/// kinds become interchangeable — so the tests reject duplicate numbers, and a <c>switch</c> over the
/// kinds usually rejects the alias at compile time as an unreachable arm.
/// </para>
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<MediaMarkerKind>))]
public enum MediaMarkerKind : byte
{
    ImageUrl = 0,
    ImagePath = 1,
    FileUrl = 2,
    FilePath = 3,
    TelegramImageFileId = 4,
    TelegramVideoFileId = 5,
    TelegramAudioFileId = 6,
    TelegramDocumentFileId = 7,
    TelegramStickerFileId = 8,
    VideoUrl = 9,
    AudioUrl = 10,
    DocumentUrl = 11,
    StickerUrl = 12
}

public sealed record MediaMarker(MediaMarkerKind Kind, string Value);

public static class MediaMarkerProtocol
{
    public static (List<MediaMarker> Markers, string RemainingText) Extract(string text)
    {
        if (string.IsNullOrEmpty(text))
            return ([], "");

        var markers = new List<MediaMarker>();
        var remainingLines = new List<string>();

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (TryParseMarker(trimmed, out var marker))
            {
                markers.Add(marker);
                continue;
            }

            remainingLines.Add(line);
        }

        var remaining = string.Join("\n", remainingLines).Trim();
        return (markers, remaining);
    }

    public static bool TryParseMarker(string line, out MediaMarker marker)
    {
        marker = default!;
        if (string.IsNullOrWhiteSpace(line))
            return false;

        if (TryParseBracketValue(line, "IMAGE_URL:", out var imageUrl))
        {
            marker = new MediaMarker(MediaMarkerKind.ImageUrl, imageUrl);
            return true;
        }

        if (TryParseBracketValue(line, "IMAGE_PATH:", out var imagePath))
        {
            marker = new MediaMarker(MediaMarkerKind.ImagePath, imagePath);
            return true;
        }

        if (TryParseBracketValue(line, "FILE_URL:", out var fileUrl))
        {
            marker = new MediaMarker(MediaMarkerKind.FileUrl, fileUrl);
            return true;
        }

        if (TryParseBracketValue(line, "FILE_PATH:", out var filePath))
        {
            marker = new MediaMarker(MediaMarkerKind.FilePath, filePath);
            return true;
        }

        if (TryParseBracketValue(line, "VIDEO_URL:", out var videoUrl))
        {
            marker = new MediaMarker(MediaMarkerKind.VideoUrl, videoUrl);
            return true;
        }

        if (TryParseBracketValue(line, "AUDIO_URL:", out var audioUrl))
        {
            marker = new MediaMarker(MediaMarkerKind.AudioUrl, audioUrl);
            return true;
        }

        if (TryParseBracketValue(line, "DOCUMENT_URL:", out var documentUrl))
        {
            marker = new MediaMarker(MediaMarkerKind.DocumentUrl, documentUrl);
            return true;
        }

        if (TryParseBracketValue(line, "STICKER_URL:", out var stickerUrl))
        {
            marker = new MediaMarker(MediaMarkerKind.StickerUrl, stickerUrl);
            return true;
        }

        if (TryParseTelegramFileId(line, "IMAGE", out var imageFileId))
        {
            marker = new MediaMarker(MediaMarkerKind.TelegramImageFileId, imageFileId);
            return true;
        }

        if (TryParseTelegramFileId(line, "VIDEO", out var videoFileId))
        {
            marker = new MediaMarker(MediaMarkerKind.TelegramVideoFileId, videoFileId);
            return true;
        }

        if (TryParseTelegramFileId(line, "AUDIO", out var audioFileId))
        {
            marker = new MediaMarker(MediaMarkerKind.TelegramAudioFileId, audioFileId);
            return true;
        }

        if (TryParseTelegramFileId(line, "DOCUMENT", out var documentFileId))
        {
            marker = new MediaMarker(MediaMarkerKind.TelegramDocumentFileId, documentFileId);
            return true;
        }

        if (TryParseTelegramFileId(line, "STICKER", out var stickerFileId))
        {
            marker = new MediaMarker(MediaMarkerKind.TelegramStickerFileId, stickerFileId);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Renders a marker back to its wire form — the exact inverse of <see cref="TryParseMarker"/>.
    /// </summary>
    /// <remarks>
    /// Used by the attachment-reference path, which stores markers structurally and has to rebuild
    /// the original line for consumers that can only send text. Values parsed out of a line can never
    /// contain <c>]</c>, so the round trip is lossless; a hand-constructed marker whose value does
    /// contain one would produce a line that no longer parses, which is why nothing outside parsed
    /// content should call this.
    /// </remarks>
    public static string Format(MediaMarker marker)
    {
        ArgumentNullException.ThrowIfNull(marker);

        return marker.Kind switch
        {
            MediaMarkerKind.ImageUrl => $"[IMAGE_URL:{marker.Value}]",
            MediaMarkerKind.ImagePath => $"[IMAGE_PATH:{marker.Value}]",
            MediaMarkerKind.FileUrl => $"[FILE_URL:{marker.Value}]",
            MediaMarkerKind.FilePath => $"[FILE_PATH:{marker.Value}]",
            MediaMarkerKind.VideoUrl => $"[VIDEO_URL:{marker.Value}]",
            MediaMarkerKind.AudioUrl => $"[AUDIO_URL:{marker.Value}]",
            MediaMarkerKind.DocumentUrl => $"[DOCUMENT_URL:{marker.Value}]",
            MediaMarkerKind.StickerUrl => $"[STICKER_URL:{marker.Value}]",
            MediaMarkerKind.TelegramImageFileId => $"[IMAGE:telegram:file_id={marker.Value}]",
            MediaMarkerKind.TelegramVideoFileId => $"[VIDEO:telegram:file_id={marker.Value}]",
            MediaMarkerKind.TelegramAudioFileId => $"[AUDIO:telegram:file_id={marker.Value}]",
            MediaMarkerKind.TelegramDocumentFileId => $"[DOCUMENT:telegram:file_id={marker.Value}]",
            MediaMarkerKind.TelegramStickerFileId => $"[STICKER:telegram:file_id={marker.Value}]",
            _ => marker.Value
        };
    }

    private static bool TryParseBracketValue(string line, string prefix, out string value)
    {
        value = "";
        if (line.Length < prefix.Length + 2)
            return false;

        if (!line.StartsWith('[') || !line.EndsWith(']'))
            return false;

        var inner = line[1..^1];
        if (!inner.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        value = inner[prefix.Length..].Trim();
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryParseTelegramFileId(string line, string mediaType, out string value)
    {
        value = "";
        var prefix = $"[{mediaType}:telegram:file_id=";
        if (!line.StartsWith(prefix, StringComparison.Ordinal) || !line.EndsWith(']'))
            return false;

        value = line.Substring(prefix.Length, line.Length - prefix.Length - 1).Trim();
        return !string.IsNullOrWhiteSpace(value);
    }
}
