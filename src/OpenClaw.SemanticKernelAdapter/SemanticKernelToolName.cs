using System.Security.Cryptography;
using System.Text;

namespace OpenClaw.SemanticKernelAdapter;

internal static class SemanticKernelToolName
{
    public static string MakeToolName(string prefix, string pluginName, string functionName, int maxLen)
    {
        prefix ??= "sk_";
        maxLen = Math.Clamp(maxLen, 16, 256);

        var raw = (prefix + pluginName + "_" + functionName).ToLowerInvariant();
        var sb = new StringBuilder(raw.Length);

        foreach (var ch in raw)
        {
            // Keep conservative charset for tool names.
            if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '_' || ch == '-')
                sb.Append(ch);
            else
                sb.Append('_');
        }

        var name = sb.ToString();

        if (name.Length <= maxLen)
            return name;

        // If too long, keep prefix+head and add a stable hash suffix.
        var hash = StableHashSuffix(pluginName + ":" + functionName);
        var suffix = "_" + hash;
        var keep = Math.Max(prefix.Length + 8, maxLen - suffix.Length);
        name = name[..Math.Min(name.Length, keep)] + suffix;

        return name.Length <= maxLen ? name : name[..maxLen];
    }

    private static string StableHashSuffix(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        // 8 hex chars is enough to avoid collisions for this use.
        return Convert.ToHexString(bytes)[..8].ToLowerInvariant();
    }
}
