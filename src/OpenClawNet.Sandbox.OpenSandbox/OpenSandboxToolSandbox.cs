using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;

namespace OpenClawNet.Sandbox.OpenSandbox;

public sealed class OpenSandboxToolSandbox : IToolSandbox, IAsyncDisposable
{
    private readonly HttpClient _httpClient;
    private readonly OpenSandboxOptions _options;
    private readonly ILogger<OpenSandboxToolSandbox>? _logger;
    private readonly SemaphoreSlim _leaseGate = new(1, 1);
    private readonly Dictionary<string, SandboxLease> _leases = new(StringComparer.Ordinal);
    private bool _disposed;

    public OpenSandboxToolSandbox(
        HttpClient httpClient,
        OpenSandboxOptions options,
        ILogger<OpenSandboxToolSandbox>? logger = null)
    {
        _httpClient = httpClient;
        _options = options;
        _logger = logger;

        _httpClient.BaseAddress = options.GetApiBaseUri();
        _httpClient.Timeout = Timeout.InfiniteTimeSpan;
        if (!_httpClient.DefaultRequestHeaders.Accept.Any(header =>
                string.Equals(header.MediaType, "application/json", StringComparison.OrdinalIgnoreCase)))
        {
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }

        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("OPEN-SANDBOX-API-KEY", _options.ApiKey);
    }

    public async Task<SandboxResult> ExecuteAsync(
        SandboxExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(request.Command))
            throw new ToolSandboxException("Error: Sandbox command is required.");

        if (string.IsNullOrWhiteSpace(request.Template))
            throw new ToolSandboxException("Error: Sandbox template is required.");

        var ttl = request.TimeToLiveSeconds is > 0
            ? request.TimeToLiveSeconds.Value
            : _options.DefaultTTL;

        await EvictExpiredLeasesAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(request.LeaseKey))
        {
            var oneShotLease = await CreateLeaseAsync(request.Template, ttl, leaseKey: null, cancellationToken);
            try
            {
                return await ExecuteAgainstLeaseAsync(oneShotLease, request, ttl, cancellationToken);
            }
            finally
            {
                await DeleteSandboxBestEffortAsync(oneShotLease.SandboxId, CancellationToken.None);
            }
        }

        var lease = await EnsureLeaseAsync(request.LeaseKey, request.Template, ttl, cancellationToken);
        return await ExecuteAgainstLeaseAsync(lease, request, ttl, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        List<SandboxLease> leases;
        await _leaseGate.WaitAsync();
        try
        {
            leases = [.. _leases.Values];
            _leases.Clear();
        }
        finally
        {
            _leaseGate.Release();
        }

        foreach (var lease in leases)
            await DeleteSandboxBestEffortAsync(lease.SandboxId, CancellationToken.None);

        _leaseGate.Dispose();
    }

    private async Task<SandboxResult> ExecuteAgainstLeaseAsync(
        SandboxLease lease,
        SandboxExecutionRequest request,
        int ttl,
        CancellationToken cancellationToken)
    {
        try
        {
            await RenewLeaseAsync(lease, ttl, cancellationToken);

            var command = BuildCommandText(request);
            var response = await SendAsync(
                HttpMethod.Post,
                $"sandboxes/{Uri.EscapeDataString(lease.SandboxId)}/exec",
                new OpenSandboxExecRequest { Command = command },
                OpenSandboxJsonContext.Default.OpenSandboxExecRequest,
                cancellationToken);

            return await DeserializeAsync(
                response,
                OpenSandboxJsonContext.Default.OpenSandboxExecResponse,
                static payload => new SandboxResult
                {
                    ExitCode = payload.ExitCode,
                    Stdout = payload.StdOut,
                    Stderr = payload.StdErr
                },
                cancellationToken);
        }
        catch (OpenSandboxMissingLeaseException)
        {
            if (string.IsNullOrWhiteSpace(request.LeaseKey))
                throw;

            await RemoveLeaseAsync(request.LeaseKey);
            var recreatedLease = await EnsureLeaseAsync(request.LeaseKey, request.Template!, ttl, cancellationToken);
            return await ExecuteAgainstLeaseAsync(recreatedLease, request, ttl, cancellationToken);
        }
    }

    private async Task<SandboxLease> EnsureLeaseAsync(
        string leaseKey,
        string template,
        int ttl,
        CancellationToken cancellationToken)
    {
        await _leaseGate.WaitAsync(cancellationToken);
        try
        {
            if (_leases.TryGetValue(leaseKey, out var existing) &&
                existing.ExpiresAt > DateTimeOffset.UtcNow &&
                string.Equals(existing.Template, template, StringComparison.Ordinal))
            {
                return existing;
            }
        }
        finally
        {
            _leaseGate.Release();
        }

        var created = await CreateLeaseAsync(template, ttl, leaseKey, cancellationToken);

        await _leaseGate.WaitAsync(cancellationToken);
        try
        {
            _leases[leaseKey] = created;
            return created;
        }
        finally
        {
            _leaseGate.Release();
        }
    }

    private async Task<SandboxLease> CreateLeaseAsync(
        string template,
        int ttl,
        string? leaseKey,
        CancellationToken cancellationToken)
    {
        var response = await SendAsync(
            HttpMethod.Post,
            "sandboxes",
            new OpenSandboxCreateRequest
            {
                Image = new OpenSandboxImageSpec { Uri = template },
                Timeout = ttl,
                Metadata = leaseKey is null
                    ? null
                    : new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["leaseKey"] = leaseKey,
                        ["toolTemplate"] = template
                    }
            },
            OpenSandboxJsonContext.Default.OpenSandboxCreateRequest,
            cancellationToken);

        return await DeserializeAsync(
            response,
            OpenSandboxJsonContext.Default.OpenSandboxCreateResponse,
            payload => new SandboxLease(
                payload.Id,
                template,
                payload.ExpiresAt ?? DateTimeOffset.UtcNow.AddSeconds(ttl)),
            cancellationToken);
    }

    private async Task RenewLeaseAsync(
        SandboxLease lease,
        int ttl,
        CancellationToken cancellationToken)
    {
        var targetExpiration = DateTimeOffset.UtcNow.AddSeconds(ttl);
        try
        {
            var response = await SendAsync(
                HttpMethod.Post,
                $"sandboxes/{Uri.EscapeDataString(lease.SandboxId)}/renew-expiration",
                new OpenSandboxRenewRequest { ExpiresAt = targetExpiration.ToString("O") },
                OpenSandboxJsonContext.Default.OpenSandboxRenewRequest,
                cancellationToken);

            var renewedAt = await DeserializeAsync(
                response,
                OpenSandboxJsonContext.Default.OpenSandboxRenewResponse,
                payload => payload.ExpiresAt ?? targetExpiration,
                cancellationToken);

            lease.ExpiresAt = renewedAt;
        }
        catch (OpenSandboxMissingLeaseException)
        {
            throw;
        }
    }

    private async Task EvictExpiredLeasesAsync(CancellationToken cancellationToken)
    {
        List<(string LeaseKey, SandboxLease Lease)> expired;
        await _leaseGate.WaitAsync(cancellationToken);
        try
        {
            var now = DateTimeOffset.UtcNow;
            expired = _leases
                .Where(static pair => pair.Value.ExpiresAt <= DateTimeOffset.UtcNow)
                .Select(static pair => (pair.Key, pair.Value))
                .ToList();

            foreach (var (leaseKey, _) in expired)
                _leases.Remove(leaseKey);
        }
        finally
        {
            _leaseGate.Release();
        }

        foreach (var (_, lease) in expired)
            await DeleteSandboxBestEffortAsync(lease.SandboxId, cancellationToken);
    }

    private async Task RemoveLeaseAsync(string leaseKey)
    {
        await _leaseGate.WaitAsync();
        try
        {
            _leases.Remove(leaseKey);
        }
        finally
        {
            _leaseGate.Release();
        }
    }

    private async Task<HttpResponseMessage> SendAsync<TPayload>(
        HttpMethod method,
        string path,
        TPayload? payload,
        JsonTypeInfo<TPayload> payloadTypeInfo,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        if (payload is not null)
        {
            request.Content = new StringContent(
                JsonSerializer.Serialize(payload, payloadTypeInfo),
                Encoding.UTF8,
                "application/json");
        }

        try
        {
            var response = await _httpClient.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                response.Dispose();
                throw new OpenSandboxMissingLeaseException();
            }

            if (response.IsSuccessStatusCode)
                return response;

            var responseBody = response.Content is null
                ? null
                : await response.Content.ReadAsStringAsync(cancellationToken);
            var message = $"OpenSandbox request failed ({(int)response.StatusCode}).";
            response.Dispose();

            if ((int)response.StatusCode >= 500)
                throw new ToolSandboxUnavailableException(message);

            throw new ToolSandboxException(string.IsNullOrWhiteSpace(responseBody) ? message : $"{message} {responseBody}");
        }
        catch (OpenSandboxMissingLeaseException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            throw new ToolSandboxUnavailableException("OpenSandbox is unreachable.", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ToolSandboxUnavailableException("OpenSandbox request timed out.", ex);
        }
    }

    private static async Task<TResult> DeserializeAsync<TPayload, TResult>(
        HttpResponseMessage response,
        JsonTypeInfo<TPayload> payloadTypeInfo,
        Func<TPayload, TResult> projector,
        CancellationToken cancellationToken)
    {
        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var payload = await JsonSerializer.DeserializeAsync(responseStream, payloadTypeInfo, cancellationToken);
        response.Dispose();

        if (payload is null)
            throw new ToolSandboxException("OpenSandbox returned an invalid response.");

        return projector(payload);
    }

    private async Task DeleteSandboxBestEffortAsync(string sandboxId, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, $"sandboxes/{Uri.EscapeDataString(sandboxId)}");
            using var response = await _httpClient.SendAsync(request, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to delete OpenSandbox lease {SandboxId}", sandboxId);
        }
    }

    private static string BuildCommandText(SandboxExecutionRequest request)
    {
        var builder = new StringBuilder();
        var environment = request.Environment ?? new Dictionary<string, string>(StringComparer.Ordinal);
        if (environment.Count > 0)
        {
            foreach (var pair in environment.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                builder.Append("export ");
                builder.Append(pair.Key);
                builder.Append('=');
                builder.Append(SandboxCommandLine.Quote(pair.Value));
                builder.Append("; ");
            }
        }

        if (!string.IsNullOrWhiteSpace(request.WorkingDirectory))
        {
            builder.Append("cd ");
            builder.Append(SandboxCommandLine.Quote(request.WorkingDirectory));
            builder.Append(" && ");
        }

        builder.Append(SandboxCommandLine.BuildCommand(request.Command, request.Arguments));
        return builder.ToString();
    }

    private sealed class SandboxLease
    {
        public SandboxLease(string sandboxId, string template, DateTimeOffset expiresAt)
        {
            SandboxId = sandboxId;
            Template = template;
            ExpiresAt = expiresAt;
        }

        public string SandboxId { get; }
        public string Template { get; }
        public DateTimeOffset ExpiresAt { get; set; }
    }

    private sealed class OpenSandboxMissingLeaseException : Exception;
}
