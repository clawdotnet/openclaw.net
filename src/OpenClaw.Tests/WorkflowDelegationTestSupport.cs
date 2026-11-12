using Microsoft.Extensions.Logging;
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
