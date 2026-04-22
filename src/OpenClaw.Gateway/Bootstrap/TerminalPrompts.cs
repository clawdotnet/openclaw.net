namespace OpenClaw.Gateway.Bootstrap;

internal static class TerminalPrompts
{
    public static bool IsInteractiveConsole()
        => Environment.UserInteractive && !Console.IsInputRedirected && !Console.IsOutputRedirected;

    public static string Prompt(TextWriter output, TextReader input, string label, string defaultValue)
    {
        output.Write($"{label} [{defaultValue}]: ");
        var value = input.ReadLine();
        return string.IsNullOrWhiteSpace(value) ? defaultValue : value.Trim();
    }

    public static string PromptRequired(TextWriter output, TextReader input, string label)
    {
        while (true)
        {
            output.Write($"{label}: ");
            var value = input.ReadLine()?.Trim();
            if (!string.IsNullOrWhiteSpace(value))
                return value;

            output.WriteLine("A value is required.");
        }
    }

    public static string PromptRequiredSecret(TextWriter output, TextReader input, string label)
    {
        if (ReferenceEquals(input, Console.In) && ReferenceEquals(output, Console.Out) && !Console.IsInputRedirected)
            return ReadSecretFromConsole(label);

        return PromptRequired(output, input, label);
    }

    public static bool PromptYesNo(TextWriter output, TextReader input, string question, bool defaultValue)
    {
        while (true)
        {
            output.Write($"{question} [{(defaultValue ? "Y/n" : "y/N")}]: ");
            var raw = input.ReadLine();
            if (string.IsNullOrWhiteSpace(raw))
                return defaultValue;

            switch (raw.Trim().ToLowerInvariant())
            {
                case "y":
                case "yes":
                    return true;
                case "n":
                case "no":
                    return false;
                default:
                    output.WriteLine("Please answer y or n.");
                    break;
            }
        }
    }

    public static int PromptPort(TextWriter output, TextReader input, int defaultPort)
    {
        while (true)
        {
            var value = Prompt(output, input, "Port", defaultPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var port) &&
                port is >= 1 and <= 65535)
            {
                return port;
            }

            output.WriteLine("Enter a valid TCP port between 1 and 65535.");
        }
    }

    private static string ReadSecretFromConsole(string label)
    {
        while (true)
        {
            Console.Write($"{label}: ");
            var buffer = new Stack<char>();
            while (true)
            {
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter)
                {
                    Console.WriteLine();
                    break;
                }

                if (key.Key == ConsoleKey.Backspace)
                {
                    if (buffer.Count == 0)
                        continue;

                    buffer.Pop();
                    Console.Write("\b \b");
                    continue;
                }

                if (!char.IsControl(key.KeyChar))
                {
                    buffer.Push(key.KeyChar);
                    Console.Write('*');
                }
            }

            var value = new string([.. buffer.Reverse()]);
            if (!string.IsNullOrWhiteSpace(value))
                return value;

            Console.WriteLine("A value is required.");
        }
    }
}
