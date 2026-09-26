using System.ComponentModel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace NacosLiveAcceptance;

internal static class WeatherFixture
{
    public static async Task RunAsync(int port, CancellationToken cancellationToken)
    {
        await using var app = CreateApplication(port);
        await app.StartAsync(cancellationToken);
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    internal static WebApplication CreateApplication(int port)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Services
            .AddMcpServer(options => options.ProtocolVersion = "2025-11-25")
            .WithHttpTransport()
            .WithTools<WeatherMcpTools>();

        var app = builder.Build();
        app.MapMcp("/mcp");
        app.Map("/health", static context =>
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "application/json";
            return context.Response.WriteAsync("{\"status\":\"ok\"}");
        });
        return app;
    }
}

[McpServerToolType]
internal sealed class WeatherMcpTools
{
    [McpServerTool(Name = "get_weather", ReadOnly = true), Description("Return deterministic acceptance weather for Oslo.")]
    public string GetWeather(string city)
    {
        if (!string.Equals(city, "Oslo", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The acceptance fixture only supports Oslo.", nameof(city));
        }

        return "{\"city\":\"Oslo\",\"temperature_c\":7.5,\"source\":\"acceptance fixture\"}";
    }
}