using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OpenClaw.Core.Memory;

internal static class MemoryRetentionArchive
{
    public static async ValueTask ArchivePayloadAsync(
        string archiveRoot,
        DateTimeOffset nowUtc,
        string kind,
        string id,
        DateTimeOffset expiresAtUtc,
        string sourceBackend,
        string payloadJson,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(archiveRoot))
            throw new ArgumentException("archiveRoot must be set.", nameof(archiveRoot));
        if (string.IsNullOrWhiteSpace(kind))
            throw new ArgumentException("kind must be set.", nameof(kind));
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("id must be set.", nameof(id));

        using var payload = JsonDocument.Parse(payloadJson);

        var now = nowUtc.UtcDateTime;
        var archiveBase = Path.GetFullPath(archiveRoot);
        var destinationDir = Path.Combine(
            archiveBase,
            now.ToString("yyyy"),
            now.ToString("MM"),
            now.ToString("dd"),
            kind);
        Directory.CreateDirectory(destinationDir);

        var fileName = $"{EncodeId(id)}-{now:yyyyMMddTHHmmssfffffffZ}.json";
        var destinationPath = Path.Combine(destinationDir, fileName);
        var tempPath = destinationPath + ".tmp";

        try
        {
            await using (var stream = new FileStream(tempPath, new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous
            }))
            {
                using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
                {
                    Indented = false
                });

                writer.WriteStartObject();
                writer.WriteString("kind", kind);
                writer.WriteString("id", id);
                writer.WriteString("sweptAtUtc", nowUtc.UtcDateTime);
                writer.WriteString("expiresAtUtc", expiresAtUtc.UtcDateTime);
                writer.WriteString("sourceBackend", sourceBackend);
                writer.WritePropertyName("payload");
                payload.RootElement.WriteTo(writer);
                writer.WriteEndObject();
                await writer.FlushAsync(ct);
                await stream.FlushAsync(ct);
            }

            File.Move(tempPath, destinationPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(tempPath); } catch { /* ignore cleanup failures */ }
            throw;
        }
    }

    public static (int DeletedFiles, int Errors, List<string> ErrorMessages) PurgeExpiredArchives(
        string archiveRoot,
        DateTimeOffset nowUtc,
        int retentionDays,
        CancellationToken ct)
    {
        var deleted = 0;
        var errors = 0;
        var errorMessages = new List<string>(capacity: 4);

        if (string.IsNullOrWhiteSpace(archiveRoot) || !Directory.Exists(archiveRoot))
            return (deleted, errors, errorMessages);

        var cutoff = nowUtc.UtcDateTime.AddDays(-Math.Max(1, retentionDays));
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(archiveRoot, "*.json", SearchOption.AllDirectories);
        }
        catch (Exception ex)
        {
            errors++;
            errorMessages.Add($"Failed to enumerate archive files: {ex.Message}");
            return (deleted, errors, errorMessages);
        }

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            var shouldDelete = false;
            try
            {
                if (TryGetArchiveSweepDayUtc(archiveRoot, file, out var archiveDayUtc))
                {
                    if (archiveDayUtc > cutoff.Date)
                        continue;
                    if (archiveDayUtc < cutoff.Date)
                        shouldDelete = true;
                }

                if (!shouldDelete)
                {
                    using var stream = File.OpenRead(file);
                    using var doc = JsonDocument.Parse(stream);
                    if (!doc.RootElement.TryGetProperty("sweptAtUtc", out var sweptAtElement) ||
                        sweptAtElement.ValueKind != JsonValueKind.String ||
                        !DateTime.TryParse(
                            sweptAtElement.GetString(),
                            provider: null,
                            System.Globalization.DateTimeStyles.RoundtripKind,
                            out var sweptAtUtc))
                    {
                        var fallbackLastWriteUtc = File.GetLastWriteTimeUtc(file);
                        if (fallbackLastWriteUtc >= cutoff)
                            continue;
                    }
                    else if (sweptAtUtc >= cutoff)
                    {
                        continue;
                    }
                }
            }
            catch (Exception ex)
            {
                errors++;
                if (errorMessages.Count < 16)
                    errorMessages.Add($"Failed to inspect archive file '{file}': {ex.Message}");
                continue;
            }

            try
            {
                File.Delete(file);
                deleted++;
            }
            catch (Exception ex)
            {
                errors++;
                if (errorMessages.Count < 16)
                    errorMessages.Add($"Failed to delete archive file '{file}': {ex.Message}");
            }
        }

        CleanupEmptyDirectories(archiveRoot);

        return (deleted, errors, errorMessages);
    }

    private static string EncodeId(string id)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(id));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool TryGetArchiveSweepDayUtc(string archiveRoot, string filePath, out DateTime archiveDayUtc)
    {
        archiveDayUtc = default;

        try
        {
            var relative = Path.GetRelativePath(archiveRoot, filePath);
            var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (segments.Length < 4)
                return false;

            if (!int.TryParse(segments[0], out var year) ||
                !int.TryParse(segments[1], out var month) ||
                !int.TryParse(segments[2], out var day))
            {
                return false;
            }

            archiveDayUtc = new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void CleanupEmptyDirectories(string archiveRoot)
    {
        foreach (var dir in Directory.EnumerateDirectories(archiveRoot, "*", SearchOption.AllDirectories)
                     .OrderByDescending(static path => path.Length))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir, recursive: false);
            }
            catch
            {
                // Best effort.
            }
        }
    }
}
