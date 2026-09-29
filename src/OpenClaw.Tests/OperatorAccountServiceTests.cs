using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Gateway;
using Xunit;

namespace OpenClaw.Tests;

public sealed class OperatorAccountServiceTests : IDisposable
{
    private readonly string _storagePath = Path.Combine(Path.GetTempPath(), "openclaw-operator-account-tests", Guid.NewGuid().ToString("N"));
    private readonly Clock _clock = new();
    private int _hashes;

    public void Dispose()
    {
        try { Directory.Delete(_storagePath, recursive: true); }
        catch { }
    }

    [Fact]
    public void TryAuthenticateToken_WhenSameTokenIsPresentedAgain_DoesNotHashItAgain()
    {
        var (accounts, _, token) = CreateOperatorToken();
        Assert.True(accounts.TryAuthenticateToken(token.Token, out _));
        var hashesAfterFirstUse = _hashes;

        Assert.True(accounts.TryAuthenticateToken(token.Token, out var identity));

        Assert.Equal(hashesAfterFirstUse, _hashes);
        Assert.Equal(OperatorRoleNames.Operator, identity!.Role);
    }

    [Fact]
    public void TryAuthenticateToken_WhenVerifiedTokenIsRevoked_RejectsIt()
    {
        var (accounts, accountId, token) = CreateOperatorToken();
        Assert.True(accounts.TryAuthenticateToken(token.Token, out _));

        Assert.True(accounts.RevokeToken(accountId, token.TokenInfo!.Id));

        Assert.False(accounts.TryAuthenticateToken(token.Token, out _));
    }

    [Fact]
    public void TryAuthenticateToken_WhenVerifiedTokenExpires_RejectsIt()
    {
        var (accounts, accountId, _) = CreateOperatorToken();
        var token = accounts.CreateToken(accountId, new OperatorAccountTokenCreateRequest { ExpiresAtUtc = _clock.Now.AddMinutes(5) })!;
        Assert.True(accounts.TryAuthenticateToken(token.Token, out _));

        _clock.Now = _clock.Now.AddMinutes(6);

        Assert.False(accounts.TryAuthenticateToken(token.Token, out _));
    }

    [Fact]
    public void TryAuthenticateToken_WhenAccountIsDisabledAfterVerification_RejectsIt()
    {
        var (accounts, accountId, token) = CreateOperatorToken();
        Assert.True(accounts.TryAuthenticateToken(token.Token, out _));

        accounts.Update(accountId, new OperatorAccountUpdateRequest { Enabled = false });

        Assert.False(accounts.TryAuthenticateToken(token.Token, out _));
    }

    [Fact]
    public void TryAuthenticateToken_WhenAccountIsDeletedAfterVerification_RejectsIt()
    {
        var (accounts, accountId, token) = CreateOperatorToken();
        Assert.True(accounts.TryAuthenticateToken(token.Token, out _));

        Assert.True(accounts.Delete(accountId));

        Assert.False(accounts.TryAuthenticateToken(token.Token, out _));
    }

    [Fact]
    public void TryAuthenticateToken_WhenRoleChangesAfterVerification_ReturnsCurrentRole()
    {
        var (accounts, accountId, token) = CreateOperatorToken();
        Assert.True(accounts.TryAuthenticateToken(token.Token, out _));

        accounts.Update(accountId, new OperatorAccountUpdateRequest { Role = OperatorRoleNames.Viewer });

        Assert.True(accounts.TryAuthenticateToken(token.Token, out var identity));
        Assert.Equal(OperatorRoleNames.Viewer, identity!.Role);
    }

    [Fact]
    public void TryAuthenticateToken_WhenTokenDiffersFromVerifiedOne_RejectsIt()
    {
        var (accounts, _, token) = CreateOperatorToken();
        Assert.True(accounts.TryAuthenticateToken(token.Token, out _));

        // Same prefix, different secret.
        var forged = token.Token[..^1] + (token.Token[^1] == '0' ? '1' : '0');

        Assert.False(accounts.TryAuthenticateToken(forged, out _));
    }

    [Fact]
    public void TryAuthenticateToken_WithinLastLoginInterval_DoesNotRewriteLastLogin()
    {
        var (accounts, accountId, token) = CreateOperatorToken();
        Assert.True(accounts.TryAuthenticateToken(token.Token, out _));
        var firstUse = PersistedLastLogin(accountId);

        _clock.Now = _clock.Now.AddSeconds(30);
        Assert.True(accounts.TryAuthenticateToken(token.Token, out _));

        Assert.Equal(firstUse, PersistedLastLogin(accountId));
    }

    [Fact]
    public void TryAuthenticateToken_AfterLastLoginInterval_RecordsNewLastLogin()
    {
        var (accounts, accountId, token) = CreateOperatorToken();
        Assert.True(accounts.TryAuthenticateToken(token.Token, out _));

        _clock.Now = _clock.Now.AddMinutes(2);
        Assert.True(accounts.TryAuthenticateToken(token.Token, out _));

        Assert.Equal(_clock.Now, PersistedLastLogin(accountId));
    }

    [Fact]
    public void TryAuthenticateToken_WhenLastLoginWriteFails_TriesAgainOnTheNextRequest()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Uses Unix directory permissions to make the accounts file unwritable.");
            return;
        }

        var (accounts, _, token) = CreateOperatorToken();
        Assert.True(accounts.TryAuthenticateToken(token.Token, out _));
        _clock.Now = _clock.Now.AddMinutes(2);

        var adminDirectory = Path.Combine(_storagePath, "admin");
        var originalMode = File.GetUnixFileMode(adminDirectory);
        File.SetUnixFileMode(adminDirectory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            if (DirectoryIsWritable(adminDirectory))
                Assert.Skip("Directory permissions are not enforced for this user.");

            Assert.Throws<InvalidOperationException>(() => accounts.TryAuthenticateToken(token.Token, out _));

            // A write that failed must not count as recorded, or requests for the next minute would skip it.
            Assert.Throws<InvalidOperationException>(() => accounts.TryAuthenticateToken(token.Token, out _));
        }
        finally
        {
            File.SetUnixFileMode(adminDirectory, originalMode);
        }
    }

    private (OperatorAccountService Accounts, string AccountId, OperatorAccountTokenCreateResponse Token) CreateOperatorToken()
    {
        var accounts = CreateService();
        var account = accounts.Create(new OperatorAccountCreateRequest
        {
            Username = "cache-operator",
            Password = "P@ssw0rd123!",
            Role = OperatorRoleNames.Operator
        });
        return (accounts, account.Id, accounts.CreateToken(account.Id, new OperatorAccountTokenCreateRequest { Label = "cache" })!);
    }

    private static bool DirectoryIsWritable(string directory)
    {
        var probe = Path.Combine(directory, $".probe-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    // A fresh instance reads what was written to disk, not the first instance's in-memory state.
    private DateTimeOffset? PersistedLastLogin(string accountId)
        => CreateService().Get(accountId)!.Account!.LastLoginAtUtc;

    // A fast stand-in for PBKDF2 that counts how often a secret is hashed.
    private OperatorAccountService CreateService()
        => new(_storagePath, NullLogger<OperatorAccountService>.Instance, _clock, (secret, saltHex) =>
        {
            Interlocked.Increment(ref _hashes);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(saltHex + secret)));
        });

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
