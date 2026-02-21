using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace OpenClaw.Gateway;

internal static class GatewaySecurity
{
    public static bool IsLoopbackBind(string bindAddress)
    {
        if (IPAddress.TryParse(bindAddress, out var ip))
            return IPAddress.IsLoopback(ip);

        return string.Equals(bindAddress, "localhost", StringComparison.OrdinalIgnoreCase);
    }

    public static string? GetBearerToken(HttpContext ctx)
    {
        if (!ctx.Request.Headers.TryGetValue("Authorization", out var auth))
            return null;

        var value = auth.ToString();
        const string prefix = "Bearer ";
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;

        return value[prefix.Length..].Trim();
    }

    public static string? GetToken(HttpContext ctx, bool allowQueryStringToken)
    {
        var token = GetBearerToken(ctx);
        if (!string.IsNullOrEmpty(token))
            return token;

        if (!allowQueryStringToken)
            return null;

        return ctx.Request.Query["token"].FirstOrDefault();
    }

    public static bool IsTokenValid(string? provided, string expected)
    {
        if (string.IsNullOrEmpty(provided))
            return false;

        // FixedTimeEquals handles different-length spans in constant time (returns false).
        // An explicit length check would leak timing info about the expected token length.
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(provided),
            Encoding.UTF8.GetBytes(expected));
    }

    public static string ComputeHmacSha256Hex(string secret, string payload)
    {
        var secretBytes = Encoding.UTF8.GetBytes(secret);
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        var hashBytes = HMACSHA256.HashData(secretBytes, payloadBytes);
        return Convert.ToHexStringLower(hashBytes);
    }
}

