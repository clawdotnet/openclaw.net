using System.Collections.ObjectModel;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenClaw.Payments.Abstractions;

namespace OpenClaw.Companion.ViewModels;

public sealed partial class MainWindowViewModel
{
    [ObservableProperty]
    private bool _isPaymentsBusy;

    [ObservableProperty]
    private string _paymentsStatus = "Payment Lab not loaded.";

    [ObservableProperty]
    private string _paymentProvider = "";

    [ObservableProperty]
    private string _paymentEnvironment = PaymentEnvironments.Test;

    [ObservableProperty]
    private string _paymentSetupSummary = "Load setup status before running payment actions.";

    [ObservableProperty]
    private string _virtualCardMerchantName = "";

    [ObservableProperty]
    private string _virtualCardMerchantUrl = "";

    [ObservableProperty]
    private string _virtualCardAmountMinor = "";

    [ObservableProperty]
    private string _virtualCardCurrency = "USD";

    [ObservableProperty]
    private string _virtualCardFundingSourceId = "";

    [ObservableProperty]
    private string _machinePaymentReference = "";

    [ObservableProperty]
    private string _machinePaymentMerchantName = "";

    [ObservableProperty]
    private string _machinePaymentAmountMinor = "";

    [ObservableProperty]
    private string _machinePaymentCurrency = "USD";

    [ObservableProperty]
    private string _machinePaymentFundingSourceId = "";

    [ObservableProperty]
    private string _paymentStatusLookupId = "";

    [ObservableProperty]
    private string _paymentResultText = "";

    public ObservableCollection<FundingSourceRow> FundingSourceRows { get; } = [];
    public bool HasFundingSourceRows => FundingSourceRows.Count > 0;

    [RelayCommand]
    private async Task LoadPaymentSetupAsync()
    {
        if (IsPaymentsBusy)
            return;

        IsPaymentsBusy = true;
        try
        {
            using var client = RequireIntegrationClient(status => PaymentsStatus = status);
            if (client is null)
                return;

            var setup = await client.GetPaymentSetupStatusAsync(EmptyToNull(PaymentProvider), CancellationToken.None);
            PaymentSetupSummary = string.Join(Environment.NewLine, new[]
            {
                $"Provider: {setup.ProviderId}",
                $"Enabled: {setup.Enabled}",
                $"Installed: {setup.Installed}",
                $"Mode: {setup.Mode}",
                $"Status: {setup.Status}",
                $"Message: {setup.Message ?? ""}",
                $"Requirements: {(setup.Requirements.Count == 0 ? "none" : string.Join("; ", setup.Requirements.Select(r => $"{r.Name}={(r.Satisfied ? "ok" : "missing")}")))}"
            });

            var funding = setup.Enabled
                ? await client.ListPaymentFundingSourcesAsync(EmptyToNull(PaymentProvider), EmptyToNull(PaymentEnvironment), CancellationToken.None)
                : new List<FundingSource>();
            ReplaceItems(FundingSourceRows, funding.Select(FundingSourceRow.FromFundingSource));
            OnPropertyChanged(nameof(HasFundingSourceRows));
            PaymentsStatus = setup.Enabled ? "Payment setup loaded." : "Payments are disabled by configuration.";
        }
        catch (OperationCanceledException ex)
        {
            ReplaceItems(FundingSourceRows, []);
            OnPropertyChanged(nameof(HasFundingSourceRows));
            PaymentsStatus = $"Payment setup load canceled: {ex.Message}";
        }
        catch (HttpRequestException ex)
        {
            ReplaceItems(FundingSourceRows, []);
            OnPropertyChanged(nameof(HasFundingSourceRows));
            PaymentsStatus = $"Payment setup load failed: {ex.Message}";
        }
        catch (InvalidOperationException ex)
        {
            ReplaceItems(FundingSourceRows, []);
            OnPropertyChanged(nameof(HasFundingSourceRows));
            PaymentsStatus = $"Payment setup load failed: {ex.Message}";
        }
        finally
        {
            IsPaymentsBusy = false;
        }
    }

    [RelayCommand]
    private async Task IssueVirtualCardAsync()
    {
        if (!long.TryParse(VirtualCardAmountMinor, out var amountMinor) || amountMinor <= 0)
        {
            PaymentsStatus = "Virtual card amount must be a positive minor-unit integer.";
            return;
        }

        var confirmed = await ConfirmMutationAsync(
            "Issue virtual card",
            $"Issue a {VirtualCardCurrency} {amountMinor} minor-unit virtual card for '{VirtualCardMerchantName}'? Payment Lab actions are experimental and approval-gated by policy.",
            "Issue");
        if (!confirmed)
            return;

        try
        {
            using var client = RequireIntegrationClient(status => PaymentsStatus = status);
            if (client is null)
                return;

            var handle = await client.IssueVirtualCardAsync(new VirtualCardRequest
            {
                ProviderId = EmptyToNull(PaymentProvider),
                Environment = PaymentEnvironment,
                FundingSourceId = EmptyToNull(VirtualCardFundingSourceId),
                MerchantName = VirtualCardMerchantName,
                MerchantUrl = EmptyToNull(VirtualCardMerchantUrl),
                AmountMinor = amountMinor,
                Currency = VirtualCardCurrency
            }, yes: true, CancellationToken.None);
            PaymentResultText = $"Virtual card issued: {handle.HandleId} · {handle.Status} · last4 {handle.Last4 ?? "masked"}";
            PaymentsStatus = "Virtual card request completed.";
        }
        catch (OperationCanceledException ex)
        {
            PaymentsStatus = $"Virtual card request canceled: {ex.Message}";
        }
        catch (HttpRequestException ex)
        {
            PaymentsStatus = $"Virtual card request failed: {ex.Message}";
        }
        catch (InvalidOperationException ex)
        {
            PaymentsStatus = $"Virtual card request failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ExecuteMachinePaymentAsync()
    {
        if (string.IsNullOrWhiteSpace(MachinePaymentReference) || MachinePaymentReference.Length > 200)
        {
            PaymentsStatus = "Enter a payment reference of up to 200 characters. Reuse the same reference when retrying a purchase.";
            return;
        }

        if (!long.TryParse(MachinePaymentAmountMinor, out var amountMinor) || amountMinor <= 0)
        {
            PaymentsStatus = "Payment amount must be a positive minor-unit integer.";
            return;
        }

        var confirmed = await ConfirmMutationAsync(
            "Execute payment",
            $"Execute a {MachinePaymentCurrency} {amountMinor} minor-unit machine payment for '{MachinePaymentMerchantName}'?",
            "Execute");
        if (!confirmed)
            return;

        try
        {
            using var client = RequireIntegrationClient(status => PaymentsStatus = status);
            if (client is null)
                return;

            var result = await client.ExecuteMachinePaymentAsync(new MachinePaymentRequest
            {
                IdempotencyKey = MachinePaymentReference,
                ProviderId = EmptyToNull(PaymentProvider),
                Environment = PaymentEnvironment,
                FundingSourceId = EmptyToNull(MachinePaymentFundingSourceId),
                Challenge = new MachinePaymentChallenge
                {
                    MerchantName = MachinePaymentMerchantName,
                    AmountMinor = amountMinor,
                    Currency = MachinePaymentCurrency,
                    ProviderId = EmptyToNull(PaymentProvider)
                }
            }, yes: true, CancellationToken.None);
            PaymentResultText = $"Payment {result.PaymentId}: {result.Status} · {result.MerchantName} · {result.AmountMinor} {result.Currency}";
            PaymentsStatus = "Payment execution completed.";
        }
        catch (OperationCanceledException ex)
        {
            PaymentsStatus = $"Payment execution canceled: {ex.Message}";
        }
        catch (HttpRequestException ex)
        {
            PaymentsStatus = $"Payment execution failed: {ex.Message}";
        }
        catch (InvalidOperationException ex)
        {
            PaymentsStatus = $"Payment execution failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task LookupPaymentStatusAsync()
    {
        if (string.IsNullOrWhiteSpace(PaymentStatusLookupId))
        {
            PaymentsStatus = "Payment id is required.";
            return;
        }

        try
        {
            using var client = RequireIntegrationClient(status => PaymentsStatus = status);
            if (client is null)
                return;

            var status = await client.GetPaymentStatusAsync(PaymentStatusLookupId, EmptyToNull(PaymentProvider), EmptyToNull(PaymentEnvironment), CancellationToken.None);
            PaymentResultText = $"Payment {status.PaymentId}: {status.Status} · {status.MerchantName ?? ""} · {status.AmountMinor?.ToString() ?? ""} {status.Currency ?? ""}";
            PaymentsStatus = "Payment status loaded.";
        }
        catch (OperationCanceledException ex)
        {
            PaymentsStatus = $"Payment status lookup canceled: {ex.Message}";
        }
        catch (HttpRequestException ex)
        {
            PaymentsStatus = $"Payment status lookup failed: {ex.Message}";
        }
        catch (InvalidOperationException ex)
        {
            PaymentsStatus = $"Payment status lookup failed: {ex.Message}";
        }
    }
}

public sealed class FundingSourceRow
{
    public required string FundingSourceId { get; init; }
    public required string Provider { get; init; }
    public required string DisplayName { get; init; }
    public required string Type { get; init; }
    public required string Mode { get; init; }
    public required string Detail { get; init; }

    public static FundingSourceRow FromFundingSource(FundingSource item)
        => new()
        {
            FundingSourceId = item.FundingSourceId,
            Provider = item.ProviderId,
            DisplayName = item.DisplayName,
            Type = item.Type,
            Mode = item.TestMode ? "test" : "live",
            Detail = $"{item.Currency ?? ""} · last4 {item.Last4 ?? "n/a"} · {(item.Available ? "available" : "unavailable")}"
        };
}
