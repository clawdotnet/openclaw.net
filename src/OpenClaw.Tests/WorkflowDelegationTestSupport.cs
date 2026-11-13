using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;

namespace OpenClaw.Tests;

internal sealed class RecordingHttpMessageHandler(
    Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => callback(request, cancellationToken);
}

internal sealed class WorkflowTestHttpClientFactory(HttpClient client) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => client;
}

internal sealed class RecordingWorkflowLogger<T> : ILogger<T>
{
    private readonly List<string> _messages = [];
    public IReadOnlyList<string> Messages => _messages;
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => _messages.Add(formatter(state, exception));
}

internal static class WorkflowDelegationTestSupport
{
    public static WorkflowAuthenticationConfig CreateAuthentication(string mode) => new()
    {
        Mode = mode, TokenEndpoint = "https://identity.example/token",
        ClientId = "client:with+space", ClientSecretRef = "env:UNSET_WORKFLOW_TEST_SECRET",
        Scopes = ["workflow.read", "workflow.respond"], UserAssertionProfile = "entra-delegated"
    };

    public static WorkflowExecutionContext CreateExecutionContext(
        string backendId, string issuer, string subject, string assertion, DateTimeOffset expiresAtUtc)
        => new(backendId, issuer, subject, expiresAtUtc, assertion);
}

internal sealed class WorkflowTestLifetime : IHostApplicationLifetime, IDisposable
{
    private readonly CancellationTokenSource _stopping = new();
    public CancellationToken ApplicationStarted => CancellationToken.None;
    public CancellationToken ApplicationStopping => _stopping.Token;
    public CancellationToken ApplicationStopped => CancellationToken.None;
    public void StopApplication() => _stopping.Cancel();
    public void Dispose() => _stopping.Dispose();
}

internal sealed class ManualWorkflowTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    private readonly List<ManualTimer> _timers = [];
    private readonly object _gate = new();
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan duration)
    {
        _now += duration;
        ManualTimer[] timers;
        lock (_gate)
            timers = _timers.ToArray();
        foreach (var timer in timers)
            timer.Tick(_now);
    }
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (_gate)
            _timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }
    private sealed class ManualTimer(ManualWorkflowTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private DateTimeOffset _due = DateTimeOffset.MaxValue;
        private TimeSpan _period = Timeout.InfiniteTimeSpan;
        private bool _disposed;
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (_disposed) return false;
            _due = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : owner._now + dueTime;
            _period = period;
            return true;
        }
        public void Tick(DateTimeOffset now)
        {
            if (_disposed || now < _due) return;
            _due = _period == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : now + _period;
            callback(state);
        }
        public void Dispose()
        {
            _disposed = true;
            lock (owner._gate)
                owner._timers.Remove(this);
        }
        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
