using System.Net;
using System.Net.Sockets;
using OpenClaw.Core.Models;

namespace OpenClaw.Core.Security;

public sealed record UrlSafetyValidationResult(bool Allowed, string? Reason)
{
    public static UrlSafetyValidationResult Allow { get; } = new(true, null);

    public static UrlSafetyValidationResult Deny(string reason) => new(false, reason);

    public string ToToolError()
        => Allowed ? "" : $"Error: URL blocked by safety policy - {Reason}";
}

public static class UrlSafetyValidator
{
    private static readonly string[] BuiltInBlockedHostGlobs =
    [
        "localhost",
        "*.localhost",
        "metadata",
        "metadata.google.internal"
    ];

    public static async ValueTask<UrlSafetyValidationResult> ValidateHttpUrlAsync(
        Uri uri,
        UrlSafetyConfig? config,
        CancellationToken ct = default)
    {
        var preliminary = ValidateHttpUrl(uri, config, resolveDns: false);
        if (!preliminary.Allowed)
            return preliminary;

        var policy = config ?? new UrlSafetyConfig();
        if (!policy.Enabled || (!policy.BlockPrivateNetworkTargets && policy.BlockedCidrs.Length == 0))
            return UrlSafetyValidationResult.Allow;

        var host = NormalizeHost(uri);
        if (IPAddress.TryParse(host, out _))
            return preliminary;

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(host, ct);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            return UrlSafetyValidationResult.Deny($"DNS resolution failed for '{host}': {ex.Message}");
        }

        if (addresses.Length == 0)
            return UrlSafetyValidationResult.Deny($"DNS resolution returned no addresses for '{host}'.");

        return ValidateAddresses(addresses, policy, host);
    }

    public static UrlSafetyValidationResult ValidateHttpUrl(
        Uri uri,
        UrlSafetyConfig? config,
        bool resolveDns = true)
    {
        var policy = config ?? new UrlSafetyConfig();
        if (!policy.Enabled)
            return UrlSafetyValidationResult.Allow;

        if (!uri.IsAbsoluteUri || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return UrlSafetyValidationResult.Deny("only absolute http(s) URLs are allowed.");

        var host = NormalizeHost(uri);
        if (string.IsNullOrWhiteSpace(host))
            return UrlSafetyValidationResult.Deny("URL host is empty.");

        if (policy.BlockPrivateNetworkTargets)
        {
            foreach (var pattern in BuiltInBlockedHostGlobs)
            {
                if (GlobMatcher.IsMatch(pattern, host, StringComparison.OrdinalIgnoreCase))
                    return UrlSafetyValidationResult.Deny($"host '{host}' is blocked.");
            }
        }

        foreach (var pattern in policy.BlockedHostGlobs.Where(static p => !string.IsNullOrWhiteSpace(p)))
        {
            if (GlobMatcher.IsMatch(pattern.Trim(), host, StringComparison.OrdinalIgnoreCase))
            {
                return UrlSafetyValidationResult.Deny($"host '{host}' matches blocklist entry '{pattern}'.");
            }
        }

        if (IPAddress.TryParse(host, out var literalIp))
            return ValidateAddresses([literalIp], policy, host);

        if (!resolveDns || (!policy.BlockPrivateNetworkTargets && policy.BlockedCidrs.Length == 0))
            return UrlSafetyValidationResult.Allow;

        IPAddress[] addresses;
        try
        {
            addresses = Dns.GetHostAddresses(host);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            return UrlSafetyValidationResult.Deny($"DNS resolution failed for '{host}': {ex.Message}");
        }

        if (addresses.Length == 0)
            return UrlSafetyValidationResult.Deny($"DNS resolution returned no addresses for '{host}'.");

        return ValidateAddresses(addresses, policy, host);
    }

    private static UrlSafetyValidationResult ValidateAddresses(
        IReadOnlyList<IPAddress> addresses,
        UrlSafetyConfig policy,
        string host)
    {
        foreach (var address in addresses)
        {
            var normalized = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
            if (policy.BlockPrivateNetworkTargets && IsNonPublicAddress(normalized))
                return UrlSafetyValidationResult.Deny($"host '{host}' resolves to non-public address {normalized}.");

            foreach (var cidr in policy.BlockedCidrs.Where(static c => !string.IsNullOrWhiteSpace(c)))
            {
                if (AddressMatchesCidr(normalized, cidr.Trim()))
                    return UrlSafetyValidationResult.Deny($"host '{host}' resolves to {normalized}, which matches blocked CIDR '{cidr}'.");
            }
        }

        return UrlSafetyValidationResult.Allow;
    }

    private static string NormalizeHost(Uri uri)
        => uri.Host.Trim().TrimEnd('.').ToLowerInvariant();

    private static bool IsNonPublicAddress(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip))
            return true;

        if (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.None))
            return true;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = ip.GetAddressBytes();
            return bytes[0] == 0 ||
                   bytes[0] == 10 ||
                   bytes[0] == 127 ||
                   (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127) ||
                   (bytes[0] == 169 && bytes[1] == 254) ||
                   (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                   (bytes[0] == 192 && bytes[1] == 168) ||
                   (bytes[0] == 198 && (bytes[1] == 18 || bytes[1] == 19)) ||
                   bytes[0] >= 224;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast)
                return true;

            var bytes = ip.GetAddressBytes();
            return ip.Equals(IPAddress.IPv6None) ||
                   (bytes[0] & 0xFE) == 0xFC;
        }

        return true;
    }

    private static bool AddressMatchesCidr(IPAddress address, string cidr)
    {
        var separator = cidr.IndexOf('/', StringComparison.Ordinal);
        if (separator <= 0 || separator == cidr.Length - 1)
            return false;

        var networkText = cidr[..separator];
        var prefixText = cidr[(separator + 1)..];
        if (!IPAddress.TryParse(networkText, out var network) ||
            !int.TryParse(prefixText, out var prefixLength))
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (network.IsIPv4MappedToIPv6)
            network = network.MapToIPv4();

        if (address.AddressFamily != network.AddressFamily)
            return false;

        var addressBytes = address.GetAddressBytes();
        var networkBytes = network.GetAddressBytes();
        var maxPrefix = addressBytes.Length * 8;
        if (prefixLength < 0 || prefixLength > maxPrefix)
            return false;

        var fullBytes = prefixLength / 8;
        var remainingBits = prefixLength % 8;

        for (var i = 0; i < fullBytes; i++)
        {
            if (addressBytes[i] != networkBytes[i])
                return false;
        }

        if (remainingBits == 0)
            return true;

        var mask = (byte)(0xFF << (8 - remainingBits));
        return (addressBytes[fullBytes] & mask) == (networkBytes[fullBytes] & mask);
    }
}
