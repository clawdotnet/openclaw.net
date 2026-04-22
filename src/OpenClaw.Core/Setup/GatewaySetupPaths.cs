namespace OpenClaw.Core.Setup;

public static class GatewaySetupPaths
{
    public const string DefaultConfigPath = "~/.openclaw/config/openclaw.settings.json";
    public const string DefaultLocalStartupStatePath = "~/.openclaw/state/local-startup.json";

    public static string ExpandPath(string path)
    {
        if (path.StartsWith("~/", StringComparison.Ordinal) || string.Equals(path, "~", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return path.Length == 1 ? home : Path.Combine(home, path[2..]);
        }

        return path;
    }

    public static string QuoteIfNeeded(string path)
        => path.Contains(' ', StringComparison.Ordinal) ? $"\"{path}\"" : path;

    public static string ResolveDefaultConfigPath()
        => Path.GetFullPath(ExpandPath(DefaultConfigPath));

    public static string ResolveDefaultLocalStartupStatePath()
        => Path.GetFullPath(ExpandPath(DefaultLocalStartupStatePath));
}
