namespace NacosLiveAcceptance;

internal sealed record AcceptanceOptions(
    string ManagedPath,
    string NativePath,
    string TestDllPath,
    string OutputDirectory)
{
    private static readonly string[] RequiredOptions = ["--managed", "--native", "--test-dll", "--output"];

    public static AcceptanceOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            var option = args[index];
            if (!RequiredOptions.Contains(option, StringComparer.Ordinal))
            {
                throw new ArgumentException($"Unknown option: {option}");
            }

            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Missing value for {option}.");
            }

            if (!values.TryAdd(option, args[index + 1]))
            {
                throw new ArgumentException($"Option {option} was specified more than once.");
            }
        }

        foreach (var option in RequiredOptions)
        {
            if (!values.ContainsKey(option))
            {
                throw new ArgumentException($"Required option {option} is missing.");
            }
        }

        var managedPath = GetExistingFile(values["--managed"], "--managed");
        var nativePath = GetExistingFile(values["--native"], "--native");
        var testDllPath = GetExistingFile(values["--test-dll"], "--test-dll");
        var outputDirectory = Path.GetFullPath(values["--output"]);
        if (Directory.Exists(outputDirectory) || File.Exists(outputDirectory))
        {
            throw new ArgumentException($"Output path for --output already exists: {outputDirectory}");
        }

        return new AcceptanceOptions(managedPath, nativePath, testDllPath, outputDirectory);
    }

    private static string GetExistingFile(string path, string option)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new ArgumentException($"Input file for {option} does not exist: {fullPath}");
        }

        return fullPath;
    }
}