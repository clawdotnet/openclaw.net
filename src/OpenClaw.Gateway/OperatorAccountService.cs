using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Models;

namespace OpenClaw.Gateway;

internal sealed class OperatorAccountService
{
    internal sealed class StoreState
    {
        public List<StoredAccount> Accounts { get; init; } = [];
    }

    internal sealed class StoredAccount
    {
        public required string Id { get; init; }
        public required string Username { get; set; }
        public string DisplayName { get; set; } = "";
        public string Role { get; set; } = OperatorRoleNames.Viewer;
        public bool Enabled { get; set; } = true;
        public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
        public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? LastLoginAtUtc { get; set; }
        public required string PasswordSalt { get; set; }
        public required string PasswordHash { get; set; }
        public List<StoredToken> Tokens { get; init; } = [];
    }

    internal sealed class StoredToken
    {
        public required string Id { get; init; }
        public string Label { get; set; } = "";
        public required string TokenPrefix { get; init; }
        public required string SecretSalt { get; init; }
        public required string SecretHash { get; init; }
        public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? ExpiresAtUtc { get; init; }
        public DateTimeOffset? RevokedAtUtc { get; set; }
    }

    private const int Pbkdf2Iterations = 120_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const string DirectoryName = "admin";
    private const string FileName = "operator-accounts.json";

    private readonly string _path;
    private readonly Lock _gate = new();
    private readonly ILogger<OperatorAccountService> _logger;
    private StoreState? _cached;

    public OperatorAccountService(string storagePath, ILogger<OperatorAccountService> logger)
    {
        var rootedStoragePath = Path.IsPathRooted(storagePath)
            ? storagePath
            : Path.GetFullPath(storagePath);
        _path = Path.Combine(rootedStoragePath, DirectoryName, FileName);
        _logger = logger;
    }

    public IReadOnlyList<OperatorAccountSummary> List()
    {
        lock (_gate)
        {
            return LoadUnsafe().Accounts
                .OrderBy(static account => account.Username, StringComparer.OrdinalIgnoreCase)
                .Select(MapSummary)
                .ToArray();
        }
    }

    public OperatorAccountDetailResponse? Get(string id)
    {
        lock (_gate)
        {
            var account = LoadUnsafe().Accounts.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal));
            if (account is null)
                return null;

            return new OperatorAccountDetailResponse
            {
                Account = MapSummary(account),
                Tokens = account.Tokens
                    .OrderByDescending(static item => item.CreatedAtUtc)
                    .Select(MapToken)
                    .ToArray()
            };
        }
    }

    public OperatorAccountSummary Create(OperatorAccountCreateRequest request)
    {
        var username = NormalizeUsername(request.Username);
        var password = NormalizePassword(request.Password);

        lock (_gate)
        {
            var state = LoadUnsafe();
            if (state.Accounts.Any(item => string.Equals(item.Username, username, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"Operator account '{username}' already exists.");

            var salt = GenerateSalt();
            var account = new StoredAccount
            {
                Id = $"opacct_{Guid.NewGuid():N}"[..20],
                Username = username,
                DisplayName = request.DisplayName?.Trim() ?? "",
                Role = OperatorRoleNames.Normalize(request.Role),
                Enabled = request.Enabled,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                PasswordSalt = salt,
                PasswordHash = HashSecret(password, salt)
            };

            state.Accounts.Add(account);
            SaveUnsafe(state);
            return MapSummary(account);
        }
    }

    public OperatorAccountSummary? Update(string id, OperatorAccountUpdateRequest request)
    {
        lock (_gate)
        {
            var state = LoadUnsafe();
            var account = state.Accounts.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal));
            if (account is null)
                return null;

            if (request.DisplayName is not null)
                account.DisplayName = request.DisplayName.Trim();
            if (request.Role is not null)
                account.Role = OperatorRoleNames.Normalize(request.Role);
            if (request.Enabled.HasValue)
                account.Enabled = request.Enabled.Value;
            if (request.Password is not null)
            {
                var salt = GenerateSalt();
                account.PasswordSalt = salt;
                account.PasswordHash = HashSecret(NormalizePassword(request.Password), salt);
            }

            account.UpdatedAtUtc = DateTimeOffset.UtcNow;
            SaveUnsafe(state);
            return MapSummary(account);
        }
    }

    public bool Delete(string id)
    {
        lock (_gate)
        {
            var state = LoadUnsafe();
            var removed = state.Accounts.RemoveAll(item => string.Equals(item.Id, id, StringComparison.Ordinal)) > 0;
            if (removed)
                SaveUnsafe(state);
            return removed;
        }
    }

    public OperatorTokenExchangeResponse? CreateTokenFromCredentials(OperatorTokenExchangeRequest request)
    {
        var username = NormalizeUsername(request.Username);
        var password = NormalizePassword(request.Password);

        lock (_gate)
        {
            var state = LoadUnsafe();
            var account = state.Accounts.FirstOrDefault(item => string.Equals(item.Username, username, StringComparison.OrdinalIgnoreCase));
            if (account is null || !account.Enabled)
                return null;

            if (!SecretMatches(password, account.PasswordSalt, account.PasswordHash))
                return null;

            var created = CreateTokenUnsafe(account, request.Label, request.ExpiresAtUtc);
            account.LastLoginAtUtc = DateTimeOffset.UtcNow;
            account.UpdatedAtUtc = DateTimeOffset.UtcNow;
            SaveUnsafe(state);

            return new OperatorTokenExchangeResponse
            {
                Account = MapSummary(account),
                TokenInfo = MapToken(created.Record),
                Token = created.Secret
            };
        }
    }

    public OperatorAccountTokenCreateResponse? CreateToken(string id, OperatorAccountTokenCreateRequest request)
    {
        lock (_gate)
        {
            var state = LoadUnsafe();
            var account = state.Accounts.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal));
            if (account is null)
                return null;

            var created = CreateTokenUnsafe(account, request.Label, request.ExpiresAtUtc);
            account.UpdatedAtUtc = DateTimeOffset.UtcNow;
            SaveUnsafe(state);

            return new OperatorAccountTokenCreateResponse
            {
                Account = MapSummary(account),
                TokenInfo = MapToken(created.Record),
                Token = created.Secret
            };
        }
    }

    public bool RevokeToken(string accountId, string tokenId)
    {
        lock (_gate)
        {
            var state = LoadUnsafe();
            var account = state.Accounts.FirstOrDefault(item => string.Equals(item.Id, accountId, StringComparison.Ordinal));
            var token = account?.Tokens.FirstOrDefault(item => string.Equals(item.Id, tokenId, StringComparison.Ordinal));
            if (token is null)
                return false;

            if (token.RevokedAtUtc is null)
            {
                token.RevokedAtUtc = DateTimeOffset.UtcNow;
                if (account is not null)
                    account.UpdatedAtUtc = DateTimeOffset.UtcNow;
                SaveUnsafe(state);
            }

            return true;
        }
    }

    public bool TryAuthenticatePassword(string username, string password, out OperatorIdentitySnapshot? identity)
    {
        identity = null;
        var normalizedUsername = NormalizeUsername(username);

        lock (_gate)
        {
            var state = LoadUnsafe();
            var account = state.Accounts.FirstOrDefault(item => string.Equals(item.Username, normalizedUsername, StringComparison.OrdinalIgnoreCase));
            if (account is null || !account.Enabled)
                return false;

            if (!SecretMatches(NormalizePassword(password), account.PasswordSalt, account.PasswordHash))
                return false;

            account.LastLoginAtUtc = DateTimeOffset.UtcNow;
            account.UpdatedAtUtc = DateTimeOffset.UtcNow;
            SaveUnsafe(state);
            identity = MapIdentity(account, OrganizationAuthModeNames.BrowserSession);
            return true;
        }
    }

    public bool TryAuthenticateToken(string token, out OperatorIdentitySnapshot? identity)
    {
        identity = null;
        if (string.IsNullOrWhiteSpace(token))
            return false;

        lock (_gate)
        {
            var state = LoadUnsafe();
            foreach (var account in state.Accounts)
            {
                if (!account.Enabled)
                    continue;

                foreach (var tokenRecord in account.Tokens)
                {
                    if (tokenRecord.RevokedAtUtc is not null)
                        continue;
                    if (tokenRecord.ExpiresAtUtc is not null && tokenRecord.ExpiresAtUtc <= DateTimeOffset.UtcNow)
                        continue;
                    if (!SecretMatches(token, tokenRecord.SecretSalt, tokenRecord.SecretHash))
                        continue;

                    account.LastLoginAtUtc = DateTimeOffset.UtcNow;
                    account.UpdatedAtUtc = DateTimeOffset.UtcNow;
                    SaveUnsafe(state);
                    identity = MapIdentity(account, OrganizationAuthModeNames.AccountToken);
                    return true;
                }
            }
        }

        return false;
    }

    private StoreState LoadUnsafe()
    {
        if (_cached is not null)
            return _cached;

        if (!AtomicJsonFileStore.TryLoad(_path, GatewayJsonContext.Default.OperatorAccountStoreState, out StoreState? loaded, out var error))
        {
            _logger.LogWarning("Failed to load operator accounts from {Path}: {Error}", _path, error);
        }

        _cached = loaded ?? new StoreState();
        return _cached;
    }

    private void SaveUnsafe(StoreState state)
    {
        if (!AtomicJsonFileStore.TryWriteAtomic(_path, state, GatewayJsonContext.Default.OperatorAccountStoreState, out var error))
            throw new InvalidOperationException($"Failed to persist operator accounts: {error}");

        _cached = state;
    }

    private static OperatorAccountSummary MapSummary(StoredAccount account)
        => new()
        {
            Id = account.Id,
            Username = account.Username,
            DisplayName = account.DisplayName,
            Role = OperatorRoleNames.Normalize(account.Role),
            Enabled = account.Enabled,
            CreatedAtUtc = account.CreatedAtUtc,
            UpdatedAtUtc = account.UpdatedAtUtc,
            LastLoginAtUtc = account.LastLoginAtUtc,
            TokenCount = account.Tokens.Count(static token => token.RevokedAtUtc is null)
        };

    private static OperatorAccountTokenSummary MapToken(StoredToken token)
        => new()
        {
            Id = token.Id,
            Label = token.Label,
            TokenPrefix = token.TokenPrefix,
            CreatedAtUtc = token.CreatedAtUtc,
            ExpiresAtUtc = token.ExpiresAtUtc,
            RevokedAtUtc = token.RevokedAtUtc
        };

    private static OperatorIdentitySnapshot MapIdentity(StoredAccount account, string authMode)
        => new()
        {
            AuthMode = authMode,
            Role = OperatorRoleNames.Normalize(account.Role),
            AccountId = account.Id,
            Username = account.Username,
            DisplayName = account.DisplayName,
            IsBootstrapAdmin = false
        };

    private static CreatedToken CreateTokenUnsafe(StoredAccount account, string? label, DateTimeOffset? expiresAtUtc)
    {
        var token = $"oca_{Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant()}";
        var salt = GenerateSalt();
        var tokenRecord = new StoredToken
        {
            Id = $"optok_{Guid.NewGuid():N}"[..20],
            Label = label?.Trim() ?? "",
            TokenPrefix = token[..Math.Min(token.Length, 12)],
            SecretSalt = salt,
            SecretHash = HashSecret(token, salt),
            CreatedAtUtc = DateTimeOffset.UtcNow,
            ExpiresAtUtc = expiresAtUtc
        };

        account.Tokens.Add(tokenRecord);
        return new CreatedToken(token, tokenRecord);
    }

    private static string NormalizeUsername(string? username)
    {
        var normalized = username?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            throw new InvalidOperationException("username is required.");

        return normalized.ToLowerInvariant();
    }

    private static string NormalizePassword(string? password)
    {
        var normalized = password?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            throw new InvalidOperationException("password is required.");

        return normalized;
    }

    private static string GenerateSalt()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(SaltBytes));

    private static string HashSecret(string secret, string saltHex)
    {
        var secretBytes = System.Text.Encoding.UTF8.GetBytes(secret);
        var saltBytes = Convert.FromHexString(saltHex);
        return Convert.ToHexString(Rfc2898DeriveBytes.Pbkdf2(secretBytes, saltBytes, Pbkdf2Iterations, HashAlgorithmName.SHA256, HashBytes));
    }

    private static bool SecretMatches(string candidate, string saltHex, string expectedHashHex)
    {
        var actual = Convert.FromHexString(HashSecret(candidate, saltHex));
        var expected = Convert.FromHexString(expectedHashHex);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private sealed record CreatedToken(string Secret, StoredToken Record);
}
