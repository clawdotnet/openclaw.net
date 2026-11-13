using System.Text.Json;
using NSubstitute;
using OpenClaw.Payments.Abstractions;
using OpenClaw.Payments.Core;
using OpenClaw.Payments.StripeLink;
using OpenClaw.Plugins.Payment;
using Xunit;

namespace OpenClaw.Tests;

public sealed class PaymentIdempotencyTests : IDisposable
{
    private readonly string _directory = Path.Join(Path.GetTempPath(), "openclaw-payment-retries", Guid.NewGuid().ToString("N"));
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ExecuteMachinePayment_WhenRetriedAfterRestart_ShouldReplayWithoutDispatchOrReplenishingSecret()
    {
        var provider = CreateProvider();
        var first = CreateRuntime(provider);
        var result = await first.ExecuteMachinePaymentAsync(Request(), new(), Ct);
        Assert.NotNull(await first.RetrieveMachineAuthorizationOnceAsync(result.PaymentId, Ct));
        var replay = await first.ExecuteMachinePaymentAsync(Request(), new(), Ct);
        Assert.Equal(JsonSerializer.Serialize(result, PaymentJsonContext.Default.MachinePaymentResult),
            JsonSerializer.Serialize(replay, PaymentJsonContext.Default.MachinePaymentResult));
        Assert.Null(await first.RetrieveMachineAuthorizationOnceAsync(result.PaymentId, Ct));
        var restarted = CreateRuntime(provider);
        Assert.Equal(result.PaymentId, (await restarted.ExecuteMachinePaymentAsync(Request(), new(), Ct)).PaymentId);
        Assert.Null(await restarted.RetrieveMachineAuthorizationOnceAsync(result.PaymentId, Ct));
        await provider.Received(1).ExecuteMachinePaymentAsync(Arg.Any<MachinePaymentRequest>(), Arg.Any<PaymentExecutionContext>(), Arg.Any<CancellationToken>());
        Assert.DoesNotContain("private-token", File.ReadAllText(Assert.Single(Directory.GetFiles(_directory, "*.json"))));
    }

    [Theory]
    [InlineData("amount")]
    [InlineData("currency")]
    [InlineData("resource")]
    [InlineData("funding")]
    [InlineData("challenge")]
    [InlineData("metadata")]
    public async Task ExecuteMachinePayment_WhenKeyReusedForDifferentRequest_ShouldRejectConflict(string change)
    {
        var provider = CreateProvider();
        var runtime = CreateRuntime(provider);
        var request = Request();
        await runtime.ExecuteMachinePaymentAsync(request, new(), Ct);
        var changed = change switch
        {
            "amount" => request with { Challenge = request.Challenge with { AmountMinor = 51 } },
            "currency" => request with { Challenge = request.Challenge with { Currency = "EUR" } },
            "resource" => request with { Challenge = request.Challenge with { ResourceUrl = "https://different.example/resource" } },
            "funding" => request with { FundingSourceId = "other" },
            "challenge" => request with { Challenge = request.Challenge with { ChallengeId = "other" } },
            _ => request with { Challenge = request.Challenge with { SafeMetadata = new() { ["order"] = "other" } } }
        };
        var error = await Assert.ThrowsAsync<PaymentIdempotencyException>(() => runtime.ExecuteMachinePaymentAsync(changed, new(), Ct).AsTask());
        Assert.Equal("idempotency_conflict", error.Code);
        await provider.Received(1).ExecuteMachinePaymentAsync(Arg.Any<MachinePaymentRequest>(), Arg.Any<PaymentExecutionContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteMachinePayment_WhenConcurrentRuntimesUseSameKey_ShouldDispatchOnce()
    {
        var provider = CreateProvider();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.ExecuteMachinePaymentAsync(Arg.Any<MachinePaymentRequest>(), Arg.Any<PaymentExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask<MachinePaymentProviderResult>(WaitAsync()));
        async Task<MachinePaymentProviderResult> WaitAsync()
        {
            started.SetResult();
            await release.Task.WaitAsync(Ct);
            return Result();
        }
        var initial = CreateRuntime(provider).ExecuteMachinePaymentAsync(Request(), new(), Ct).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        try
        {
            var attempts = Enumerable.Range(0, 16).Select(async _ =>
                await Assert.ThrowsAsync<PaymentIdempotencyException>(() =>
                    CreateRuntime(provider).ExecuteMachinePaymentAsync(Request(), new(), Ct).AsTask()));
            Assert.All(await Task.WhenAll(attempts), error => Assert.Equal("payment_outcome_unknown", error.Code));
        }
        finally
        {
            release.TrySetResult();
        }
        await initial;
        Assert.Equal("pay-1", (await CreateRuntime(provider).ExecuteMachinePaymentAsync(Request(), new(), Ct)).PaymentId);
        await provider.Received(1).ExecuteMachinePaymentAsync(Arg.Any<MachinePaymentRequest>(), Arg.Any<PaymentExecutionContext>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteMachinePayment_WhenProviderOutcomeUnknown_ShouldRetainReservationAcrossRestart(bool canceled)
    {
        var provider = CreateProvider();
        provider.ExecuteMachinePaymentAsync(Arg.Any<MachinePaymentRequest>(), Arg.Any<PaymentExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(_ => ValueTask.FromException<MachinePaymentProviderResult>(canceled ? new OperationCanceledException() : new TimeoutException()));
        if (canceled)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateRuntime(provider).ExecuteMachinePaymentAsync(Request(), new(), Ct).AsTask());
        else
            Assert.Equal("payment_outcome_unknown", (await Assert.ThrowsAsync<PaymentIdempotencyException>(() =>
                CreateRuntime(provider).ExecuteMachinePaymentAsync(Request(), new(), Ct).AsTask())).Code);
        var error = await Assert.ThrowsAsync<PaymentIdempotencyException>(() => CreateRuntime(provider).ExecuteMachinePaymentAsync(Request(), new(), Ct).AsTask());
        Assert.Equal("payment_outcome_unknown", error.Code);
        await provider.Received(1).ExecuteMachinePaymentAsync(Arg.Any<MachinePaymentRequest>(), Arg.Any<PaymentExecutionContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteMachinePayment_WhenCallerOrModeDiffers_ShouldUseSeparateScope()
    {
        var provider = CreateProvider();
        var runtime = CreateRuntime(provider);
        await runtime.ExecuteMachinePaymentAsync(Request(), new() { CallerId = "one" }, Ct);
        await runtime.ExecuteMachinePaymentAsync(Request(), new() { CallerId = "two" }, Ct);
        await runtime.ExecuteMachinePaymentAsync(Request() with { Environment = "live" }, new() { CallerId = "one", CliConfirmed = true }, Ct);
        await provider.Received(3).ExecuteMachinePaymentAsync(Arg.Any<MachinePaymentRequest>(), Arg.Any<PaymentExecutionContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteMachinePayment_WhenApprovalDenied_ShouldNotReserveKey()
    {
        var provider = CreateProvider();
        var runtime = CreateRuntime(provider);
        var request = Request() with { Environment = "live" };
        await Assert.ThrowsAsync<PaymentPolicyDeniedException>(() => runtime.ExecuteMachinePaymentAsync(request, new(), Ct).AsTask());
        Assert.False(Directory.Exists(_directory));
        Assert.Equal("pay-1", (await runtime.ExecuteMachinePaymentAsync(request, new() { CliConfirmed = true }, Ct)).PaymentId);
    }

    [Fact]
    public async Task ExecuteMachinePayment_WhenStripeModeMismatch_ShouldNotReserveKey()
    {
        var deniedRunner = Substitute.For<ILinkCliCommandRunner>();
        var deniedProvider = new StripeLinkPaymentProvider(new StripeLinkOptions { Mode = PaymentEnvironments.Test }, deniedRunner);
        var request = Request() with { ProviderId = deniedProvider.ProviderId, Environment = PaymentEnvironments.Live };

        await Assert.ThrowsAsync<PaymentPolicyDeniedException>(() =>
            CreateRuntime(deniedProvider).ExecuteMachinePaymentAsync(
                request, new() { Environment = PaymentEnvironments.Live, CliConfirmed = true }, Ct).AsTask());

        Assert.False(Directory.Exists(_directory));
        await deniedRunner.DidNotReceive().RunAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string?>(),
            Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteMachinePayment_WhenCompletedPaymentIsRetried_ShouldNotRequestApprovalAgain()
    {
        var provider = CreateProvider();
        var approval = Substitute.For<IPaymentApprovalService>();
        approval.RequestApprovalAsync(Arg.Any<ApprovalRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ApprovalResult { Approved = true, Source = "fixture" });
        var runtime = new PaymentRuntimeService([provider], new InMemoryPaymentSecretVault(),
            new DefaultPaymentPolicy(), new InMemoryPaymentAuditSink(), approval,
            defaultProviderId: provider.ProviderId, idempotencyStore: new FilePaymentIdempotencyStore(_directory));
        var request = Request() with { Environment = PaymentEnvironments.Live };
        var context = new PaymentExecutionContext { Environment = PaymentEnvironments.Live };

        await runtime.ExecuteMachinePaymentAsync(request, context, Ct);
        await runtime.ExecuteMachinePaymentAsync(request, context, Ct);

        await approval.Received(1).RequestApprovalAsync(Arg.Any<ApprovalRequest>(), Arg.Any<CancellationToken>());
        await provider.Received(1).ExecuteMachinePaymentAsync(
            Arg.Any<MachinePaymentRequest>(), Arg.Any<PaymentExecutionContext>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task ExecuteMachinePayment_WhenKeyMissing_ShouldRejectBeforeDispatch(string? key)
    {
        var provider = CreateProvider();
        await Assert.ThrowsAsync<ArgumentException>(() => CreateRuntime(provider).ExecuteMachinePaymentAsync(Request() with { IdempotencyKey = key }, new(), Ct).AsTask());
        await provider.DidNotReceive().ExecuteMachinePaymentAsync(Arg.Any<MachinePaymentRequest>(), Arg.Any<PaymentExecutionContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PaymentTool_WhenRetried_ShouldPassStableKeyToRuntime()
    {
        var provider = CreateProvider();
        var tool = new PaymentTool(CreateRuntime(provider));
        const string arguments = """{"action":"execute_machine_payment","idempotency_key":"order-1","amount_minor":50,"merchant":"fixture"}""";
        var first = await tool.ExecuteAsync(arguments, Ct);
        Assert.Equal(first, await tool.ExecuteAsync(arguments, Ct));
        await provider.Received(1).ExecuteMachinePaymentAsync(Arg.Is<MachinePaymentRequest>(r => r.IdempotencyKey == "order-1"), Arg.Any<PaymentExecutionContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteMachinePayment_WhenStripeCliRetried_ShouldRunCommandOnce()
    {
        var runner = Substitute.For<ILinkCliCommandRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new LinkCliCommandResult { ExitCode = 0, Stdout = """{"payment_id":"stripe-1","status":"completed"}""" });
        var provider = new StripeLinkPaymentProvider(new StripeLinkOptions(), runner);
        var request = Request() with { ProviderId = "stripe-link" };
        await CreateRuntime(provider).ExecuteMachinePaymentAsync(request, new(), Ct);
        await CreateRuntime(provider).ExecuteMachinePaymentAsync(request, new(), Ct);
        await runner.Received(1).RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteMachinePayment_WhenMetadataOrderChanges_ShouldReplayEquivalentRequest()
    {
        var provider = CreateProvider();
        var runtime = CreateRuntime(provider);
        var request = Request();
        var first = request with { Challenge = request.Challenge with { SafeMetadata = new() { ["a"] = "1", ["b"] = "2" } } };
        var reordered = request with { Challenge = request.Challenge with { SafeMetadata = new() { ["b"] = "2", ["a"] = "1" } } };
        await runtime.ExecuteMachinePaymentAsync(first, new(), Ct);
        await runtime.ExecuteMachinePaymentAsync(reordered, new(), Ct);
        await provider.Received(1).ExecuteMachinePaymentAsync(Arg.Any<MachinePaymentRequest>(), Arg.Any<PaymentExecutionContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteMachinePayment_WhenSavedRecordIsCorrupt_ShouldFailClosed()
    {
        var provider = CreateProvider();
        await CreateRuntime(provider).ExecuteMachinePaymentAsync(Request(), new(), Ct);
        await File.WriteAllTextAsync(Assert.Single(Directory.GetFiles(_directory, "*.json")), "invalid json", Ct);
        var error = await Assert.ThrowsAsync<PaymentIdempotencyException>(() => CreateRuntime(provider).ExecuteMachinePaymentAsync(Request(), new(), Ct).AsTask());
        Assert.Equal("payment_outcome_unknown", error.Code);
        await provider.Received(1).ExecuteMachinePaymentAsync(Arg.Any<MachinePaymentRequest>(), Arg.Any<PaymentExecutionContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteMachinePayment_WhenCallerDisconnectsAfterProviderCompletes_ShouldPersistResultBeforePostProcessing()
    {
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var provider = CreateProvider();
        var vault = new InMemoryPaymentSecretVault();
        provider.ExecuteMachinePaymentAsync(Arg.Any<MachinePaymentRequest>(), Arg.Any<PaymentExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(_ => { caller.Cancel(); return Result(); });
        var runtime = new PaymentRuntimeService([provider], vault, new DefaultPaymentPolicy(),
            new InMemoryPaymentAuditSink(), defaultProviderId: provider.ProviderId,
            idempotencyStore: new FilePaymentIdempotencyStore(_directory));
        try
        {
            await runtime.ExecuteMachinePaymentAsync(Request(), new(), caller.Token);
        }
        catch (OperationCanceledException)
        {
            // Post-payment audit work may observe the caller's cancellation.
        }
        Assert.NotNull(await vault.TryRetrieveAsync("pay-1", "test", Ct));
        Assert.Equal("pay-1", (await CreateRuntime(provider).ExecuteMachinePaymentAsync(Request(), new(), Ct)).PaymentId);
        await provider.Received(1).ExecuteMachinePaymentAsync(Arg.Any<MachinePaymentRequest>(), Arg.Any<PaymentExecutionContext>(), Arg.Any<CancellationToken>());
    }

    private PaymentRuntimeService CreateRuntime(IPaymentProvider provider)
        => new([provider], new InMemoryPaymentSecretVault(), new DefaultPaymentPolicy(), new InMemoryPaymentAuditSink(),
            defaultProviderId: provider.ProviderId, idempotencyStore: new FilePaymentIdempotencyStore(_directory));

    private static IPaymentProvider CreateProvider()
    {
        var provider = Substitute.For<IPaymentProvider>();
        provider.ProviderId.Returns("mock");
        provider.ExecuteMachinePaymentAsync(Arg.Any<MachinePaymentRequest>(), Arg.Any<PaymentExecutionContext>(), Arg.Any<CancellationToken>()).Returns(_ => Result());
        return provider;
    }

    private static MachinePaymentProviderResult Result() => new()
    {
        Result = new MachinePaymentResult { PaymentId = "pay-1", ProviderId = "mock", AmountMinor = 50, Status = "completed" },
        ScopedAuthorizationSecret = new PaymentSecret("pay-1", "mock", authorizationHeader: "Payment private-token")
    };

    private static MachinePaymentRequest Request() => new()
    {
        IdempotencyKey = "order-1",
        Challenge = new MachinePaymentChallenge { ChallengeId = "challenge-1", AmountMinor = 50, Currency = "USD", ResourceUrl = "https://example.test/paid" }
    };

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
