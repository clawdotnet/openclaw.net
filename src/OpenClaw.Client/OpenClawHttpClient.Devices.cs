using System.Text;
using System.Text.Json;
using OpenClaw.Core.Models;
namespace OpenClaw.Client;
public sealed partial class OpenClawHttpClient
{
    public async Task<DeviceEnrollmentCode> CreateDeviceEnrollmentAsync(DeviceEnrollmentRequest request, CancellationToken ct)
    {
        RequireSecureEnrollment();
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUri, "auth/devices/enroll"))
        { Content = new StringContent(JsonSerializer.Serialize(request, DeviceEnrollmentJsonContext.Default.DeviceEnrollmentRequest), Encoding.UTF8, "application/json") };
        return await SendAsync(message, DeviceEnrollmentJsonContext.Default.DeviceEnrollmentCode, ct);
    }
    public async Task<OperatorAccountTokenCreateResponse> ExchangeDeviceEnrollmentAsync(string code, CancellationToken ct)
    {
        RequireSecureEnrollment();
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUri, "auth/devices/exchange"))
        { Content = new StringContent(JsonSerializer.Serialize(new DeviceEnrollmentExchange(code), DeviceEnrollmentJsonContext.Default.DeviceEnrollmentExchange), Encoding.UTF8, "application/json") };
        return await SendAsync(message, CoreJsonContext.Default.OperatorAccountTokenCreateResponse, ct);
    }
    private void RequireSecureEnrollment()
    {
        if (_baseUri.Scheme != "https" && !_baseUri.IsLoopback)
            throw new InvalidOperationException("Remote enrollment requires HTTPS.");
    }
}
