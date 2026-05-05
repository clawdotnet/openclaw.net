using System.Text.Json;
using OpenClaw.Payments.Abstractions;

namespace OpenClaw.Cli;

internal static class PaymentCommands
{
    private const string DefaultBaseUrl = "http://127.0.0.1:18789";
    private const string EnvBaseUrl = "OPENCLAW_BASE_URL";
    private const string EnvAuthToken = "OPENCLAW_AUTH_TOKEN";

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintHelp();
            return 0;
        }

        var parsed = CliArgs.Parse(args);
        var command = string.Join(' ', parsed.Positionals.Take(2));
        if (parsed.Positionals.Count == 1)
            command = parsed.Positionals[0];

        using var client = CreateClient(parsed);
        var ct = CancellationToken.None;
        var provider = parsed.GetOption("--provider");
        var environment = parsed.HasFlag("--test") ? PaymentEnvironments.Test : parsed.GetOption("--environment");
        var json = parsed.HasFlag("--json");

        switch (command)
        {
            case "setup":
            {
                var status = await client.GetPaymentSetupStatusAsync(provider, ct);
                Write(status, PaymentJsonContext.Default.PaymentSetupStatus, json, TextSetup);
                return 0;
            }

            case "funding list":
            {
                var items = await client.ListPaymentFundingSourcesAsync(provider, environment, ct);
                Write(items, PaymentJsonContext.Default.ListFundingSource, json, TextFunding);
                return 0;
            }

            case "virtual-card issue":
            {
                var merchant = Required(parsed, "--merchant");
                var request = new VirtualCardRequest
                {
                    ProviderId = provider,
                    FundingSourceId = parsed.GetOption("--funding-source"),
                    MerchantName = merchant,
                    MerchantUrl = parsed.GetOption("--merchant-url"),
                    AmountMinor = ParseLong(parsed.GetOption("--amount-minor"), "--amount-minor"),
                    Currency = parsed.GetOption("--currency") ?? "USD",
                    Purpose = parsed.GetOption("--purpose"),
                    ValidUntilUtc = DateTimeOffset.UtcNow.AddMinutes(ParseInt(parsed.GetOption("--valid-minutes"), "--valid-minutes", 30)),
                    Environment = environment ?? PaymentEnvironments.Live
                };
                RequireYesForLive(request.Environment, parsed.HasFlag("--yes"));
                var handle = await client.IssueVirtualCardAsync(request, parsed.HasFlag("--yes"), ct);
                Write(handle, PaymentJsonContext.Default.VirtualCardHandle, json, TextHandle);
                return 0;
            }

            case "execute":
            {
                var env = environment ?? PaymentEnvironments.Live;
                RequireYesForLive(env, parsed.HasFlag("--yes"));
                var request = new MachinePaymentRequest
                {
                    ProviderId = provider,
                    FundingSourceId = parsed.GetOption("--funding-source"),
                    Environment = env,
                    Challenge = new MachinePaymentChallenge
                    {
                        ChallengeId = parsed.GetOption("--challenge-id"),
                        Protocol = parsed.GetOption("--protocol") ?? "http-402",
                        ResourceUrl = parsed.GetOption("--resource-url"),
                        MerchantName = parsed.GetOption("--merchant"),
                        AmountMinor = ParseLong(parsed.GetOption("--amount-minor"), "--amount-minor"),
                        Currency = parsed.GetOption("--currency") ?? "USD",
                        ProviderId = provider
                    }
                };
                var result = await client.ExecuteMachinePaymentAsync(request, parsed.HasFlag("--yes"), ct);
                Write(result, PaymentJsonContext.Default.MachinePaymentResult, json, TextMachinePayment);
                return 0;
            }

            case "status":
            {
                var id = Required(parsed, "--id");
                var status = await client.GetPaymentStatusAsync(id, provider, environment, ct);
                Write(status, PaymentJsonContext.Default.PaymentStatus, json, TextStatus);
                return 0;
            }

            default:
                PrintHelp();
                return 2;
        }
    }

    private static OpenClawHttpClient CreateClient(CliArgs parsed)
    {
        var baseUrl = parsed.GetOption("--url") ?? Environment.GetEnvironmentVariable(EnvBaseUrl) ?? DefaultBaseUrl;
        var token = parsed.GetOption("--token") ?? Environment.GetEnvironmentVariable(EnvAuthToken);
        return new OpenClawHttpClient(baseUrl, token);
    }

    private static void RequireYesForLive(string environment, bool yes)
    {
        if (string.Equals(environment, PaymentEnvironments.Live, StringComparison.OrdinalIgnoreCase) && !yes)
            throw new InvalidOperationException("Live payment commands require --yes. Policy and approval checks still run.");
    }

    private static string Required(CliArgs parsed, string name)
        => parsed.GetOption(name) ?? throw new ArgumentException($"{name} is required.");

    private static long ParseLong(string? value, string name)
        => long.TryParse(value, out var parsed) ? parsed : throw new ArgumentException($"{name} is required and must be an integer.");

    private static int ParseInt(string? value, string name, int fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : int.TryParse(value, out var parsed) ? parsed : throw new ArgumentException($"{name} must be an integer.");

    private static void Write<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo, bool json, Action<T> text)
    {
        if (json)
            Console.WriteLine(JsonSerializer.Serialize(value, typeInfo));
        else
            text(value);
    }

    private static void TextSetup(PaymentSetupStatus status)
    {
        Console.WriteLine($"{status.ProviderId}: {status.Status} mode={status.Mode} installed={status.Installed}");
        if (!string.IsNullOrWhiteSpace(status.Version))
            Console.WriteLine($"version: {status.Version}");
        if (!string.IsNullOrWhiteSpace(status.Message))
            Console.WriteLine(status.Message);
    }

    private static void TextFunding(List<FundingSource> items)
    {
        foreach (var item in items)
            Console.WriteLine($"{item.FundingSourceId}\t{item.ProviderId}\t{item.DisplayName}\tlast4={item.Last4 ?? ""}\tavailable={item.Available}");
    }

    private static void TextHandle(VirtualCardHandle handle)
        => Console.WriteLine($"{handle.HandleId}\t{handle.ProviderId}\tlast4={handle.Last4}\tmerchant={handle.TargetMerchantName}\tvalidUntil={handle.ValidUntilUtc:O}\tstatus={handle.Status}");

    private static void TextMachinePayment(MachinePaymentResult result)
        => Console.WriteLine($"{result.PaymentId}\t{result.ProviderId}\t{result.Status}\tmerchant={result.MerchantName}\tamount={result.AmountMinor} {result.Currency}");

    private static void TextStatus(PaymentStatus status)
        => Console.WriteLine($"{status.PaymentId}\t{status.ProviderId}\t{status.Status}\tmerchant={status.MerchantName}\tamount={status.AmountMinor} {status.Currency}");

    private static void PrintHelp()
    {
        Console.WriteLine(
            """
            openclaw payment

            Usage:
              openclaw payment setup [--provider <id>] [--json]
              openclaw payment funding list [--provider <id>] [--test] [--json]
              openclaw payment virtual-card issue --merchant <name> --amount-minor <n> [--currency USD] [--funding-source <id>] [--provider <id>] [--test|--environment live] [--yes] [--json]
              openclaw payment execute --amount-minor <n> [--merchant <name>] [--resource-url <url>] [--currency USD] [--provider <id>] [--test|--environment live] [--yes] [--json]
              openclaw payment status --id <payment-or-handle-id> [--provider <id>] [--json]

            Notes:
              - Commands talk to the running gateway so handles remain usable by browser sentinel substitution.
              - Output is allow-listed safe metadata only.
            """);
    }
}
