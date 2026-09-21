using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Gateway;
using Xunit;
namespace OpenClaw.Tests;
public sealed class DeviceEnrollmentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "enrollment-" + Guid.NewGuid().ToString("N"));
    private sealed class Clock : TimeProvider { public DateTimeOffset Now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }
    private (OperatorAccountService Accounts, string Id, DeviceEnrollmentService Service, Clock Clock) Setup()
    {
        var accounts = new OperatorAccountService(_root, NullLogger<OperatorAccountService>.Instance);
        var account = accounts.Create(new OperatorAccountCreateRequest { Username = "owner", Password = "long-enough-test-password", Role = "operator" });
        var clock = new Clock(); return (accounts, account.Id, new(accounts, clock), clock);
    }
    [Fact]
    public void OneUseCodeIssuesRevocableTokenWithAccountRole()
    {
        var (accounts, id, service, _) = Setup();
        var code = service.Create(new(id, "Laptop"));
        var result = service.Exchange(code.Code);
        Assert.NotNull(result); Assert.Null(service.Exchange(code.Code));
        Assert.True(accounts.TryAuthenticateToken(result.Token, out var identity));
        Assert.Equal("operator", identity!.Role);
        Assert.Equal("device:Laptop", result.TokenInfo!.Label);
        Assert.True(accounts.RevokeToken(id, result.TokenInfo.Id));
        Assert.False(accounts.TryAuthenticateToken(result.Token, out _));
    }
    [Fact]
    public void ExpiredOrDisabledAccountCannotRedeem()
    {
        var (accounts, id, service, clock) = Setup();
        var code = service.Create(new(id, "Laptop")); clock.Now += TimeSpan.FromMinutes(6);
        Assert.Null(service.Exchange(code.Code));
        code = service.Create(new(id, "Laptop"));
        accounts.Update(id, new OperatorAccountUpdateRequest { Enabled = false });
        Assert.Null(service.Exchange(code.Code));
    }
    [Fact]
    public async Task ConcurrentRedemptionCreatesOnlyOneToken()
    {
        var (_, id, service, _) = Setup(); var code = service.Create(new(id, "Laptop"));
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(() => service.Exchange(code.Code))));
        Assert.Single(results, x => x is not null);
    }
    [Fact]
    public void ExchangeIsRateLimitedAndRestartInvalidatesPendingCodes()
    {
        var (accounts, id, service, clock) = Setup();
        var code = service.Create(new(id, "Laptop"));
        Assert.Null(new DeviceEnrollmentService(accounts, clock).Exchange(code.Code));
        for (var i = 0; i < 30; i++) Assert.Null(service.Exchange("invalid"));
        Assert.Throws<InvalidOperationException>(() => service.Exchange(code.Code));
        clock.Now += TimeSpan.FromMinutes(1);
        Assert.NotNull(service.Exchange(code.Code));
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
