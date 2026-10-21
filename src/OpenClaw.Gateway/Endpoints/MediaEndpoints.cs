using System.Text;
using System.Text.Json.Nodes;
using OpenClaw.Core.Models;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Extensions;

namespace OpenClaw.Gateway.Endpoints;

internal static class MediaEndpoints
{
    internal static void MapOpenClawMediaEndpoints(
        this WebApplication app,
        GatewayStartupContext startup)
    {
        // The per-file ceiling lives in configuration rather than here: the same value has to hold for
        // every writer (channel uploads, provider audio, published files), and the cache refuses anything
        // larger internally. This endpoint only has to turn that refusal into a 413.
        var maxUploadBytes = startup.Config.Multimodal.MediaCache.MaxFileBytes;

        // POST /media/upload — accepts multipart/form-data with a single "file" field.
        // Returns { id, url, fileName, mimeType, sizeBytes }.
        app.MapPost("/media/upload", async (HttpContext ctx, MediaCacheStore mediaCache, CancellationToken ct) =>
        {
            if (!EndpointHelpers.IsAuthorizedRequest(ctx, startup.Config, startup.IsNonLoopbackBind))
                return Results.StatusCode(401);

            EndpointHelpers.TrySetMaxRequestBodySize(ctx, maxUploadBytes);

            if (!ctx.Request.HasFormContentType)
                return Results.BadRequest(new JsonObject { ["error"] = "multipart/form-data required" });

            IFormFile? file;
            try
            {
                var form = await ctx.Request.ReadFormAsync(ct);
                file = form.Files.GetFile("file");
            }
            catch (Exception)
            {
                return Results.BadRequest(new JsonObject { ["error"] = "Failed to read form data" });
            }

            if (file is null || file.Length == 0)
                return Results.BadRequest(new JsonObject { ["error"] = "No file provided" });

            if (file.Length > maxUploadBytes)
                return Results.StatusCode(413);

            await using var ms = new MemoryStream((int)Math.Min(file.Length, int.MaxValue));
            await file.CopyToAsync(ms, ct);
            var bytes = ms.ToArray();

            var mimeType = string.IsNullOrWhiteSpace(file.ContentType)
                ? "application/octet-stream"
                : file.ContentType;

            StoredMediaAsset asset;
            try
            {
                asset = await mediaCache.SaveAsync(bytes.AsMemory(), mimeType, file.FileName, ct);
            }
            catch (MediaAssetTooLargeException exception)
            {
                // Reachable when the multipart length header understates what was actually buffered: the
                // body limit above is enforced from the declared length, the cache refuses the real bytes.
                // Serialized by hand because this project is AOT-analyzed and Results.Json<T> is annotated
                // as requiring reflection.
                var body = new JsonObject
                {
                    ["error"] = "file too large for the media cache",
                    ["sizeBytes"] = exception.SizeBytes,
                    ["maxBytes"] = exception.MaxBytes
                };
                return Results.Text(
                    body.ToJsonString(),
                    "application/json",
                    Encoding.UTF8,
                    StatusCodes.Status413PayloadTooLarge);
            }

            return Results.Ok(new JsonObject
            {
                ["id"] = asset.Id,
                ["url"] = $"/media/{asset.Id}",
                ["fileName"] = asset.FileName,
                ["mimeType"] = asset.MediaType,
                ["sizeBytes"] = asset.SizeBytes
            });
        });

        // GET /media/{id} — serve a stored media asset by its ID.
        app.MapGet("/media/{id}", async (HttpContext ctx, string id, MediaCacheStore mediaCache, CancellationToken ct) =>
        {
            if (!EndpointHelpers.IsAuthorizedRequest(ctx, startup.Config, startup.IsNonLoopbackBind))
                return Results.StatusCode(401);

            // Reject IDs with path traversal characters.
            if (id.Contains('/') || id.Contains('\\') || id.Contains('.'))
                return Results.NotFound();

            var asset = await mediaCache.GetAsync(id, ct);
            if (asset is null || !File.Exists(asset.Path))
                return Results.NotFound();

            // Use inline disposition for media types the browser renders natively;
            // everything else (HTML, code, archives, etc.) uses attachment so the browser downloads instead.
            var isInline = asset.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                || asset.MediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
                || asset.MediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase);
            var disposition = isInline ? "inline" : "attachment";
            ctx.Response.Headers.ContentDisposition = $"{disposition}; filename=\"{Uri.EscapeDataString(asset.FileName)}\"";
            return Results.File(asset.Path, asset.MediaType, enableRangeProcessing: true);
        });

        // GET /media/cache — what the cache is allowed to hold, and what the last sweep did.
        app.MapGet("/media/cache", async (
            HttpContext ctx,
            IMediaCacheCoordinator cache,
            CancellationToken ct) =>
        {
            if (!EndpointHelpers.IsAuthorizedRequest(ctx, startup.Config, startup.IsNonLoopbackBind))
                return Results.StatusCode(401);

            var status = await cache.GetStatusAsync(ct);
            return Results.Ok(new JsonObject
            {
                ["sweepEnabled"] = status.SweepEnabled,
                ["rootPath"] = status.RootPath,
                ["limits"] = new JsonObject
                {
                    ["maxFileBytes"] = status.Limits.MaxFileBytes,
                    ["retentionDays"] = status.Limits.RetentionDays,
                    ["maxTotalBytes"] = status.Limits.MaxTotalBytes,
                    ["sweepIntervalMinutes"] = status.Limits.SweepIntervalMinutes
                },
                ["lastSweepAtUtc"] = status.LastSweepAtUtc?.ToString("O"),
                ["lastSweep"] = status.LastSweep is null ? null : DescribeSweep(status.LastSweep)
            });
        });

        // POST /media/cache/sweep[?dryRun=true] — run the retention sweep now.
        app.MapPost("/media/cache/sweep", async (
            HttpContext ctx,
            IMediaCacheCoordinator cache,
            CancellationToken ct) =>
        {
            if (!EndpointHelpers.IsAuthorizedRequest(ctx, startup.Config, startup.IsNonLoopbackBind))
                return Results.StatusCode(401);

            // Deleting cached assets is a state mutation, so it sits behind the same operator gate as the
            // other mutating surfaces rather than the plain "any authenticated caller" check that uploads
            // and reads use.
            if (!EndpointHelpers.CanExecuteAgent(ctx, startup))
            {
                await EndpointHelpers.WriteOperatorRoleRequiredAsync(ctx);
                return Results.Empty;
            }

            var dryRun = ctx.Request.Query.TryGetValue("dryRun", out var dryRunValues) &&
                bool.TryParse(dryRunValues.ToString(), out var parsed) && parsed;

            var result = await cache.SweepNowAsync(dryRun, ct);
            return Results.Ok(DescribeSweep(result));
        });
    }

    private static JsonObject DescribeSweep(MediaCacheSweepResult result) => new()
    {
        ["dryRun"] = result.DryRun,
        ["scannedAssets"] = result.ScannedAssets,
        ["totalBytesBefore"] = result.TotalBytesBefore,
        ["totalBytesAfter"] = result.TotalBytesAfter,
        ["deletedExpired"] = result.DeletedExpired,
        ["deletedOverCapacity"] = result.DeletedOverCapacity,
        ["deletedIncomplete"] = result.DeletedIncomplete,
        ["failedDeletions"] = result.FailedDeletions,
        ["freedBytes"] = result.FreedBytes
    };
}
