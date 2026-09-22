using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenClaw.Core.Models;
namespace OpenClaw.Companion.ViewModels;
public partial class MainWindowViewModel
{
    [ObservableProperty] private string _deviceEnrollmentCode = "";
    [ObservableProperty] private string _deviceEnrollmentAccountId = "";
    [ObservableProperty] private string _deviceEnrollmentName = Environment.MachineName;
    [ObservableProperty] private string _deviceEnrollmentStatus = "Generate a code as an administrator, then enter it on the new device. Codes expire after five minutes.";

    [RelayCommand]
    private async Task CreateDeviceEnrollmentAsync()
    {
        try
        {
            using var client = CreateAdminClient(out var error);
            if (client is null) throw new InvalidOperationException(error);
            var result = await client.CreateDeviceEnrollmentAsync(new(DeviceEnrollmentAccountId, DeviceEnrollmentName), CancellationToken.None);
            DeviceEnrollmentCode = result.Code;
            DeviceEnrollmentStatus = $"One-use code expires at {result.ExpiresAtUtc.ToLocalTime():t}. Device tokens expire after 30 days; revoke them in Operator accounts.";
        }
        catch (Exception ex) when (IsUserFacingOperationError(ex)) { DeviceEnrollmentStatus = ex.Message; }
    }

    [RelayCommand]
    private async Task RedeemDeviceEnrollmentAsync()
    {
        try
        {
            using var client = CreateAdminClient(authToken: null, out var error);
            if (client is null) throw new InvalidOperationException(error);
            var result = await client.ExchangeDeviceEnrollmentAsync(DeviceEnrollmentCode, CancellationToken.None);
            AuthToken = result.Token;
            RememberToken = true;
            DeviceEnrollmentCode = "";
            SaveSettings();
            DeviceEnrollmentStatus = $"Enrolled as {result.Account?.Username}. Connect to start chatting.";
        }
        catch (Exception ex) when (IsUserFacingOperationError(ex)) { DeviceEnrollmentStatus = ex.Message; }
    }

    private static bool IsUserFacingOperationError(Exception ex) => ex is ArgumentException or InvalidOperationException
        or IOException or HttpRequestException or System.Text.Json.JsonException or System.Security.Cryptography.CryptographicException
        or UnauthorizedAccessException or NotSupportedException or System.ComponentModel.Win32Exception;
}
