using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenClaw.Client;
using OpenClaw.Core.Models;
using QRCoder;

namespace OpenClaw.Companion.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly Func<string, string?, OpenClawHttpClient> _adminClientFactory;
    private CancellationTokenSource? _whatsAppAuthCts;
    private readonly object _whatsAppAuthStatesLock = new();
    private readonly Dictionary<string, ChannelAuthStatusItem> _whatsAppAuthStates = new(StringComparer.Ordinal);

    public IReadOnlyList<string> WhatsAppTypeOptions { get; } = ["official", "bridge", "first_party_worker"];
    public IReadOnlyList<string> WhatsAppDmPolicyOptions { get; } = ["open", "pairing", "closed"];

    [ObservableProperty]
    private bool _isWhatsAppBusy;

    [ObservableProperty]
    private string _whatsAppMessage = "WhatsApp setup not loaded.";

    [ObservableProperty]
    private string _whatsAppActiveBackend = "disabled";

    [ObservableProperty]
    private bool _whatsAppEnabled;

    [ObservableProperty]
    private string _whatsAppType = "official";

    [ObservableProperty]
    private string _whatsAppDmPolicy = "pairing";

    [ObservableProperty]
    private string _whatsAppWebhookPath = "/whatsapp/inbound";

    [ObservableProperty]
    private string _whatsAppWebhookPublicBaseUrl = "";

    [ObservableProperty]
    private string _whatsAppWebhookVerifyToken = "openclaw-verify";

    [ObservableProperty]
    private string _whatsAppWebhookVerifyTokenRef = "env:WHATSAPP_VERIFY_TOKEN";

    [ObservableProperty]
    private bool _whatsAppValidateSignature;

    [ObservableProperty]
    private string _whatsAppWebhookAppSecret = "";

    [ObservableProperty]
    private string _whatsAppWebhookAppSecretRef = "env:WHATSAPP_APP_SECRET";

    [ObservableProperty]
    private string _whatsAppCloudApiToken = "";

    [ObservableProperty]
    private string _whatsAppCloudApiTokenRef = "env:WHATSAPP_CLOUD_API_TOKEN";

    [ObservableProperty]
    private string _whatsAppPhoneNumberId = "";

    [ObservableProperty]
    private string _whatsAppBusinessAccountId = "";

    [ObservableProperty]
    private string _whatsAppBridgeUrl = "";

    [ObservableProperty]
    private string _whatsAppBridgeToken = "";

    [ObservableProperty]
    private string _whatsAppBridgeTokenRef = "env:WHATSAPP_BRIDGE_TOKEN";

    [ObservableProperty]
    private bool _whatsAppBridgeSuppressSendExceptions;

    [ObservableProperty]
    private bool _whatsAppPluginDetected;

    [ObservableProperty]
    private string _whatsAppPluginId = "";

    [ObservableProperty]
    private string _whatsAppPluginConfigJson = "";

    [ObservableProperty]
    private string _whatsAppFirstPartyWorkerConfigJson = "";

    [ObservableProperty]
    private string _whatsAppPluginWarning = "";

    [ObservableProperty]
    private bool _whatsAppRestartSupported;

    [ObservableProperty]
    private string _whatsAppRestartHint = "";

    [ObservableProperty]
    private string _whatsAppDerivedWebhookUrl = "";

    [ObservableProperty]
    private string _whatsAppWarnings = "";

    [ObservableProperty]
    private string _whatsAppValidationErrors = "";

    [ObservableProperty]
    private string _whatsAppAuthSummary = "";

    [ObservableProperty]
    private Bitmap? _whatsAppQrImage;

    [ObservableProperty]
    private string _whatsAppQrData = "";

    [RelayCommand]
    private async Task LoadWhatsAppSetupAsync()
    {
        if (IsWhatsAppBusy)
            return;

        IsWhatsAppBusy = true;
        try
        {
            using var client = CreateAdminClient(out var error);
            if (client is null)
            {
                WhatsAppMessage = error ?? "Invalid gateway URL.";
                return;
            }

            var response = await client.GetWhatsAppSetupAsync(CancellationToken.None);
            ApplyWhatsAppSetup(response);
            if (IsConnected)
                StartWhatsAppAuthStream();
        }
        catch (Exception ex)
        {
            WhatsAppMessage = $"WhatsApp setup load failed: {ex.Message}";
            AddSystemMessage($"WhatsApp setup load failed: {ex.Message}");
        }
        finally
        {
            IsWhatsAppBusy = false;
        }
    }

    [RelayCommand]
    private async Task SaveWhatsAppSetupAsync()
    {
        if (IsWhatsAppBusy)
            return;

        IsWhatsAppBusy = true;
        try
        {
            using var client = CreateAdminClient(out var error);
            if (client is null)
            {
                WhatsAppMessage = error ?? "Invalid gateway URL.";
                return;
            }

            var response = await client.SaveWhatsAppSetupAsync(BuildWhatsAppSetupRequest(), CancellationToken.None);
            ApplyWhatsAppSetup(response);
            if (IsConnected)
                StartWhatsAppAuthStream();
        }
        catch (Exception ex)
        {
            WhatsAppMessage = $"WhatsApp setup save failed: {ex.Message}";
            AddSystemMessage($"WhatsApp setup save failed: {ex.Message}");
        }
        finally
        {
            IsWhatsAppBusy = false;
        }
    }

    [RelayCommand]
    private async Task RestartWhatsAppAsync()
    {
        if (IsWhatsAppBusy)
            return;

        IsWhatsAppBusy = true;
        try
        {
            using var client = CreateAdminClient(out var error);
            if (client is null)
            {
                WhatsAppMessage = error ?? "Invalid gateway URL.";
                return;
            }

            var response = await client.RestartWhatsAppAsync(CancellationToken.None);
            ApplyWhatsAppSetup(response);
            if (IsConnected)
                StartWhatsAppAuthStream();
        }
        catch (Exception ex)
        {
            WhatsAppMessage = $"WhatsApp restart failed: {ex.Message}";
            AddSystemMessage($"WhatsApp restart failed: {ex.Message}");
        }
        finally
        {
            IsWhatsAppBusy = false;
        }
    }

    private void ApplyWhatsAppSetup(WhatsAppSetupResponse response)
    {
        WhatsAppMessage = string.IsNullOrWhiteSpace(response.Message) ? "WhatsApp setup loaded." : response.Message;
        WhatsAppActiveBackend = response.ActiveBackend;
        WhatsAppEnabled = response.Enabled;
        WhatsAppType = response.ConfiguredType;
        WhatsAppDmPolicy = response.DmPolicy;
        WhatsAppWebhookPath = response.WebhookPath;
        WhatsAppWebhookPublicBaseUrl = response.WebhookPublicBaseUrl ?? "";
        WhatsAppWebhookVerifyToken = response.WebhookVerifyToken;
        WhatsAppWebhookVerifyTokenRef = response.WebhookVerifyTokenRef;
        WhatsAppValidateSignature = response.ValidateSignature;
        WhatsAppWebhookAppSecret = response.WebhookAppSecret ?? "";
        WhatsAppWebhookAppSecretRef = response.WebhookAppSecretRef;
        WhatsAppCloudApiToken = response.CloudApiToken ?? "";
        WhatsAppCloudApiTokenRef = response.CloudApiTokenRef;
        WhatsAppPhoneNumberId = response.PhoneNumberId ?? "";
        WhatsAppBusinessAccountId = response.BusinessAccountId ?? "";
        WhatsAppBridgeUrl = response.BridgeUrl ?? "";
        WhatsAppBridgeToken = response.BridgeToken ?? "";
        WhatsAppBridgeTokenRef = response.BridgeTokenRef;
        WhatsAppBridgeSuppressSendExceptions = response.BridgeSuppressSendExceptions;
        WhatsAppPluginDetected = response.PluginDetected;
        WhatsAppPluginId = response.PluginId ?? "";
        WhatsAppPluginConfigJson = response.PluginConfigJson ?? "";
        WhatsAppFirstPartyWorkerConfigJson = response.FirstPartyWorkerConfigJson ?? "";
        WhatsAppPluginWarning = response.PluginWarning ?? "";
        WhatsAppRestartSupported = response.RestartSupported;
        WhatsAppRestartHint = response.RestartHint ?? "";
        WhatsAppDerivedWebhookUrl = response.DerivedWebhookUrl ?? "";
        WhatsAppWarnings = string.Join(Environment.NewLine, response.Warnings ?? []);
        WhatsAppValidationErrors = string.Join(Environment.NewLine, response.ValidationErrors ?? []);

        lock (_whatsAppAuthStatesLock)
        {
            _whatsAppAuthStates.Clear();
            foreach (var item in response.AuthStates)
                _whatsAppAuthStates[BuildAuthKey(item)] = item;
        }
        RefreshWhatsAppAuthUi();
    }

    private void StartWhatsAppAuthStream()
    {
        CancelWhatsAppAuthStream();

        try
        {
            using var initialClient = CreateAdminClient(out _);
            if (initialClient is null)
                return;
        }
        catch
        {
            return;
        }

        _whatsAppAuthCts = new CancellationTokenSource();
        var ct = _whatsAppAuthCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                using var client = CreateAdminClient(out _);
                if (client is null)
                    return;

                await client.StreamChannelAuthAsync("whatsapp", accountId: null, item =>
                {
                    lock (_whatsAppAuthStatesLock)
                    {
                        _whatsAppAuthStates[BuildAuthKey(item)] = item;
                    }
                    Dispatcher.UIThread.Post(RefreshWhatsAppAuthUi);
                }, ct);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => WhatsAppPluginWarning = $"Auth stream failed: {ex.Message}");
            }
        }, ct);
    }

    private void CancelWhatsAppAuthStream()
    {
        if (_whatsAppAuthCts is null)
            return;

        try { _whatsAppAuthCts.Cancel(); } catch { }
        try { _whatsAppAuthCts.Dispose(); } catch { }
        _whatsAppAuthCts = null;
    }

    private void RefreshWhatsAppAuthUi()
    {
        ChannelAuthStatusItem[] items;
        lock (_whatsAppAuthStatesLock)
        {
            items = _whatsAppAuthStates.Values
                .OrderByDescending(static item => item.UpdatedAtUtc)
                .ToArray();
        }
        WhatsAppAuthSummary = items.Length == 0
            ? "No live WhatsApp auth state."
            : string.Join(Environment.NewLine, items.Select(static item =>
                $"{(string.IsNullOrWhiteSpace(item.AccountId) ? "default" : item.AccountId)}: {item.State} @ {item.UpdatedAtUtc:O}"));

        var qrState = items.FirstOrDefault(static item => string.Equals(item.State, "qr_code", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(item.Data));
        WhatsAppQrData = qrState?.Data ?? "";
        WhatsAppQrImage = string.IsNullOrWhiteSpace(qrState?.Data) ? null : CreateQrBitmap(qrState.Data!);
    }

    private OpenClawHttpClient? CreateAdminClient(out string? error)
    {
        error = null;
        if (!GatewayEndpointResolver.TryResolveHttpBaseUrl(ServerUrl, out var baseUrl) || string.IsNullOrWhiteSpace(baseUrl))
        {
            error = "Server URL must be a valid ws:// or wss:// gateway endpoint.";
            return null;
        }

        return _adminClientFactory(baseUrl, string.IsNullOrWhiteSpace(AuthToken) ? null : AuthToken);
    }

    private WhatsAppSetupRequest BuildWhatsAppSetupRequest()
        => new()
        {
            Enabled = WhatsAppEnabled,
            Type = WhatsAppType,
            DmPolicy = WhatsAppDmPolicy,
            WebhookPath = WhatsAppWebhookPath,
            WebhookPublicBaseUrl = EmptyToNull(WhatsAppWebhookPublicBaseUrl),
            WebhookVerifyToken = WhatsAppWebhookVerifyToken,
            WebhookVerifyTokenRef = WhatsAppWebhookVerifyTokenRef,
            ValidateSignature = WhatsAppValidateSignature,
            WebhookAppSecret = EmptyToNull(WhatsAppWebhookAppSecret),
            WebhookAppSecretRef = WhatsAppWebhookAppSecretRef,
            CloudApiToken = EmptyToNull(WhatsAppCloudApiToken),
            CloudApiTokenRef = WhatsAppCloudApiTokenRef,
            PhoneNumberId = EmptyToNull(WhatsAppPhoneNumberId),
            BusinessAccountId = EmptyToNull(WhatsAppBusinessAccountId),
            BridgeUrl = EmptyToNull(WhatsAppBridgeUrl),
            BridgeToken = EmptyToNull(WhatsAppBridgeToken),
            BridgeTokenRef = WhatsAppBridgeTokenRef,
            BridgeSuppressSendExceptions = WhatsAppBridgeSuppressSendExceptions,
            PluginId = EmptyToNull(WhatsAppPluginId),
            PluginConfigJson = EmptyToNull(WhatsAppPluginConfigJson),
            FirstPartyWorkerConfigJson = EmptyToNull(WhatsAppFirstPartyWorkerConfigJson)
        };

    private static string BuildAuthKey(ChannelAuthStatusItem item)
        => $"{item.ChannelId}\n{item.AccountId ?? string.Empty}";

    private static string? EmptyToNull(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    private static Bitmap? CreateQrBitmap(string data)
    {
        try
        {
            using var generator = new QRCodeGenerator();
            using var qrData = generator.CreateQrCode(data, QRCodeGenerator.ECCLevel.Q);
            var png = new PngByteQRCode(qrData);
            var bytes = png.GetGraphic(12);
            return new Bitmap(new MemoryStream(bytes, writable: false));
        }
        catch
        {
            return null;
        }
    }
}
