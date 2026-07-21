using System.Text.Json;
using OpenClaw.Core.Models;

namespace OpenClaw.Gateway;

internal sealed class MediaCacheStore
{
    private readonly string _rootPath;

    public MediaCacheStore(string rootPath)
    {
        _rootPath = Path.GetFullPath(rootPath);
        Directory.CreateDirectory(_rootPath);
    }

    public async Task<StoredMediaAsset> SaveAsync(
        ReadOnlyMemory<byte> data,
        string mediaType,
        string fileName,
        CancellationToken ct)
    {
        var id = $"media_{Guid.NewGuid():N}"[..22];
        var extension = GuessExtension(mediaType, fileName);
        var assetPath = Path.Combine(_rootPath, id + extension);
        await File.WriteAllBytesAsync(assetPath, data.ToArray(), ct);

        var asset = new StoredMediaAsset
        {
            Id = id,
            MediaType = mediaType,
            FileName = !string.IsNullOrWhiteSpace(fileName) ? fileName : Path.GetFileName(assetPath),
            Path = assetPath,
            SizeBytes = data.Length,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

        var metadataPath = Path.Combine(_rootPath, id + ".json");
        await File.WriteAllTextAsync(metadataPath, JsonSerializer.Serialize(asset, CoreJsonContext.Default.StoredMediaAsset), ct);
        return asset;
    }

    public async Task<StoredMediaAsset?> GetAsync(string id, CancellationToken ct)
    {
        var metadataPath = Path.Combine(_rootPath, id + ".json");
        if (!File.Exists(metadataPath))
            return null;

        var json = await File.ReadAllTextAsync(metadataPath, ct);
        return JsonSerializer.Deserialize(json, CoreJsonContext.Default.StoredMediaAsset);
    }

    private static string GuessExtension(string mediaType, string fileName)
    {
        var existingExtension = Path.GetExtension(fileName);
        if (!string.IsNullOrWhiteSpace(existingExtension))
            return existingExtension;

        return mediaType switch
        {
            "audio/wav" or "audio/x-wav" => ".wav",
            "audio/mpeg" => ".mp3",
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "image/svg+xml" => ".svg",
            "application/pdf" => ".pdf",
            "application/zip" or "application/x-zip-compressed" => ".zip",
            "application/json" => ".json",
            "text/plain" => ".txt",
            "text/csv" => ".csv",
            "text/html" => ".html",
            "application/msword" => ".doc",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => ".docx",
            "application/vnd.ms-excel" => ".xls",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" => ".xlsx",
            _ => ".bin"
        };
    }
}
