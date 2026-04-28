using System.Text;
using System.Text.Json;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Canvas;
using OpenClaw.Core.Models;

namespace OpenClaw.Gateway.Tools;

internal abstract class CanvasToolBase : IToolWithContext
{
    protected CanvasToolBase(CanvasCommandBroker broker, GatewayConfig config)
    {
        Broker = broker;
        Config = config;
    }

    protected CanvasCommandBroker Broker { get; }
    protected GatewayConfig Config { get; }

    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract string ParameterSchema { get; }

    public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
        => new("Error: Canvas tools require session context.");

    public abstract ValueTask<string> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken ct);

    protected async ValueTask<string> SendAsync(
        string argumentsJson,
        ToolExecutionContext context,
        WsServerEnvelope command,
        string expectedResponseType,
        string? requiredCapability,
        CancellationToken ct)
    {
        var result = await Broker.SendCommandAsync(
            context.Session,
            command,
            expectedResponseType,
            requiredCapability,
            ct);

        if (!result.Success)
            return $"Error: {result.Error}";

        if (!string.IsNullOrWhiteSpace(result.SnapshotJson))
            return result.SnapshotJson!;
        if (!string.IsNullOrWhiteSpace(result.ValueJson))
            return result.ValueJson!;

        return $"Canvas command accepted. requestId={result.RequestId}";
    }

    protected static JsonDocument ParseArgs(string argumentsJson)
        => JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);

    protected static string SurfaceId(JsonElement root)
        => TryGetString(root, "surfaceId") ?? "main";

    protected static string? TryGetString(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    protected static bool TryGetRequiredString(JsonElement root, string propertyName, out string value, out string error)
    {
        if (TryGetString(root, propertyName) is { Length: > 0 } found)
        {
            value = found;
            error = "";
            return true;
        }

        value = "";
        error = $"Error: '{propertyName}' is required.";
        return false;
    }
}

internal sealed class CanvasPresentTool : CanvasToolBase
{
    public CanvasPresentTool(CanvasCommandBroker broker, GatewayConfig config) : base(broker, config) { }
    public override string Name => "canvas_present";
    public override string Description => "Show the current session's Canvas visual workspace.";
    public override string ParameterSchema => """{"type":"object","properties":{"surfaceId":{"type":"string","default":"main"}}}""";

    public override async ValueTask<string> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken ct)
    {
        using var args = ParseArgs(argumentsJson);
        return await SendAsync(argumentsJson, context, new WsServerEnvelope
        {
            Type = "canvas_present",
            SurfaceId = SurfaceId(args.RootElement)
        }, "canvas_ack", "canvas.present", ct);
    }
}

internal sealed class CanvasHideTool : CanvasToolBase
{
    public CanvasHideTool(CanvasCommandBroker broker, GatewayConfig config) : base(broker, config) { }
    public override string Name => "canvas_hide";
    public override string Description => "Hide the current session's Canvas visual workspace.";
    public override string ParameterSchema => """{"type":"object","properties":{"surfaceId":{"type":"string","default":"main"}}}""";

    public override async ValueTask<string> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken ct)
    {
        using var args = ParseArgs(argumentsJson);
        return await SendAsync(argumentsJson, context, new WsServerEnvelope
        {
            Type = "canvas_hide",
            SurfaceId = SurfaceId(args.RootElement)
        }, "canvas_ack", "canvas.hide", ct);
    }
}

internal sealed class CanvasNavigateTool : CanvasToolBase
{
    public CanvasNavigateTool(CanvasCommandBroker broker, GatewayConfig config) : base(broker, config) { }
    public override string Name => "canvas_navigate";
    public override string Description => "Load local Canvas HTML, about:blank, or an openclaw-canvas:// artifact into the session Canvas. Remote HTTP/HTTPS pages are not supported by Canvas v1.";
    public override string ParameterSchema => """
        {"type":"object","properties":{"surfaceId":{"type":"string","default":"main"},"html":{"type":"string"},"url":{"type":"string"},"contentType":{"type":"string","default":"text/html"}}}
        """;

    public override async ValueTask<string> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken ct)
    {
        using var args = ParseArgs(argumentsJson);
        var root = args.RootElement;
        var html = TryGetString(root, "html");
        var url = TryGetString(root, "url");
        if (string.IsNullOrWhiteSpace(html) && string.IsNullOrWhiteSpace(url))
            return "Error: 'html' or 'url' is required.";
        if (!string.IsNullOrWhiteSpace(html) && !Config.Canvas.EnableLocalHtml)
            return "Error: local Canvas HTML is disabled.";
        if (!string.IsNullOrWhiteSpace(url) && !IsAllowedCanvasUrl(url!))
            return "Error: Canvas v1 only supports about:blank and openclaw-canvas:// URLs; use the browser tool for remote webpages.";

        return await SendAsync(argumentsJson, context, new WsServerEnvelope
        {
            Type = "canvas_navigate",
            SurfaceId = SurfaceId(root),
            Html = html,
            Url = url,
            ContentType = TryGetString(root, "contentType") ?? "text/html"
        }, "canvas_ack", string.IsNullOrWhiteSpace(html) ? "canvas.present" : "canvas.local_html", ct);
    }

    private static bool IsAllowedCanvasUrl(string url)
        => string.Equals(url, "about:blank", StringComparison.OrdinalIgnoreCase) ||
           url.StartsWith("openclaw-canvas://", StringComparison.OrdinalIgnoreCase);
}

internal sealed class CanvasSnapshotTool : CanvasToolBase
{
    public CanvasSnapshotTool(CanvasCommandBroker broker, GatewayConfig config) : base(broker, config) { }
    public override string Name => "canvas_snapshot";
    public override string Description => "Capture a lightweight JSON state snapshot of the current session Canvas.";
    public override string ParameterSchema => """{"type":"object","properties":{"surfaceId":{"type":"string","default":"main"},"snapshotMode":{"type":"string","default":"state"}}}""";

    public override async ValueTask<string> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken ct)
    {
        using var args = ParseArgs(argumentsJson);
        return await SendAsync(argumentsJson, context, new WsServerEnvelope
        {
            Type = "canvas_snapshot",
            SurfaceId = SurfaceId(args.RootElement),
            SnapshotMode = TryGetString(args.RootElement, "snapshotMode") ?? "state"
        }, "canvas_snapshot_result", "snapshot.state", ct);
    }
}

internal sealed class A2UiPushTool : CanvasToolBase
{
    public A2UiPushTool(CanvasCommandBroker broker, GatewayConfig config) : base(broker, config) { }
    public override string Name => "a2ui_push";
    public override string Description => "Push A2UI v0.8 JSONL frames to the current session Canvas.";
    public override string ParameterSchema => """
        {"type":"object","properties":{"surfaceId":{"type":"string","default":"main"},"frames":{"type":"string","description":"A2UI v0.8 JSONL frames"}},"required":["frames"]}
        """;

    public override async ValueTask<string> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken ct)
    {
        using var args = ParseArgs(argumentsJson);
        if (!TryGetRequiredString(args.RootElement, "frames", out var frames, out var error))
            return error;

        var validation = A2UiFrameValidator.ValidateJsonl(frames, Config.Canvas.MaxFramesPerPush, Config.Canvas.MaxCommandBytes);
        if (!validation.IsValid)
            return $"Error: {validation.Error}";

        return await SendAsync(argumentsJson, context, new WsServerEnvelope
        {
            Type = "a2ui_push",
            SurfaceId = SurfaceId(args.RootElement),
            ContentType = A2UiFrameValidator.ContentTypeV08,
            Frames = frames
        }, "canvas_ack", "a2ui.v0_8", ct);
    }
}

internal sealed class A2UiResetTool : CanvasToolBase
{
    public A2UiResetTool(CanvasCommandBroker broker, GatewayConfig config) : base(broker, config) { }
    public override string Name => "a2ui_reset";
    public override string Description => "Clear A2UI-rendered content from the current session Canvas.";
    public override string ParameterSchema => """{"type":"object","properties":{"surfaceId":{"type":"string","default":"main"}}}""";

    public override async ValueTask<string> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken ct)
    {
        using var args = ParseArgs(argumentsJson);
        return await SendAsync(argumentsJson, context, new WsServerEnvelope
        {
            Type = "a2ui_reset",
            SurfaceId = SurfaceId(args.RootElement)
        }, "canvas_ack", "a2ui.v0_8", ct);
    }
}

internal sealed class A2UiEvalTool : CanvasToolBase
{
    public A2UiEvalTool(CanvasCommandBroker broker, GatewayConfig config) : base(broker, config) { }
    public override string Name => "a2ui_eval";
    public override string Description => "Run JavaScript in the local A2UI Canvas sandbox. This does not evaluate scripts on remote webpages.";
    public override string ParameterSchema => """
        {"type":"object","properties":{"surfaceId":{"type":"string","default":"main"},"script":{"type":"string"}},"required":["script"]}
        """;

    public override async ValueTask<string> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken ct)
    {
        if (!Config.Canvas.EnableEval)
            return "Error: A2UI eval is disabled.";

        using var args = ParseArgs(argumentsJson);
        if (!TryGetRequiredString(args.RootElement, "script", out var script, out var error))
            return error;

        if (Encoding.UTF8.GetByteCount(script) > Math.Max(1, Config.Canvas.MaxCommandBytes))
            return $"Error: script exceeds {Config.Canvas.MaxCommandBytes} bytes.";

        return await SendAsync(argumentsJson, context, new WsServerEnvelope
        {
            Type = "a2ui_eval",
            SurfaceId = SurfaceId(args.RootElement),
            Script = script
        }, "canvas_eval_result", "a2ui.eval", ct);
    }
}
