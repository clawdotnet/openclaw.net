using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Security;

namespace OpenClaw.Gateway.Backends;

internal sealed class ConnectedAccountProtectionService
{
    private readonly IDataProtector _protector;

    public ConnectedAccountProtectionService(IDataProtectionProvider provider)
        => _protector = provider.CreateProtector("OpenClaw.Gateway.ConnectedAccounts");

    public string Protect(string json)
        => _protector.Protect(json);

    public string Unprotect(string protectedJson)
        => _protector.Unprotect(protectedJson);
}

internal sealed class ConnectedAccountService
{
    private readonly IConnectedAccountStore _store;
    private readonly ConnectedAccountProtectionService _protection;

    public ConnectedAccountService(
        IConnectedAccountStore store,
        ConnectedAccountProtectionService protection)
    {
        _store = store;
        _protection = protection;
    }

    public ValueTask<IReadOnlyList<ConnectedAccount>> ListAsync(CancellationToken ct)
        => _store.ListAccountsAsync(ct);

    public ValueTask<ConnectedAccount?> GetAsync(string id, CancellationToken ct)
        => _store.GetAccountAsync(id, ct);

    public async ValueTask<ConnectedAccount> CreateAsync(ConnectedAccountCreateRequest request, CancellationToken ct)
    {
        var metadata = request.Metadata ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var scopes = request.Scopes ?? [];
        var modes = 0;
        if (!string.IsNullOrWhiteSpace(request.SecretRef))
            modes++;
        if (!string.IsNullOrWhiteSpace(request.Secret))
            modes++;
        if (!string.IsNullOrWhiteSpace(request.TokenFilePath))
            modes++;

        if (modes != 1)
            throw new InvalidOperationException("Exactly one of secretRef, secret, or tokenFilePath is required.");

        var id = $"acct_{Guid.NewGuid():N}"[..17];
        var now = DateTimeOffset.UtcNow;
        var normalizedMetadata = metadata
            .Where(static pair => !string.IsNullOrWhiteSpace(pair.Key))
            .ToDictionary(
                static pair => pair.Key.Trim(),
                static pair => pair.Value?.Trim() ?? string.Empty,
                StringComparer.OrdinalIgnoreCase);

        var account = new ConnectedAccount
        {
            Id = id,
            Provider = request.Provider.Trim(),
            DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? null : request.DisplayName.Trim(),
            SecretKind = ResolveSecretKind(request),
            SecretRef = string.IsNullOrWhiteSpace(request.SecretRef) ? null : request.SecretRef.Trim(),
            EncryptedSecretJson = string.IsNullOrWhiteSpace(request.Secret)
                ? null
                : _protection.Protect(JsonSerializer.Serialize(
                    new ConnectedAccountSecretPayload { Secret = request.Secret },
                    CoreJsonContext.Default.ConnectedAccountSecretPayload)),
            TokenFilePath = NormalizeOptionalPath(request.TokenFilePath),
            Scopes = scopes
                .Where(static item => !string.IsNullOrWhiteSpace(item))
                .Select(static item => item.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            ExpiresAt = request.ExpiresAt,
            IsActive = request.IsActive ?? true,
            Metadata = normalizedMetadata,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        await _store.SaveAccountAsync(account, ct);
        return account;
    }

    public ValueTask DeleteAsync(string id, CancellationToken ct)
        => _store.DeleteAccountAsync(id, ct);

    internal string UnprotectSecretPayload(string protectedJson)
        => _protection.Unprotect(protectedJson);

    private static string ResolveSecretKind(ConnectedAccountCreateRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.SecretRef))
            return ConnectedAccountSecretKind.SecretRef;
        if (!string.IsNullOrWhiteSpace(request.TokenFilePath))
            return ConnectedAccountSecretKind.TokenFile;

        return ConnectedAccountSecretKind.ProtectedBlob;
    }

    private static string? NormalizeOptionalPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim()));
    }
}

internal sealed class BackendCredentialResolver : IBackendCredentialResolver
{
    private readonly ConnectedAccountService _accounts;
    private readonly IConnectedAccountStore _store;

    public BackendCredentialResolver(
        ConnectedAccountService accounts,
        IConnectedAccountStore store)
    {
        _accounts = accounts;
        _store = store;
    }

    public ValueTask<ResolvedBackendCredential?> ResolveAsync(string provider, BackendCredentialSourceConfig? source, CancellationToken ct)
    {
        if (source is null)
            return ValueTask.FromResult<ResolvedBackendCredential?>(null);

        return ResolveAsync(provider, new ConnectedAccountSecretRef
        {
            SecretRef = source.SecretRef,
            TokenFilePath = source.TokenFilePath,
            ConnectedAccountId = source.ConnectedAccountId
        }, ct);
    }

    public async ValueTask<ResolvedBackendCredential?> ResolveAsync(string provider, ConnectedAccountSecretRef? source, CancellationToken ct)
    {
        if (source is null)
            return null;

        if (!string.IsNullOrWhiteSpace(source.ConnectedAccountId))
            return await ResolveAccountAsync(source.ConnectedAccountId.Trim(), ct);

        if (!string.IsNullOrWhiteSpace(source.TokenFilePath))
        {
            var tokenFilePath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(source.TokenFilePath.Trim()));
            if (!File.Exists(tokenFilePath))
                throw new FileNotFoundException($"Token file '{tokenFilePath}' does not exist.", tokenFilePath);

            var secret = (await File.ReadAllTextAsync(tokenFilePath, ct)).Trim();
            if (string.IsNullOrWhiteSpace(secret))
                throw new InvalidOperationException($"Token file '{tokenFilePath}' is empty.");

            return new ResolvedBackendCredential
            {
                Provider = provider,
                SourceKind = ConnectedAccountSecretKind.TokenFile,
                Secret = secret,
                TokenFilePath = tokenFilePath
            };
        }

        if (!string.IsNullOrWhiteSpace(source.SecretRef))
        {
            var resolved = SecretResolver.Resolve(source.SecretRef.Trim());
            if (string.IsNullOrWhiteSpace(resolved))
                throw new InvalidOperationException($"Unable to resolve credential secret ref '{source.SecretRef}'.");

            return new ResolvedBackendCredential
            {
                Provider = provider,
                SourceKind = ConnectedAccountSecretKind.SecretRef,
                Secret = resolved
            };
        }

        return null;
    }

    private async ValueTask<ResolvedBackendCredential?> ResolveAccountAsync(string accountId, CancellationToken ct)
    {
        var account = await _store.GetAccountAsync(accountId, ct);
        if (account is null)
            throw new InvalidOperationException($"Connected account '{accountId}' was not found.");
        if (!account.IsActive)
            throw new InvalidOperationException($"Connected account '{accountId}' is inactive.");
        if (account.ExpiresAt is DateTimeOffset expiresAt && expiresAt <= DateTimeOffset.UtcNow)
            throw new InvalidOperationException($"Connected account '{accountId}' is expired.");

        return account.SecretKind switch
        {
            ConnectedAccountSecretKind.SecretRef => new ResolvedBackendCredential
            {
                Provider = account.Provider,
                SourceKind = account.SecretKind,
                AccountId = account.Id,
                DisplayName = account.DisplayName,
                Secret = SecretResolver.Resolve(account.SecretRef)
                    ?? throw new InvalidOperationException($"Unable to resolve secret ref for account '{account.Id}'."),
                Scopes = account.Scopes,
                ExpiresAt = account.ExpiresAt,
                Metadata = account.Metadata
            },
            ConnectedAccountSecretKind.TokenFile => await ResolveTokenFileAccountAsync(account, ct),
            ConnectedAccountSecretKind.ProtectedBlob => ResolveProtectedAccount(account),
            _ => throw new InvalidOperationException($"Unsupported account secret kind '{account.SecretKind}'.")
        };
    }

    private async ValueTask<ResolvedBackendCredential> ResolveTokenFileAccountAsync(ConnectedAccount account, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(account.TokenFilePath))
            throw new InvalidOperationException($"Connected account '{account.Id}' does not have a token file path.");

        var tokenFilePath = Path.GetFullPath(account.TokenFilePath);
        if (!File.Exists(tokenFilePath))
            throw new FileNotFoundException($"Token file '{tokenFilePath}' does not exist.", tokenFilePath);

        var secret = (await File.ReadAllTextAsync(tokenFilePath, ct)).Trim();
        if (string.IsNullOrWhiteSpace(secret))
            throw new InvalidOperationException($"Token file '{tokenFilePath}' is empty.");

        return new ResolvedBackendCredential
        {
            Provider = account.Provider,
            SourceKind = account.SecretKind,
            AccountId = account.Id,
            DisplayName = account.DisplayName,
            Secret = secret,
            TokenFilePath = tokenFilePath,
            Scopes = account.Scopes,
            ExpiresAt = account.ExpiresAt,
            Metadata = account.Metadata
        };
    }

    private ResolvedBackendCredential ResolveProtectedAccount(ConnectedAccount account)
    {
        if (string.IsNullOrWhiteSpace(account.EncryptedSecretJson))
            throw new InvalidOperationException($"Connected account '{account.Id}' does not contain a protected secret.");

        var payload = JsonSerializer.Deserialize(
            _accounts.UnprotectSecretPayload(account.EncryptedSecretJson),
            CoreJsonContext.Default.ConnectedAccountSecretPayload)
            ?? throw new InvalidOperationException($"Connected account '{account.Id}' has an invalid protected secret payload.");

        return new ResolvedBackendCredential
        {
            Provider = account.Provider,
            SourceKind = account.SecretKind,
            AccountId = account.Id,
            DisplayName = account.DisplayName,
            Secret = payload.Secret,
            Scopes = account.Scopes,
            ExpiresAt = account.ExpiresAt,
            Metadata = account.Metadata
        };
    }
}
