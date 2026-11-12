using System.Net;
using System.Text;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Gateway.Workflows;
using Xunit;

namespace OpenClaw.Tests;

public sealed class WorkflowAccessTokenProviderTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 12, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(300, 600, 0, 300, 240)]
    [InlineData(40, 600, 0, 40, 20)]
    [InlineData(300, 30, 0, 30, 15)]
    [InlineData(40, 600, 10, 40, 25)]
    public async Task GetAsync_WhenLifetimeChanges_ShouldFixRefreshAtAcquisition(int lifetime, int assertionLifetime, int delay, int expiry, int refresh)
    {
        using var fixture = new Fixture((f, _) =>
        {
            f.Clock.Advance(TimeSpan.FromSeconds(delay));
            return Task.FromResult(Response(lifetime));
        });
        var caller = Caller(lifetime: assertionLifetime);
        var first = await fixture.Provider.GetAsync("review", fixture.Auth, caller, TestContext.Current.CancellationToken);
        Assert.Equal(Start.AddSeconds(expiry), first.ExpiresAtUtc);
        Assert.Equal(Start.AddSeconds(refresh), first.RefreshAtUtc);
        fixture.Clock.Advance(TimeSpan.FromSeconds(refresh - delay - 1));
        Assert.Same(first, await fixture.Provider.GetAsync("review", fixture.Auth, caller, TestContext.Current.CancellationToken));
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        var refreshed = await fixture.Provider.GetAsync("review", fixture.Auth, caller, TestContext.Current.CancellationToken);
        Assert.NotSame(first, refreshed);
        Assert.Equal(2, fixture.Requests);
    }

    [Theory]
    [InlineData("subject")]
    [InlineData("issuer")]
    [InlineData("assertion")]
    [InlineData("backend")]
    [InlineData("scope")]
    public async Task GetAsync_WhenCacheIdentityDiffers_ShouldNotShareToken(string dimension)
    {
        using var fixture = new Fixture((f, _) => Task.FromResult(Response(300, "token-" + f.Requests)));
        var first = await fixture.Provider.GetAsync("review", fixture.Auth, Caller(), TestContext.Current.CancellationToken);
        var secondCaller = Caller(
            subject: dimension == "subject" ? "other" : "user",
            issuer: dimension == "issuer" ? "other-issuer" : "issuer",
            assertion: dimension == "assertion" ? "other-assertion" : "assertion",
            backend: dimension == "backend" ? "other-backend" : "review");
        if (dimension == "scope") fixture.Auth.Scopes = ["other"];
        var second = await fixture.Provider.GetAsync(secondCaller.BackendId, fixture.Auth, secondCaller, TestContext.Current.CancellationToken);
        Assert.NotEqual(first.AccessToken, second.AccessToken);
        Assert.Equal(2, fixture.Requests);
    }

    [Fact]
    public async Task GetAsync_WhenSameKeyConcurrent_ShouldCoalesceAndSeparateWaiterCancellation()
    {
        var gate = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new Fixture((_, ct) => gate.Task.WaitAsync(ct));
        using var cancelled = new CancellationTokenSource();
        var first = fixture.Provider.GetAsync("review", fixture.Auth, Caller(), cancelled.Token);
        var rest = Enumerable.Range(0, 100).Select(_ => fixture.Provider.GetAsync("review", fixture.Auth, Caller(), TestContext.Current.CancellationToken)).ToArray();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        gate.SetResult(Response(300));
        var results = await Task.WhenAll(rest);
        Assert.All(results, result => Assert.Equal("token", result.AccessToken));
        Assert.Equal(1, fixture.Requests);
    }

    [Fact]
    public async Task GetAsync_WhenAllKeysInFlight_ShouldRejectAtCapacity()
    {
        var gate = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new Fixture((f, ct) =>
        {
            if (f.Requests == 1024) allStarted.TrySetResult();
            return gate.Task.WaitAsync(ct);
        });
        var pending = Enumerable.Range(0, 1024).Select(i =>
            fixture.Provider.GetAsync("review", fixture.Auth, Caller(assertion: "assertion-" + i), TestContext.Current.CancellationToken)).ToArray();
        var error = await Assert.ThrowsAsync<WorkflowDelegationException>(() =>
            fixture.Provider.GetAsync("review", fixture.Auth, Caller(assertion: "overflow"), TestContext.Current.CancellationToken));
        Assert.Equal(502, error.StatusCode);
        await allStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1024, fixture.Requests);
        fixture.Lifetime.StopApplication();
        foreach (var task in pending)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task GetAsync_WhenFailureOccurs_ShouldAllowLaterRetry()
    {
        using var fixture = new Fixture((f, _) => Task.FromResult(f.Requests == 1
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") }
            : Response(300)));
        await Assert.ThrowsAsync<WorkflowDelegationException>(() =>
            fixture.Provider.GetAsync("review", fixture.Auth, Caller(), TestContext.Current.CancellationToken));
        var token = await fixture.Provider.GetAsync("review", fixture.Auth, Caller(), TestContext.Current.CancellationToken);
        Assert.Equal("token", token.AccessToken);
        Assert.Equal(2, fixture.Requests);
    }

    [Fact]
    public async Task GetAsync_WhenAssertionExpired_ShouldNotExchange()
    {
        using var fixture = new Fixture((_, _) => Task.FromResult(Response(300)));
        var error = await Assert.ThrowsAsync<WorkflowDelegationException>(() =>
            fixture.Provider.GetAsync("review", fixture.Auth, Caller(lifetime: 0), TestContext.Current.CancellationToken));
        Assert.Equal(401, error.StatusCode);
        Assert.Equal(0, fixture.Requests);
    }

    [Fact]
    public async Task GetAsync_WhenSameKeyWaitersReachLimit_ShouldRejectOverflow()
    {
        var gate = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new Fixture((_, ct) => gate.Task.WaitAsync(ct));
        var pending = Enumerable.Range(0, 1024).Select(_ =>
            fixture.Provider.GetAsync("review", fixture.Auth, Caller(), TestContext.Current.CancellationToken)).ToArray();
        var error = await Assert.ThrowsAsync<WorkflowDelegationException>(() =>
            fixture.Provider.GetAsync("review", fixture.Auth, Caller(), TestContext.Current.CancellationToken));
        Assert.Equal(502, error.StatusCode);
        gate.SetResult(Response(300));
        await Task.WhenAll(pending);
        Assert.Equal(1, fixture.Requests);
    }

    [Fact]
    public async Task GetAsync_WhenSharedTimeoutReached_ShouldTerminateAllWaiters()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new Fixture(async (_, ct) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Response(300);
        });
        var first = fixture.Provider.GetAsync("review", fixture.Auth, Caller(), TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        fixture.Clock.Advance(TimeSpan.FromSeconds(30));
        var error = await Assert.ThrowsAsync<WorkflowDelegationException>(() => first);
        Assert.Equal(502, error.StatusCode);
    }

    [Fact]
    public async Task GetAsync_WhenExchangeConsumesLifetime_ShouldRejectResult()
    {
        using var fixture = new Fixture((f, _) =>
        {
            f.Clock.Advance(TimeSpan.FromSeconds(10));
            return Task.FromResult(Response(5));
        });
        var error = await Assert.ThrowsAsync<WorkflowDelegationException>(() =>
            fixture.Provider.GetAsync("review", fixture.Auth, Caller(), TestContext.Current.CancellationToken));
        Assert.Equal(502, error.StatusCode);
    }

    [Fact]
    public async Task GetAsync_WhenRefreshFails_ShouldNotReuseOldToken()
    {
        using var fixture = new Fixture((f, _) => Task.FromResult(f.Requests == 1 ? Response(40)
            : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") }));
        await fixture.Provider.GetAsync("review", fixture.Auth, Caller(), TestContext.Current.CancellationToken);
        fixture.Clock.Advance(TimeSpan.FromSeconds(20));
        await Assert.ThrowsAsync<WorkflowDelegationException>(() =>
            fixture.Provider.GetAsync("review", fixture.Auth, Caller(), TestContext.Current.CancellationToken));
        Assert.Equal(2, fixture.Requests);
    }

    private static WorkflowExecutionContext Caller(string subject = "user", string issuer = "issuer",
        string assertion = "assertion", string backend = "review", int lifetime = 600)
        => new(backend, issuer, subject, Start.AddSeconds(lifetime), assertion);
    private static HttpResponseMessage Response(int lifetime, string token = "token") => new(HttpStatusCode.OK)
    {
        Content = new StringContent($$"""{"access_token":"{{token}}","token_type":"Bearer","expires_in":{{lifetime}}}""", Encoding.UTF8, "application/json")
    };

    private sealed class Fixture : IDisposable
    {
        private readonly string _secretName = "CACHE_TEST_SECRET_" + Guid.NewGuid().ToString("N");
        private readonly HttpClient _client;
        public ManualWorkflowTimeProvider Clock { get; } = new(Start);
        public WorkflowTestLifetime Lifetime { get; } = new();
        public WorkflowAuthenticationConfig Auth { get; } = WorkflowDelegationTestSupport.CreateAuthentication("entra-obo");
        public WorkflowAccessTokenProvider Provider { get; }
        private int _requests;
        public int Requests => Volatile.Read(ref _requests);
        public Fixture(Func<Fixture, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            Environment.SetEnvironmentVariable(_secretName, "test-secret");
            Auth.ClientSecretRef = "env:" + _secretName;
            _client = new HttpClient(new RecordingHttpMessageHandler((_, ct) =>
            {
                Interlocked.Increment(ref _requests);
                return handler(this, ct);
            }));
            var exchange = new WorkflowTokenExchangeClient(new WorkflowTestHttpClientFactory(_client), Clock,
                new RecordingWorkflowLogger<WorkflowTokenExchangeClient>());
            Provider = new(exchange, Clock, Lifetime, new RecordingWorkflowLogger<WorkflowAccessTokenProvider>());
        }
        public void Dispose()
        {
            Lifetime.StopApplication();
            Lifetime.Dispose();
            _client.Dispose();
            Environment.SetEnvironmentVariable(_secretName, null);
        }
    }
}
