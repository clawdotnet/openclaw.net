using System.Net;
using System.Text.Json;
using OpenClaw.Core.Models;
using OpenClaw.Core.Security;
using OpenClaw.Payments.Abstractions;
using OpenClaw.Payments.Core;
using OpenClaw.Payments.StripeLink;
using OpenClaw.Plugins.Payment;
using Xunit;

namespace OpenClaw.Tests;

public sealed class PaymentRuntimeTests
{
    [Fact]
    public void PaymentRedactor_RedactsPanCvvHeadersAndKeepsLast4()
    {
        var redactor = new PaymentSensitiveDataRedactor();

        var text = "card=4242 4242 4242 4242 cvv: 123 Authorization: Payment payment_secret_token_123456 last4=4242";
        var redacted = redactor.Redact(text);

        Assert.DoesNotContain("4242 4242 4242 4242", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("cvv: 123", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("payment_secret_token_123456", redacted, StringComparison.Ordinal);
        Assert.Contains("last4=4242", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InMemoryVault_EnforcesRetrieveOnceTtlAndRevoke()
    {
        var vault = new InMemoryPaymentSecretVault();
        var secret = new PaymentSecret("h1", "mock", pan: "4242424242424242", cvv: "123");

        await vault.StoreAsync(secret, TimeSpan.FromMinutes(1), retrieveOnce: true, CancellationToken.None);

        Assert.NotNull(await vault.TryRetrieveAsync("h1", "test", CancellationToken.None));
        Assert.Null(await vault.TryRetrieveAsync("h1", "test", CancellationToken.None));

        await vault.StoreAsync(new PaymentSecret("h2", "mock", pan: "4242424242424242"), TimeSpan.FromMilliseconds(1), retrieveOnce: false, CancellationToken.None);
        await Task.Delay(20);
        Assert.Null(await vault.TryRetrieveAsync("h2", "test", CancellationToken.None));

        await vault.StoreAsync(new PaymentSecret("h3", "mock", pan: "4242424242424242"), TimeSpan.FromMinutes(1), retrieveOnce: false, CancellationToken.None);
        await vault.RevokeAsync("h3", "test", CancellationToken.None);
        Assert.Null(await vault.TryRetrieveAsync("h3", "test", CancellationToken.None));
    }

    [Fact]
    public void PaymentSecret_CannotBeSerialized()
    {
        var secret = new PaymentSecret("h1", "mock", pan: "4242424242424242");

        Assert.Throws<JsonException>(() => JsonSerializer.Serialize(secret));
    }

    [Fact]
    public async Task PaymentTool_IssuesSafeVirtualCardHandleOnly()
    {
        var runtime = CreateRuntime();
        var tool = new PaymentTool(runtime, "mock", PaymentEnvironments.Test);

        var result = await tool.ExecuteAsync(
            """{"action":"issue_virtual_card","merchant":"Example Store","amount_minor":1200,"currency":"USD","environment":"test"}""",
            CancellationToken.None);

        Assert.Contains("pvh_mock_", result, StringComparison.Ordinal);
        Assert.Contains("4242", result, StringComparison.Ordinal);
        Assert.DoesNotContain("4242424242424242", result, StringComparison.Ordinal);
        Assert.DoesNotContain("\"cvv\"", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("123", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LiveVirtualCard_RequiresApprovalAndDenialBlocksExecution()
    {
        var runtime = CreateRuntime(new StaticPaymentApprovalService(false));

        await Assert.ThrowsAsync<PaymentPolicyDeniedException>(async () =>
            await runtime.IssueVirtualCardAsync(new VirtualCardRequest
            {
                MerchantName = "Live Merchant",
                AmountMinor = 500,
                Currency = "USD",
                Environment = PaymentEnvironments.Live
            }, new PaymentExecutionContext { Environment = PaymentEnvironments.Live }, CancellationToken.None));
    }

    [Fact]
    public async Task SentinelSubstitution_ResolvesOnlyExecutionBoundaryAndRedactsPersistence()
    {
        var runtime = CreateRuntime(new StaticPaymentApprovalService(true));
        var handle = await runtime.IssueVirtualCardAsync(new VirtualCardRequest
        {
            MerchantName = "Example Store",
            AmountMinor = 100,
            Currency = "USD",
            Environment = PaymentEnvironments.Test
        }, new PaymentExecutionContext { Environment = PaymentEnvironments.Test }, CancellationToken.None);
        var substitution = new PaymentSentinelSubstitutionService(runtime);

        var result = await substitution.SubstituteAsync(new SentinelSubstitutionContext
        {
            ToolName = "browser",
            ArgumentsJson = "{\"action\":\"fill\",\"selector\":\"#card\",\"value\":\"{{payment.vcard:" + handle.HandleId + ":pan}}\"}",
            SessionId = "s1",
            ChannelId = "web",
            SenderId = "u1"
        }, CancellationToken.None);

        Assert.True(result.Substituted);
        Assert.Contains("4242424242424242", result.ExecutionArgumentsJson, StringComparison.Ordinal);
        Assert.Contains("payment.vcard", result.PersistedArgumentsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("4242424242424242", result.PersistedArgumentsJson, StringComparison.Ordinal);

        var redacted = new PaymentSensitiveDataRedactor().Redact(result.ExecutionArgumentsJson);
        Assert.DoesNotContain("4242424242424242", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MachinePayment_ResultDoesNotLeakScopedTokenAndTokenIsRetrieveOnce()
    {
        var runtime = CreateRuntime();

        var result = await runtime.ExecuteMachinePaymentAsync(new MachinePaymentRequest
        {
            ProviderId = "mock",
            Environment = PaymentEnvironments.Test,
            Challenge = new MachinePaymentChallenge
            {
                ChallengeId = "ch_1",
                MerchantName = "Paid API",
                AmountMinor = 42,
                Currency = "USD"
            }
        }, new PaymentExecutionContext { Environment = PaymentEnvironments.Test }, CancellationToken.None);

        var json = JsonSerializer.Serialize(result, PaymentJsonContext.Default.MachinePaymentResult);
        Assert.DoesNotContain("payment_mock_secret_token", json, StringComparison.Ordinal);

        var secret = await runtime.RetrieveMachineAuthorizationOnceAsync(result.PaymentId, CancellationToken.None);
        Assert.NotNull(secret);
        Assert.Contains("payment_mock_secret_token", secret!.Resolve(PaymentSecretField.AuthorizationHeader), StringComparison.Ordinal);
        Assert.Null(await runtime.RetrieveMachineAuthorizationOnceAsync(result.PaymentId, CancellationToken.None));
    }

    [Fact]
    public async Task StripeLinkSetupStatus_IsCleanWhenCliMissing()
    {
        var provider = new StripeLinkPaymentProvider(
            new StripeLinkOptions { CliPath = "missing-link-cli" },
            new StaticLinkRunner(new LinkCliCommandResult { ExitCode = -1, Stderr = "not found" }));

        var status = await provider.GetSetupStatusAsync(CancellationToken.None);

        Assert.False(status.Installed);
        Assert.Equal("not_installed", status.Status);
        Assert.DoesNotContain("pan", JsonSerializer.Serialize(status, PaymentJsonContext.Default.PaymentSetupStatus), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LinkCliProcessRunner_UsesArgumentListWithoutShellExpansion()
    {
        var runner = new LinkCliProcessRunner();

        var result = await runner.RunAsync(
            "/bin/echo",
            ["hello;echo injected"],
            workingDirectory: null,
            environment: new Dictionary<string, string>(),
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("hello;echo injected", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("injected\ninjected", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PaymentAwareHttpHandler_DoesNotExposeAuthorizationToken()
    {
        var runtime = CreateRuntime(new StaticPaymentApprovalService(true));
        var handler = new PaymentAwareHttpHandler(
            runtime,
            new PaymentExecutionContext { Environment = PaymentEnvironments.Test },
            "mock",
            new ChallengeThenOkHandler());
        using var client = new HttpClient(handler);

        var response = await client.GetAsync("https://example.test/paid");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("paid", body);
    }

    private static PaymentRuntimeService CreateRuntime(IPaymentApprovalService? approval = null)
        => new(
            [new MockPaymentProvider()],
            new InMemoryPaymentSecretVault(),
            new DefaultPaymentPolicy(),
            new InMemoryPaymentAuditSink(),
            approval,
            defaultProviderId: "mock");

    private sealed class StaticPaymentApprovalService : IPaymentApprovalService
    {
        private readonly bool _approved;

        public StaticPaymentApprovalService(bool approved)
        {
            _approved = approved;
        }

        public ValueTask<ApprovalResult> RequestApprovalAsync(ApprovalRequest request, CancellationToken ct)
            => ValueTask.FromResult(new ApprovalResult { Approved = _approved, Source = "test" });
    }

    private sealed class StaticLinkRunner : ILinkCliCommandRunner
    {
        private readonly LinkCliCommandResult _result;

        public StaticLinkRunner(LinkCliCommandResult result)
        {
            _result = result;
        }

        public ValueTask<LinkCliCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, string? workingDirectory, IReadOnlyDictionary<string, string> environment, TimeSpan timeout, CancellationToken ct)
            => ValueTask.FromResult(_result);
    }

    private sealed class ChallengeThenOkHandler : HttpMessageHandler
    {
        private int _count;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _count) == 1)
            {
                var challenge = new HttpResponseMessage(HttpStatusCode.PaymentRequired)
                {
                    Content = new StringContent("challenge=ch_1;merchant=Paid API;amount=42;currency=USD")
                };
                return Task.FromResult(challenge);
            }

            Assert.Equal("Payment", request.Headers.Authorization?.Scheme);
            Assert.DoesNotContain("payment_mock_secret_token", request.RequestUri!.ToString(), StringComparison.Ordinal);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("paid")
            });
        }
    }
}
