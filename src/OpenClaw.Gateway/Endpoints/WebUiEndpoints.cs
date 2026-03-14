using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Composition;

namespace OpenClaw.Gateway.Endpoints;

internal static class WebUiEndpoints
{
    public static void MapOpenClawWebUiEndpoints(
        this WebApplication app,
        GatewayStartupContext startup,
        GatewayAppRuntime runtime)
    {
        app.MapGet("/admin", async (HttpContext ctx) =>
        {
            var htmlPath = Path.Combine(AppContext.BaseDirectory, "wwwroot", "admin.html");
            if (File.Exists(htmlPath))
            {
                ctx.Response.ContentType = "text/html";
                await ctx.Response.SendFileAsync(htmlPath);
                return;
            }

            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        });

        app.MapGet("/chat", async (HttpContext ctx) =>
        {
            var htmlPath = Path.Combine(AppContext.BaseDirectory, "wwwroot", "webchat.html");
            if (File.Exists(htmlPath))
            {
                ctx.Response.ContentType = "text/html";
                await ctx.Response.SendFileAsync(htmlPath);
                return;
            }

            ctx.Response.ContentType = "text/html";
            await ctx.Response.WriteAsync("""
                <!DOCTYPE html>
                <html lang="en"><head><meta charset="utf-8"><title>OpenClaw.NET</title>
                <style>body{font-family:system-ui,sans-serif;display:flex;align-items:center;justify-content:center;height:100vh;margin:0;background:#0f172a;color:#e2e8f0;}
                .card{text-align:center;max-width:420px;padding:2rem;border:1px solid #334155;border-radius:12px;background:#1e293b;}
                code{background:#334155;padding:2px 6px;border-radius:4px;font-size:0.9em;}
                a{color:#38bdf8;}</style></head>
                <body><div class="card">
                <h1>&#128062; OpenClaw.NET Gateway</h1>
                <p>The WebChat UI is not bundled. Connect via WebSocket at <code>ws://HOST:PORT/ws</code> or use the <a href="https://github.com/openclaw/openclaw.net">Companion app</a>.</p>
                </div></body></html>
                """);
        });
    }
}
