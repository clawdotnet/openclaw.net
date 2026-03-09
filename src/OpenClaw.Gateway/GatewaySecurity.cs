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

        var token = value[prefix.Length..].Trim();
        return string.IsNullOrWhiteSpace(token) ? null : token;
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

    public static bool IsHmacSha256SignatureValid(string secret, ReadOnlySpan<byte> payload, string? providedSignature)
    {
        if (string.IsNullOrWhiteSpace(providedSignature))
            return false;

        var signature = providedSignature.Trim();
        const string prefix = "sha256=";
        if (signature.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            signature = signature[prefix.Length..].Trim();

        Span<byte> providedHash = stackalloc byte[32];
        if (!TryDecodeHex(signature, providedHash))
            return false;

        var secretBytes = Encoding.UTF8.GetBytes(secret);
        var expectedHash = HMACSHA256.HashData(secretBytes, payload);
        return CryptographicOperations.FixedTimeEquals(expectedHash, providedHash);
    }

    public static bool IsHmacSha256SignatureValid(string secret, string payload, string? providedSignature)
        => IsHmacSha256SignatureValid(secret, Encoding.UTF8.GetBytes(payload), providedSignature);

    private static bool TryDecodeHex(string hex, Span<byte> destination)
    {
        if (hex.Length != destination.Length * 2)
            return false;

        for (var i = 0; i < destination.Length; i++)
        {
            var hi = DecodeHexNibble(hex[i * 2]);
            var lo = DecodeHexNibble(hex[i * 2 + 1]);
            if (hi < 0 || lo < 0)
                return false;
            destination[i] = (byte)((hi << 4) | lo);
        }

        return true;
    }

    private static int DecodeHexNibble(char c)
    {
        if (c is >= '0' and <= '9')
            return c - '0';
        if (c is >= 'a' and <= 'f')
            return c - 'a' + 10;
        if (c is >= 'A' and <= 'F')
            return c - 'A' + 10;
        return -1;
    }
}
