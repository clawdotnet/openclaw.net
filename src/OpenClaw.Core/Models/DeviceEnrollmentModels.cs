using System.Text.Json.Serialization;
namespace OpenClaw.Core.Models;
public sealed record DeviceEnrollmentRequest(string AccountId, string DeviceName);
public sealed record DeviceEnrollmentCode(string Code, DateTimeOffset ExpiresAtUtc);
public sealed record DeviceEnrollmentExchange(string Code);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(DeviceEnrollmentRequest))]
[JsonSerializable(typeof(DeviceEnrollmentCode))]
[JsonSerializable(typeof(DeviceEnrollmentExchange))]
public partial class DeviceEnrollmentJsonContext : JsonSerializerContext { }
