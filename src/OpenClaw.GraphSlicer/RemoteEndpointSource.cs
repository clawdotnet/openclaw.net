using System.Net;
using System.Text;
using VDS.RDF;
using VDS.RDF.Parsing;
using OpenClaw.Core.Models;

namespace OpenClaw.GraphSlicer;

internal sealed class RemoteEndpointSource : ISparqlSource
{
    private readonly SliceSourceConfig _config;
    private readonly HttpClient _httpClient;

    public RemoteEndpointSource(SliceSourceConfig config, HttpClient? httpClient = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        if (string.IsNullOrWhiteSpace(config.Endpoint))
            throw new ArgumentException("Endpoint is required for remote-endpoint source.");
        _httpClient = httpClient ?? new HttpClient();
    }

    public async Task<IGraph> ExecuteConstructAsync(string constructQuery, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_config.TimeoutSeconds));

        using var request = new HttpRequestMessage(HttpMethod.Post, _config.Endpoint);
        request.Content = new FormUrlEncodedContent([
            new KeyValuePair<string, string>("query", constructQuery)
        ]);
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/n-triples"));

        ConfigureAuth(request);

        using var response = await _httpClient.SendAsync(request, cts.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var responseBody = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        var graph = new Graph();
        var parser = new NTriplesParser();
        using var reader = new StringReader(responseBody);
        parser.Load(graph, reader);

        return graph;
    }

    private void ConfigureAuth(HttpRequestMessage request)
    {
        var auth = _config.Auth;
        if (auth is null || string.Equals(auth.Type, "none", StringComparison.OrdinalIgnoreCase))
            return;

        var username = Environment.GetEnvironmentVariable(auth.UsernameEnv ?? "")?.Trim() ?? "";
        var password = Environment.GetEnvironmentVariable(auth.PasswordEnv ?? "")?.Trim() ?? "";

        if (!string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password))
        {
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", credentials);
        }
    }
}