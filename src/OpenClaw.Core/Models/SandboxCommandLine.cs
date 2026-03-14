using System.Text;

namespace OpenClaw.Core.Models;

public static class SandboxCommandLine
{
    public static string Quote(string value)
        => "'" + (value ?? string.Empty).Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    public static string BuildCommand(string command, IEnumerable<string>? arguments = null)
    {
        var builder = new StringBuilder();
        builder.Append(Quote(command));

        if (arguments is null)
            return builder.ToString();

        foreach (var argument in arguments)
        {
            builder.Append(' ');
            builder.Append(Quote(argument));
        }

        return builder.ToString();
    }

    public static string WrapWithTimeout(string command, IEnumerable<string>? arguments, int timeoutSeconds)
    {
        var effectiveTimeout = Math.Clamp(timeoutSeconds, 1, 3600);
        var baseCommand = BuildCommand(command, arguments);
        return $"if command -v timeout >/dev/null 2>&1; then exec timeout {effectiveTimeout}s {baseCommand}; else exec {baseCommand}; fi";
    }
}
