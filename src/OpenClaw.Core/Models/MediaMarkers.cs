namespace OpenClaw.Core.Models;

public enum MediaMarkerKind : byte
{
    ImageUrl,
    ImagePath,
    FileUrl,
    FilePath,
    TelegramImageFileId
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

        // Telegram inbound marker: [IMAGE:telegram:file_id=<id>]
        if (line.StartsWith("[IMAGE:telegram:file_id=", StringComparison.Ordinal) && line.EndsWith(']'))
        {
            var start = "[IMAGE:telegram:file_id=".Length;
            var value = line.Substring(start, line.Length - start - 1).Trim();
            if (!string.IsNullOrWhiteSpace(value))
            {
                marker = new MediaMarker(MediaMarkerKind.TelegramImageFileId, value);
                return true;
            }
        }

        return false;
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
}

