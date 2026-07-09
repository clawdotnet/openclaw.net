using System.Text.Json.Nodes;
using OpenClaw.Gateway.Bootstrap;

namespace OpenClaw.Gateway.Endpoints;

internal static class MediaEndpoints
{
    private const long MaxUploadBytes = 50 * 1024 * 1024; // 50 MB

    internal static void MapOpenClawMediaEndpoints(
        this WebApplication app,
        GatewayStartupContext startup)
    {
        // POST /media/upload — accepts multipart/form-data with a single "file" field.
        // Returns { id, url, fileName, mimeType, sizeBytes }.
        app.MapPost("/media/upload", async (HttpContext ctx, MediaCacheStore mediaCache, CancellationToken ct) =>
        {
            if (!EndpointHelpers.IsAuthorizedRequest(ctx, startup.Config, startup.IsNonLoopbackBind))
                return Results.StatusCode(401);

            EndpointHelpers.TrySetMaxRequestBodySize(ctx, MaxUploadBytes);

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

            if (file.Length > MaxUploadBytes)
                return Results.StatusCode(413);

            await using var ms = new MemoryStream((int)Math.Min(file.Length, int.MaxValue));
            await file.CopyToAsync(ms, ct);
            var bytes = ms.ToArray();

            var mimeType = string.IsNullOrWhiteSpace(file.ContentType)
                ? "application/octet-stream"
                : file.ContentType;

            var asset = await mediaCache.SaveAsync(bytes.AsMemory(), mimeType, file.FileName, ct);

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
    }
}
