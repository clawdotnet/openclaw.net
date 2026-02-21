using System.Security.Cryptography;
using System.Text;

namespace OpenClaw.Gateway;

internal static class TwilioWebhookVerifier
{
    public static string ComputeSignature(string url, IReadOnlyDictionary<string, string> parameters, string authToken)
    {
        var sb = new StringBuilder(capacity: url.Length + 256);
        sb.Append(url);

        foreach (var kvp in parameters.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            sb.Append(kvp.Key);
            sb.Append(kvp.Value);
        }

        var key = Encoding.UTF8.GetBytes(authToken);
        var data = Encoding.UTF8.GetBytes(sb.ToString());

        using var hmac = new HMACSHA1(key);
        var hash = hmac.ComputeHash(data);
        return Convert.ToBase64String(hash);
    }

    public static bool IsValidSignature(string url, IReadOnlyDictionary<string, string> parameters, string authToken, string? providedSignature)
    {
        if (string.IsNullOrWhiteSpace(providedSignature))
            return false;

        var expected = ComputeSignature(url, parameters, authToken);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var providedBytes = Encoding.UTF8.GetBytes(providedSignature.Trim());

        if (expectedBytes.Length != providedBytes.Length)
            return false;

        return CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes);
    }
}

