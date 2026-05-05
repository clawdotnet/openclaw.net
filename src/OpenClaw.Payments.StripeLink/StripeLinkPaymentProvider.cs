using System.Text.Json;
using OpenClaw.Payments.Abstractions;

namespace OpenClaw.Payments.StripeLink;

public sealed class StripeLinkPaymentProvider : IPaymentProvider
{
    private readonly StripeLinkOptions _options;
    private readonly ILinkCliCommandRunner _runner;

    public StripeLinkPaymentProvider(StripeLinkOptions options, ILinkCliCommandRunner? runner = null)
    {
        _options = options;
        _runner = runner ?? new LinkCliProcessRunner();
    }

    public string ProviderId => _options.ProviderId;

    public async ValueTask<PaymentSetupStatus> GetSetupStatusAsync(CancellationToken ct)
    {
        var result = await _runner.RunAsync(
            _options.CliPath,
            ["--version"],
            _options.WorkingDirectory,
            _options.EnvironmentVariables,
            _options.Timeout,
            ct);

        if (result.ExitCode != 0)
        {
            return new PaymentSetupStatus
            {
                ProviderId = ProviderId,
                Enabled = true,
                Installed = false,
                Mode = _options.Mode,
                Status = "not_installed",
                Message = "link-cli was not found or did not start.",
                Requirements =
                [
                    new PaymentSetupRequirement
                    {
                        Name = "link-cli",
                        Satisfied = false,
                        Message = result.Stderr
                    }
                ]
            };
        }

        return new PaymentSetupStatus
        {
            ProviderId = ProviderId,
            Enabled = true,
            Installed = true,
            Version = FirstLine(result.Stdout),
            Mode = _options.Mode,
            Status = "ready",
            Requirements =
            [
                new PaymentSetupRequirement
                {
                    Name = "link-cli",
                    Satisfied = true
                }
            ]
        };
    }

    public async ValueTask<IReadOnlyList<FundingSource>> ListFundingSourcesAsync(PaymentExecutionContext context, CancellationToken ct)
    {
        var result = await RunJsonAsync(["funding-sources", "list", "--json", "--mode", _options.Mode], ct);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Stripe Link funding source list failed: {result.Stderr}");

        return ParseFundingSources(result.Stdout);
    }

    public async ValueTask<VirtualCardIssueResult> IssueVirtualCardAsync(VirtualCardRequest request, PaymentExecutionContext context, CancellationToken ct)
    {
        var args = new List<string>
        {
            "virtual-card",
            "issue",
            "--json",
            "--mode",
            _options.Mode,
            "--merchant",
            request.MerchantName,
            "--amount",
            request.AmountMinor.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--currency",
            request.Currency
        };
        if (!string.IsNullOrWhiteSpace(request.FundingSourceId))
            args.AddRange(["--funding-source", request.FundingSourceId]);

        var result = await RunJsonAsync(args, ct);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Stripe Link virtual card issue failed: {result.Stderr}");

        return ParseVirtualCardIssue(result.Stdout, request, context);
    }

    public async ValueTask<MachinePaymentProviderResult> ExecuteMachinePaymentAsync(MachinePaymentRequest request, PaymentExecutionContext context, CancellationToken ct)
    {
        var args = new List<string>
        {
            "machine-payment",
            "execute",
            "--json",
            "--mode",
            _options.Mode,
            "--amount",
            request.Challenge.AmountMinor.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--currency",
            request.Challenge.Currency
        };
        if (!string.IsNullOrWhiteSpace(request.Challenge.ChallengeId))
            args.AddRange(["--challenge", request.Challenge.ChallengeId]);
        if (!string.IsNullOrWhiteSpace(request.Challenge.ResourceUrl))
            args.AddRange(["--resource", request.Challenge.ResourceUrl]);

        var result = await RunJsonAsync(args, ct);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Stripe Link machine payment failed: {result.Stderr}");

        return ParseMachinePayment(result.Stdout, request);
    }

    public async ValueTask<PaymentStatus> GetPaymentStatusAsync(string paymentIdOrHandleId, PaymentExecutionContext context, CancellationToken ct)
    {
        var result = await RunJsonAsync(["status", "--json", "--mode", _options.Mode, "--id", paymentIdOrHandleId], ct);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Stripe Link status failed: {result.Stderr}");

        return ParseStatus(result.Stdout, paymentIdOrHandleId);
    }

    private ValueTask<LinkCliCommandResult> RunJsonAsync(IReadOnlyList<string> arguments, CancellationToken ct)
        => _runner.RunAsync(_options.CliPath, arguments, _options.WorkingDirectory, _options.EnvironmentVariables, _options.Timeout, ct);

    private static string? FirstLine(string value)
        => value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

    private IReadOnlyList<FundingSource> ParseFundingSources(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var items = root.ValueKind == JsonValueKind.Array
            ? root.EnumerateArray()
            : root.TryGetProperty("funding_sources", out var sources) && sources.ValueKind == JsonValueKind.Array
                ? sources.EnumerateArray()
                : root.TryGetProperty("fundingSources", out var camel) && camel.ValueKind == JsonValueKind.Array
                    ? camel.EnumerateArray()
                    : Enumerable.Empty<JsonElement>();

        var results = new List<FundingSource>();
        foreach (var item in items)
        {
            results.Add(new FundingSource
            {
                FundingSourceId = ReadString(item, "id") ?? ReadString(item, "fundingSourceId") ?? "",
                ProviderId = ProviderId,
                DisplayName = ReadString(item, "display_name") ?? ReadString(item, "displayName") ?? "Stripe Link funding source",
                Type = ReadString(item, "type") ?? "link",
                Last4 = ReadString(item, "last4"),
                Currency = ReadString(item, "currency"),
                TestMode = string.Equals(_options.Mode, PaymentEnvironments.Test, StringComparison.OrdinalIgnoreCase),
                Available = ReadBool(item, "available") ?? true
            });
        }

        return results;
    }

    private VirtualCardIssueResult ParseVirtualCardIssue(string json, VirtualCardRequest request, PaymentExecutionContext context)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var handleId = ReadString(root, "handle_id") ?? ReadString(root, "handleId") ?? ReadString(root, "id") ?? $"stripe_link_{Guid.NewGuid():N}";
        var pan = ReadString(root, "pan") ?? ReadString(root, "card_number") ?? ReadString(root, "cardNumber");
        var secret = new PaymentSecret(
            handleId,
            ProviderId,
            pan: pan,
            cvv: ReadString(root, "cvv") ?? ReadString(root, "cvc"),
            expMonth: ReadString(root, "exp_month") ?? ReadString(root, "expMonth"),
            expYear: ReadString(root, "exp_year") ?? ReadString(root, "expYear"),
            postalCode: ReadString(root, "postal_code") ?? ReadString(root, "postalCode"),
            expiresAtUtc: request.ValidUntilUtc);

        return new VirtualCardIssueResult
        {
            Handle = new VirtualCardHandle
            {
                HandleId = handleId,
                ProviderId = ProviderId,
                Last4 = ReadString(root, "last4") ?? secret.Last4,
                TargetMerchantName = request.MerchantName,
                IssuedAtUtc = DateTimeOffset.UtcNow,
                ValidUntilUtc = request.ValidUntilUtc,
                SpendRequestId = ReadString(root, "spend_request_id") ?? ReadString(root, "spendRequestId"),
                Status = ReadString(root, "status") ?? "issued",
                Environment = context.Environment
            },
            Secret = secret
        };
    }

    private MachinePaymentProviderResult ParseMachinePayment(string json, MachinePaymentRequest request)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var paymentId = ReadString(root, "payment_id") ?? ReadString(root, "paymentId") ?? ReadString(root, "id") ?? $"stripe_mp_{Guid.NewGuid():N}";
        var auth = ReadString(root, "authorization_header") ?? ReadString(root, "authorizationHeader");
        var token = ReadString(root, "authorization_token") ?? ReadString(root, "authorizationToken") ?? ReadString(root, "token");

        return new MachinePaymentProviderResult
        {
            Result = new MachinePaymentResult
            {
                PaymentId = paymentId,
                ProviderId = ProviderId,
                Status = ReadString(root, "status") ?? "completed",
                MerchantName = request.Challenge.MerchantName,
                AmountMinor = request.Challenge.AmountMinor,
                Currency = request.Challenge.Currency,
                ProviderReference = ReadString(root, "provider_reference") ?? ReadString(root, "providerReference")
            },
            ScopedAuthorizationSecret = string.IsNullOrWhiteSpace(auth) && string.IsNullOrWhiteSpace(token)
                ? null
                : new PaymentSecret(paymentId, ProviderId, authorizationToken: token, authorizationHeader: auth)
        };
    }

    private PaymentStatus ParseStatus(string json, string id)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new PaymentStatus
        {
            PaymentId = ReadString(root, "payment_id") ?? ReadString(root, "paymentId") ?? ReadString(root, "id") ?? id,
            ProviderId = ProviderId,
            Status = ReadString(root, "status") ?? "unknown",
            MerchantName = ReadString(root, "merchant_name") ?? ReadString(root, "merchantName"),
            AmountMinor = ReadLong(root, "amount") ?? ReadLong(root, "amountMinor"),
            Currency = ReadString(root, "currency"),
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            ProviderReference = ReadString(root, "provider_reference") ?? ReadString(root, "providerReference")
        };
    }

    private static string? ReadString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static bool? ReadBool(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.GetBoolean()
            : null;

    private static long? ReadLong(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return null;
        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var number))
            return number;
        if (property.ValueKind == JsonValueKind.String && long.TryParse(property.GetString(), out var parsed))
            return parsed;
        return null;
    }
}
