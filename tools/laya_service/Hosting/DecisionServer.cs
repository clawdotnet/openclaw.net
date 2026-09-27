using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenClaw.LayaService.Protocol;

namespace OpenClaw.LayaService.Hosting;

public static class DecisionServer
{
    private const string DecisionsPath = "/v1/decisions";
    private const string HealthPath = "/health";

    public static WebApplication Build(IDecisionPredictor predictor, ServiceOptions options)
    {
        ArgumentNullException.ThrowIfNull(predictor);
        ArgumentNullException.ThrowIfNull(options);
        if (options.Port is < 0 or > 65535 || options.MaxConnections is < 1 or > 128 ||
            options.MaxRequestBytes is < 1 or > 65536 || options.RequestTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [],
            EnvironmentName = Environments.Production
        });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server =>
        {
            server.Limits.MaxConcurrentConnections = options.MaxConnections;
            server.Limits.RequestHeadersTimeout = options.RequestTimeout;
            server.Limits.KeepAliveTimeout = options.RequestTimeout;
            server.Limits.MaxRequestBodySize = null;
            server.Listen(IPAddress.Loopback, options.Port);
        });

        var app = builder.Build();
        var inferenceGate = new SemaphoreSlim(1, 1);
        app.Run(context => HandleAsync(context, predictor, options, inferenceGate));
        return app;
    }

    private static async Task HandleAsync(
        HttpContext context,
        IDecisionPredictor predictor,
        ServiceOptions options,
        SemaphoreSlim inferenceGate)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!IsLocalRequest(context))
        {
            await WriteErrorAsync(context, StatusCodes.Status403Forbidden, "local_clients_only");
            return;
        }

        if (HttpMethods.IsGet(context.Request.Method) && context.Request.Path == HealthPath)
        {
            try
            {
                await WriteJsonAsync(context, StatusCodes.Status200OK, predictor.GetHealth(), context.RequestAborted);
            }
            catch
            {
                await WriteErrorAsync(context, StatusCodes.Status503ServiceUnavailable, "health_unavailable");
            }
            return;
        }

        if (!HttpMethods.IsPost(context.Request.Method) || context.Request.Path != DecisionsPath)
        {
            await WriteErrorAsync(context, StatusCodes.Status404NotFound, "not_found");
            return;
        }

        if (context.Request.Headers.ContainsKey("Transfer-Encoding") ||
            !string.Equals(context.Request.ContentType?.Split(';', 2)[0].Trim(), "application/json", StringComparison.OrdinalIgnoreCase) ||
            context.Request.ContentLength is null)
        {
            await WriteErrorAsync(context, StatusCodes.Status415UnsupportedMediaType, "json_content_length_required");
            return;
        }

        var contentLength = context.Request.ContentLength.Value;
        if (contentLength <= 0 || contentLength > options.MaxRequestBytes)
        {
            await WriteErrorAsync(context, StatusCodes.Status413PayloadTooLarge, "request_size");
            return;
        }

        if (!inferenceGate.Wait(0))
        {
            await WriteErrorAsync(context, StatusCodes.Status503ServiceUnavailable, "busy");
            return;
        }

        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            deadline.CancelAfter(options.RequestTimeout);
            var body = new byte[(int)contentLength];
            await context.Request.Body.ReadExactlyAsync(body.AsMemory(), deadline.Token);

            DecisionWireRequest request;
            try
            {
                request = StrictJson.ParseRequest(body);
                RequestValidator.Validate(request, predictor.Model);
            }
            catch (ProtocolRejectionException exception)
            {
                await WriteErrorAsync(context, StatusCodes.Status422UnprocessableEntity, exception.ReasonCode);
                return;
            }

            try
            {
                var result = await predictor.PredictAsync(request, deadline.Token);
                await WriteJsonAsync(context, StatusCodes.Status200OK, result, deadline.Token);
            }
            catch (ProtocolRejectionException exception)
            {
                await WriteErrorAsync(context, StatusCodes.Status422UnprocessableEntity, exception.ReasonCode);
            }
            catch
            {
                await WriteErrorAsync(context, StatusCodes.Status503ServiceUnavailable, "inference_failed");
            }
        }
        catch (OperationCanceledException)
        {
            if (!context.Response.HasStarted)
            {
                await WriteErrorAsync(context, StatusCodes.Status503ServiceUnavailable, "inference_failed");
            }
        }
        catch
        {
            if (!context.Response.HasStarted)
            {
                await WriteErrorAsync(context, StatusCodes.Status503ServiceUnavailable, "inference_failed");
            }
        }
        finally
        {
            inferenceGate.Release();
        }
    }

    private static bool IsLocalRequest(HttpContext context)
    {
        if (context.Request.Headers.ContainsKey("Origin")) return false;
        var expectedHost = $"127.0.0.1:{context.Connection.LocalPort}";
        return string.Equals(context.Request.Host.Value, expectedHost, StringComparison.Ordinal);
    }

    private static async Task WriteErrorAsync(HttpContext context, int statusCode, string reasonCode)
    {
        var body = Encoding.UTF8.GetBytes("{\"error\":\"" + reasonCode + "\"}");
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength = body.Length;
        await context.Response.Body.WriteAsync(body, context.RequestAborted);
    }

    private static async Task WriteJsonAsync(HttpContext context, int statusCode, JsonElement value, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(value);
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength = body.Length;
        await context.Response.Body.WriteAsync(body, cancellationToken);
    }
}