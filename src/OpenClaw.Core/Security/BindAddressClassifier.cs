using System.Net;

namespace OpenClaw.Core.Security;

public static class BindAddressClassifier
{
    public static bool IsLoopbackBind(string bindAddress)
    {
        if (IPAddress.TryParse(bindAddress, out var ip))
            return IPAddress.IsLoopback(ip);

        return string.Equals(bindAddress, "localhost", StringComparison.OrdinalIgnoreCase);
    }
}
