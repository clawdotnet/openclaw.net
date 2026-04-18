using System.Net;

namespace OpenClaw.Core.Security;

public static class BindAddressClassifier
{
    public static bool IsLoopbackBind(string bindAddress)
    {
        // ASP.NET Core wildcard bind addresses mean "all interfaces" — not loopback
        if (bindAddress is "*" or "+" or "[::]")
            return false;

        if (IPAddress.TryParse(bindAddress, out var ip))
            return IPAddress.IsLoopback(ip);

        return string.Equals(bindAddress, "localhost", StringComparison.OrdinalIgnoreCase);
    }
}
