using System.Text.Json;
using OpenClaw.Core.Models;

namespace OpenClaw.Gateway;

/// <summary>
/// Raised when a writer hands the cache more bytes than <see cref="MediaCacheConfig.MaxFileBytes"/>
/// allows.
/// </summary>
/// <remarks>
/// Thrown rather than silently truncated: a truncated asset is a corrupt file that looks valid, and the
/// caller keeps a reference to it. Callers degrade instead — the upload endpoint answers 413, the
/// inbound paths keep the original path and skip caching.
/// </remarks>
internal sealed class MediaAssetTooLargeException(long sizeBytes, long maxBytes)
    : Exception($"Media asset of {sizeBytes} bytes exceeds the {maxBytes}-byte media cache limit.")
{
    public long SizeBytes { get; } = sizeBytes;

    public long MaxBytes { get; } = maxBytes;
}

/// <summary>What a sweep did, or — when <see cref="DryRun"/> — what it would have done.</summary>
internal sealed record MediaCacheSweepResult(
    int ScannedAssets,
    long TotalBytesBefore,
    int DeletedExpired,
    int DeletedOverCapacity,
    int DeletedIncomplete,
    int FailedDeletions,
    long FreedBytes,
    long TotalBytesAfter,
    bool DryRun);

internal sealed class MediaCacheStore
{
    private readonly string _rootPath;
    private readonly MediaCacheConfig _config;

    public MediaCacheStore(string rootPath, MediaCacheConfig? config = null)
    {
        _rootPath = Path.GetFullPath(rootPath);
        _config = config ?? new MediaCacheConfig();
        Directory.CreateDirectory(_rootPath);
    }

    public string RootPath => _rootPath;

    public MediaCacheConfig Config => _config;

    public async Task<StoredMediaAsset> SaveAsync(
        ReadOnlyMemory<byte> data,
        string mediaType,
        string fileName,
        CancellationToken ct)
    {
        if (data.Length > _config.MaxFileBytes)
            throw new MediaAssetTooLargeException(data.Length, _config.MaxFileBytes);

        // The id shape is a contract, not an implementation detail: AttachmentReferences.TryGetMediaAssetId
        // recognises "<prefix><16 hex>" in a file name so that a ChatAttachmentRef pointing at a cached
        // file carries its AssetId. Changing the shape silently empties that field.
        var id = AttachmentReferences.MediaAssetIdPrefix + Guid.NewGuid().ToString("N")[..16];
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

    /// <summary>
    /// Removes assets that outlived <see cref="MediaCacheConfig.RetentionDays"/> and, if the directory is
    /// still over <see cref="MediaCacheConfig.MaxTotalBytes"/>, the oldest survivors until it fits.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One sweep, two rules, applied in that order: expiry is about age, capacity is about size, and
    /// applying capacity first would let a burst of fresh assets evict something that was about to expire
    /// anyway.
    /// </para>
    /// <para>
    /// The scan also removes entries that are already incomplete — a sidecar whose asset file is gone, and
    /// an asset file with no sidecar at all. Nothing can serve either one (the sidecar is what makes an id
    /// resolvable), so both are pure leak. A sidecar that cannot be parsed is skipped rather than guessed
    /// at, so a half-written file is never mistaken for an orphan and deleted.
    /// </para>
    /// <para>
    /// Entries written in the last minute are never deleted for capacity. A writer mid-save holds no lock
    /// the sweep could take, and deleting an asset between its bytes and its sidecar would turn a
    /// successful save into a dangling reference.
    /// </para>
    /// </remarks>
    public Task<MediaCacheSweepResult> SweepAsync(DateTimeOffset nowUtc, bool dryRun, CancellationToken ct)
    {
        var entries = new List<CacheEntry>();
        var referencedAssetFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unresolvedStems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var incomplete = new List<CacheEntry>();

        foreach (var metadataPath in Directory.EnumerateFiles(_rootPath, "*.json"))
        {
            ct.ThrowIfCancellationRequested();
            var entry = ReadEntry(metadataPath);
            if (entry is null)
            {
                // A sidecar that cannot be read is not evidence that its asset is an orphan. Remember the
                // stem so the asset is left alone: a half-written sidecar is normally a save in progress,
                // and treating it as "no metadata" would delete the very file it is about to describe.
                unresolvedStems.Add(Path.GetFileNameWithoutExtension(metadataPath));
                continue;
            }

            if (entry.AssetPath is null)
                incomplete.Add(entry);
            else
            {
                referencedAssetFiles.Add(entry.AssetPath);
                entries.Add(entry);
            }
        }

        // Asset files nobody claims. Creation time comes from the file system because there is no sidecar
        // to ask; an unreadable timestamp is treated as old rather than fresh, since the file is already
        // unreachable and keeping it forever is the worse failure.
        foreach (var assetPath in Directory.EnumerateFiles(_rootPath))
        {
            ct.ThrowIfCancellationRequested();
            if (assetPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                referencedAssetFiles.Contains(assetPath) ||
                unresolvedStems.Contains(Path.GetFileNameWithoutExtension(assetPath)))
            {
                continue;
            }

            var info = new FileInfo(assetPath);
            incomplete.Add(new CacheEntry(
                Path.GetFileNameWithoutExtension(assetPath),
                assetPath,
                null,
                info.Exists ? info.Length : 0,
                SafeLastWrite(info)));
        }

        var totalBefore = entries.Sum(entry => entry.SizeBytes) + incomplete.Sum(entry => entry.SizeBytes);
        var total = totalBefore;
        var freed = 0L;
        var deletedExpired = 0;
        var deletedOverCapacity = 0;
        var deletedIncomplete = 0;
        var failed = 0;

        foreach (var entry in incomplete)
        {
            if (!Delete(entry, dryRun, ref failed))
                continue;

            total -= entry.SizeBytes;
            freed += entry.SizeBytes;
            deletedIncomplete++;
        }

        var survivors = new List<CacheEntry>(entries.Count);
        // Entries a delete attempt already failed on. The capacity phase must not try them again: the file
        // has not moved, so the second attempt fails identically and only doubles the failure count.
        var undeletable = new HashSet<string>(StringComparer.Ordinal);
        if (entries.Count > 0)
        {
            var expiryCutoff = nowUtc.AddDays(-Math.Max(_config.RetentionDays, 0));
            var capacityGraceCutoff = nowUtc.AddMinutes(-1);

            foreach (var entry in entries)
            {
                // RetentionDays <= 0 disables the age rule but not the capacity rule: a deployment that
                // turns retention off still asked for a total ceiling.
                if (_config.RetentionDays <= 0 || entry.CreatedAtUtc >= expiryCutoff)
                {
                    survivors.Add(entry);
                    continue;
                }

                if (!Delete(entry, dryRun, ref failed))
                {
                    // Still on disk, so it must still count against the total below.
                    survivors.Add(entry);
                    undeletable.Add(entry.Id);
                    continue;
                }

                total -= entry.SizeBytes;
                freed += entry.SizeBytes;
                deletedExpired++;
            }

            foreach (var entry in survivors.OrderBy(entry => entry.CreatedAtUtc).ToArray())
            {
                if (total <= _config.MaxTotalBytes)
                    break;

                if (entry.CreatedAtUtc > capacityGraceCutoff)
                    continue;

                if (undeletable.Contains(entry.Id))
                    continue;

                if (!Delete(entry, dryRun, ref failed))
                    continue;

                total -= entry.SizeBytes;
                freed += entry.SizeBytes;
                deletedOverCapacity++;
            }
        }

        return Task.FromResult(Describe());

        MediaCacheSweepResult Describe() => new(
            entries.Count + incomplete.Count,
            totalBefore,
            deletedExpired,
            deletedOverCapacity,
            deletedIncomplete,
            failed,
            freed,
            dryRun ? totalBefore : totalBefore - freed,
            dryRun);
    }

    private bool Delete(CacheEntry entry, bool dryRun, ref int failed)
    {
        if (dryRun)
            return true;

        try
        {
            if (entry.AssetPath is not null && File.Exists(entry.AssetPath))
                File.Delete(entry.AssetPath);
            if (entry.MetadataPath is not null && File.Exists(entry.MetadataPath))
                File.Delete(entry.MetadataPath);
            return true;
        }
        catch (IOException)
        {
            failed++;
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            failed++;
            return false;
        }
    }

    private CacheEntry? ReadEntry(string metadataPath)
    {
        try
        {
            var json = File.ReadAllText(metadataPath);
            var asset = JsonSerializer.Deserialize(json, CoreJsonContext.Default.StoredMediaAsset);
            if (asset is null || string.IsNullOrWhiteSpace(asset.Id))
                return null;

            var assetPath = string.IsNullOrWhiteSpace(asset.Path) ? null : asset.Path;
            if (assetPath is not null && !File.Exists(assetPath))
                assetPath = null;

            var size = assetPath is null ? 0 : new FileInfo(assetPath).Length;
            return new CacheEntry(
                asset.Id,
                assetPath,
                metadataPath,
                size,
                // An unset timestamp is old, not new: treating a sidecar written before the field existed
                // as fresh would keep it forever.
                asset.CreatedAtUtc == default ? DateTimeOffset.MinValue : asset.CreatedAtUtc);
        }
        catch (JsonException)
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
    }

    private static DateTimeOffset SafeLastWrite(FileInfo info)
    {
        try
        {
            return info.Exists ? new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero) : DateTimeOffset.MinValue;
        }
        catch (IOException)
        {
            return DateTimeOffset.MinValue;
        }
        catch (UnauthorizedAccessException)
        {
            return DateTimeOffset.MinValue;
        }
    }

    private sealed record CacheEntry(
        string Id,
        string? AssetPath,
        string? MetadataPath,
        long SizeBytes,
        DateTimeOffset CreatedAtUtc);

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
