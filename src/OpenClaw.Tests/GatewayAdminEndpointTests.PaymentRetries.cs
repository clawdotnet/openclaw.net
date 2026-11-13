using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using OpenClaw.Core.Models;
using Xunit;

namespace OpenClaw.Tests;

public sealed partial class GatewayAdminEndpointTests
{
    [Fact]
    public async Task PaymentExecute_WhenRetried_ShouldReplayAndRejectConflictingReuse()
    {
        await using var harness = await CreateHarnessAsync(true, config => config.Payments.Enabled = true);
        harness.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-admin-token");
        const string body = """{"idempotencyKey":"order-1","challenge":{"merchantName":"fixture","amountMinor":50,"currency":"USD"}}""";
        using var first = await harness.Client.PostAsync("/api/integration/payment/execute", JsonContent(body));
        Assert.True(first.StatusCode == HttpStatusCode.OK, await first.Content.ReadAsStringAsync());
        var firstBody = await first.Content.ReadAsStringAsync();
        using var retry = await harness.Client.PostAsync("/api/integration/payment/execute?senderId=changed", JsonContent(body));
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(firstBody, await retry.Content.ReadAsStringAsync());
        using var conflict = await harness.Client.PostAsync("/api/integration/payment/execute", JsonContent(body.Replace(":50", ":51")));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Contains("idempotency_conflict", await conflict.Content.ReadAsStringAsync());
        using var missingKey = await harness.Client.PostAsync("/api/integration/payment/execute", JsonContent("""{"challenge":{"amountMinor":50,"currency":"USD"}}"""));
        Assert.Equal(HttpStatusCode.BadRequest, missingKey.StatusCode);
    }

    [Fact]
    public async Task PaymentExecute_WhenDifferentAccountsReuseKey_ShouldKeepTransactionsSeparate()
    {
        await using var harness = await CreateHarnessAsync(true, config => config.Payments.Enabled = true);
        var one = CreateOperatorToken(harness, OperatorRoleNames.Operator, "payer-one");
        var two = CreateOperatorToken(harness, OperatorRoleNames.Operator, "payer-two");
        const string body = """{"idempotencyKey":"order-1","challenge":{"amountMinor":50,"currency":"USD"}}""";
        harness.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", one);
        using var first = await harness.Client.PostAsync("/api/integration/payment/execute", JsonContent(body));
        Assert.True(first.StatusCode == HttpStatusCode.OK, await first.Content.ReadAsStringAsync());
        harness.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", two);
        using var second = await harness.Client.PostAsync("/api/integration/payment/execute", JsonContent(body));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        using var firstJson = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        using var secondJson = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        Assert.NotEqual(firstJson.RootElement.GetProperty("paymentId").GetString(), secondJson.RootElement.GetProperty("paymentId").GetString());
    }
}
