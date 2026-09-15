using System.Net;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Models;
using OpenClaw.Core.Security;
using VaultSharp;
using VaultSharp.Core;

namespace OpenClaw.Security.Vault;

public sealed class VaultSecretProvider : ISecretProvider
{
    private readonly IVaultClient _client;
    private readonly VaultRefCache _cache;
    private readonly VaultSecurityOptions _options;
    private readonly ILogger<VaultSecretProvider> _logger;

    public VaultSecretProvider(IVaultClient client, VaultRefCache cache, VaultSecurityOptions options, ILogger<VaultSecretProvider> logger)
    {
        _client = client;
        _cache = cache;
        _options = options;
        _logger = logger;
    }

    public string Scheme => "vault";

    public bool CanResolve(string secretRef) =>
        !string.IsNullOrWhiteSpace(secretRef) &&
        secretRef.StartsWith("vault:", StringComparison.OrdinalIgnoreCase);

    public ValueTask<string?> ResolveAsync(string secretRef, CancellationToken ct)
        => new(ResolveInternalAsync(secretRef, ct));

    private async Task<string?> ResolveInternalAsync(string secretRef, CancellationToken ct)
    {
        var parsed = VaultRefParser.Parse(secretRef, defaultMount: _options.KvMount);

        var value = await _cache.GetOrFetchAsync(parsed, async token =>
        {
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeoutCts.CancelAfter(_options.RequestTimeout);

                var data = await _client.ReadSecretV2Async(parsed.Mount, parsed.Path, timeoutCts.Token)
                    .ConfigureAwait(false);

                if (data is null)
                    throw new VaultPathNotFoundException(
                        $"Vault path '{parsed.Mount}/data/{parsed.Path}' not found.");

                if (!data.TryGetValue(parsed.Key, out var raw) || raw is null)
                    throw new VaultKeyNotFoundException(
                        $"Vault key '{parsed.Key}' not found at path '{parsed.Mount}/data/{parsed.Path}'.");

                return raw.ToString() ?? string.Empty;
            }
            catch (VaultPathNotFoundException) { throw; }
            catch (VaultKeyNotFoundException) { throw; }
            catch (VaultAuthException) { throw; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (OperationCanceledException)
            {
                throw new VaultUnavailableException(
                    $"Vault request timed out after {_options.RequestTimeout}.", retryable: true);
            }
            catch (VaultApiException vex)
            {
                if (vex.HttpStatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    throw new VaultAuthException($"Vault auth failed: {vex.HttpStatusCode}");
                throw new VaultUnavailableException($"Vault error: {vex.HttpStatusCode}", retryable: true);
            }
            catch (HttpRequestException hex)
            {
                throw new VaultUnavailableException($"Vault network error: {hex.Message}", retryable: true);
            }
        }, ct).ConfigureAwait(false);

        return value;
    }
}

/// <summary>
/// Concrete <see cref="IVaultClient"/> backed by VaultSharp. The token is resolved
/// by the DI factory before construction; see <c>AddOpenClawVaultSecrets</c>.
/// </summary>
public sealed class VaultSharpClient : IVaultClient
{
    private readonly VaultSharp.VaultClient _client;

    public VaultSharpClient(string address, string token, string? ns, VaultTlsOptions tls)
    {
        var settings = new VaultClientSettings(
            address,
            new VaultSharp.V1.AuthMethods.Token.TokenAuthMethodInfo(token))
        {
            Namespace = ns,
        };
        var caCerts = VaultCaCertLoader.Load(tls.CaCertPath);
        if (tls.SkipVerify)
        {
            // VaultSharp 1.x exposes TLS customization via the handler post-processing hook.
            settings.PostProcessHttpClientHandlerAction = handler =>
            {
                if (handler is HttpClientHandler clientHandler)
                    clientHandler.ServerCertificateCustomValidationCallback =
                        HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
            };
        }
        else if (caCerts.Count > 0)
        {
            // Custom CA bundle: trust exactly the loaded roots, keep hostname checks.
            var validator = VaultCaCertLoader.BuildServerCertificateValidator(caCerts);
            settings.PostProcessHttpClientHandlerAction = handler =>
            {
                if (handler is HttpClientHandler clientHandler)
                    clientHandler.ServerCertificateCustomValidationCallback = validator;
            };
        }
        _client = new VaultSharp.VaultClient(settings);
    }

    public async Task<Dictionary<string, object>?> ReadSecretV2Async(
        string mount, string path, CancellationToken ct)
    {
        try
        {
            var secret = await _client.V1.Secrets.KeyValue.V2
                .ReadSecretAsync(path, version: null, mountPoint: mount, wrapTimeToLive: null)
                .WaitAsync(ct).ConfigureAwait(false);

            if (secret?.Data?.Data is not IDictionary<string, object> data)
                return null;

            return data.ToDictionary(kv => kv.Key, kv => kv.Value);
        }
        catch (VaultApiException vex) when (vex.HttpStatusCode == HttpStatusCode.NotFound)
        {
            return null; // Path does not exist.
        }
    }
}
