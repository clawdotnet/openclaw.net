using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using OpenClaw.LayaService.Hosting;
using OpenClaw.LayaService.Protocol;
using Xunit;

namespace OpenClaw.LayaService.Tests;

public sealed class DecisionServerTests : IAsyncLifetime
{
    private const string Model = "laya@1c5edc17a7acd8701df6fc341c0d179f1c62c982";
    private const string GoldenRequest = "{\"model\":\"" + Model + "\",\"state\":\"hello\",\"questions\":{\"tier\":{\"type\":\"choice\",\"instructions\":\"Which task?\",\"criteria\":{\"small\":\"simple\",\"large\":\"complex\"}}},\"rubric_version\":\"test-v1\"}";
    private readonly FakePredictor _predictor = new();
    private WebApplication _app = null!;
    private HttpClient _http = null!;

    public async ValueTask InitializeAsync()
    {
        _app = DecisionServer.Build(_predictor, new ServiceOptions(0, 8, 65536, TimeSpan.FromSeconds(5)));
        await _app.StartAsync();
        var server = _app.Services.GetRequiredService<IServer>();
        var address = server.Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        _http = new HttpClient { BaseAddress = new Uri(address) };
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task HealthAndValidRequest_ReturnPredictorResponses()
    {
        using var health = await _http.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.True(JsonDocument.Parse(await health.Content.ReadAsStringAsync()).RootElement.GetProperty("ready").GetBoolean());

        using var response = await _http.SendAsync(CreatePost());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(1, _predictor.Calls);
    }

    [Fact]
    public async Task RejectsOriginHostChunkedAndOversizedBodies()
    {
        using var originRequest = CreatePost();
        originRequest.Headers.Add("Origin", "https://example.invalid");
        using var originResponse = await _http.SendAsync(originRequest);
        Assert.Equal(HttpStatusCode.Forbidden, originResponse.StatusCode);

        using var hostRequest = CreatePost();
        hostRequest.Headers.Host = "attacker.invalid";
        using var hostResponse = await _http.SendAsync(hostRequest);
        Assert.Equal(HttpStatusCode.Forbidden, hostResponse.StatusCode);

        using var chunkedRequest = CreatePost();
        chunkedRequest.Headers.TransferEncodingChunked = true;
        using var chunkedResponse = await _http.SendAsync(chunkedRequest);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, chunkedResponse.StatusCode);

        using var largeRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/decisions")
        {
            Content = new StringContent(new string('x', 65537), Encoding.UTF8, "application/json")
        };
        using var largeResponse = await _http.SendAsync(largeRequest);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, largeResponse.StatusCode);
        Assert.Equal(0, _predictor.Calls);
    }

    [Fact]
    public async Task InvalidRequestDoesNotEchoInput()
    {
        var secretRequest = GoldenRequest.Replace(Model, "SECRET", StringComparison.Ordinal);
        using var response = await _http.PostAsync("/v1/decisions", JsonContent(secretRequest));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.DoesNotContain("SECRET", body, StringComparison.Ordinal);
        Assert.Equal("model_version_mismatch", JsonDocument.Parse(body).RootElement.GetProperty("error").GetString());
        Assert.Equal(0, _predictor.Calls);
    }

    [Fact]
    public async Task PredictorFailureReturnsFixedServiceError()
    {
        _predictor.Failure = new InvalidOperationException("sensitive inference detail");
        using var response = await _http.SendAsync(CreatePost());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("{\"error\":\"inference_failed\"}", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ConcurrentInferenceIsRejectedWithoutQueueing()
    {
        _predictor.Block = true;
        using var firstRequest = CreatePost();
        var firstResponseTask = _http.SendAsync(firstRequest);
        await _predictor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));

        using var secondResponse = await _http.SendAsync(CreatePost());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, secondResponse.StatusCode);
        Assert.Equal("busy", JsonDocument.Parse(await secondResponse.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString());

        _predictor.Release.TrySetResult();
        using var firstResponse = await firstResponseTask;
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
    }

    private static HttpRequestMessage CreatePost()
        => new(HttpMethod.Post, "/v1/decisions") { Content = JsonContent(GoldenRequest) };

    private static StringContent JsonContent(string value)
        => new(value, Encoding.UTF8, "application/json");

    private sealed class FakePredictor : IDecisionPredictor
    {
        public string Model => DecisionServerTests.Model;
        private static readonly JsonElement Health = JsonDocument.Parse("{\"ready\":true}").RootElement.Clone();
        private static readonly JsonElement Result = JsonDocument.Parse("{\"ok\":true}").RootElement.Clone();

        public int Calls { get; private set; }
        public Exception? Failure { get; set; }
        public bool Block { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public JsonElement GetHealth() => Health;

        public async Task<JsonElement> PredictAsync(DecisionWireRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            Entered.TrySetResult();
            if (Block) await Release.Task.WaitAsync(cancellationToken);
            if (Failure is not null) throw Failure;
            return Result;
        }
    }
}
