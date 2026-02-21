using System.Net.Http;

namespace OpenClaw.Core.Http;

/// <summary>
/// Creates <see cref="HttpClient"/> instances with a <see cref="SocketsHttpHandler"/>
/// configured for DNS-friendly connection recycling. Long-lived <c>HttpClient</c> instances
/// can hold stale DNS entries; setting <see cref="SocketsHttpHandler.PooledConnectionLifetime"/>
/// ensures connections are periodically refreshed without disposing the client.
/// </summary>
public static class HttpClientFactory
{
    /// <summary>
    /// Default pooled connection lifetime (2 minutes).
    /// Matches the Microsoft guidance for long-lived HttpClient instances.
    /// </summary>
    private static readonly TimeSpan DefaultPooledConnectionLifetime = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Creates an <see cref="HttpClient"/> backed by a <see cref="SocketsHttpHandler"/>
    /// with a 2-minute pooled connection lifetime to avoid DNS staleness.
    /// </summary>
    public static HttpClient Create(TimeSpan? pooledConnectionLifetime = null)
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = pooledConnectionLifetime ?? DefaultPooledConnectionLifetime
        };

        return new HttpClient(handler);
    }
}
