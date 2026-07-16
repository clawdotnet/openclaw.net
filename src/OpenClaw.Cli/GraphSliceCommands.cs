using System.Text.Json;
using OpenClaw.Core.Models;

namespace OpenClaw.Cli;

internal static class GraphSliceCommands
{
    public static async Task<int> RunAsync(string[] args)
        => await RunAsync(args, Console.Out, Console.Error);

    internal static async Task<int> RunAsync(
        string[] args,
        TextWriter output,
        TextWriter error)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintHelp(output);
            return 0;
        }

        var command = args[0].ToLowerInvariant();
        if (command != "slice")
        {
            PrintHelp(output);
            return 2;
        }

        var rest = args.Skip(1).ToArray();
        var parsed = CliArgs.Parse(rest);
        if (parsed.ShowHelp)
        {
            PrintHelp(output);
            return 0;
        }

        var profileName = parsed.GetOption("--profile");
        if (string.IsNullOrWhiteSpace(profileName))
        {
            await error.WriteLineAsync("--profile is required.");
            return 2;
        }

        var profile = LoadSliceProfile(profileName);
        if (profile is null)
        {
            await error.WriteLineAsync($"Profile '{profileName}' not found in graph-slice.json.");
            return 2;
        }

        // --info mode
        if (parsed.HasFlag("--info"))
        {
            PrintInfo(profileName, profile, output);
            return 0;
        }

        // --output override
        var outputPath = parsed.GetOption("--output");
        if (!string.IsNullOrWhiteSpace(outputPath))
            profile.Output.Path = outputPath;

        // --dry-run mode
        if (parsed.HasFlag("--dry-run"))
        {
            await output.WriteLineAsync(
                $"[dry-run] Profile: {profileName}, Sources: {profile.Sources.Count}, " +
                $"Output would be: {profile.Output.Path}");
            return 0;
        }

        // Execute
        var engine = new GraphSlicer.GraphSlicerEngine();
        var result = await engine.ExecuteAsync(profile, CancellationToken.None);

        if (!result.Success)
        {
            await error.WriteLineAsync($"Slice failed: {result.ErrorMessage}");
            return 1;
        }

        await output.WriteLineAsync($"Slice complete: {result.OutputPath} ({result.TripleCount} triples{(result.Truncated ? ", truncated" : "")})");
        return 0;
    }

    private static SliceProfile? LoadSliceProfile(string profileName)
    {
        var cwd = Directory.GetCurrentDirectory();
        var configPaths = new[]
        {
            Path.Combine(cwd, "graph-slice.json"),
            Path.Combine(AppContext.BaseDirectory, "graph-slice.json"),
        };

        foreach (var configPath in configPaths)
        {
            if (!File.Exists(configPath))
                continue;

            try
            {
                var json = File.ReadAllText(configPath);
                if (string.IsNullOrWhiteSpace(json))
                    continue;

                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("Profiles", out var profilesEl))
                    continue;
                if (!profilesEl.TryGetProperty(profileName, out var profileEl))
                    continue;

                return ParseSliceProfile(profileEl);
            }
            catch
            {
                // Skip unparseable config
            }
        }

        return null;
    }

    private static SliceProfile ParseSliceProfile(JsonElement el)
    {
        var profile = new SliceProfile();

        if (el.TryGetProperty("Sources", out var sourcesEl) && sourcesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var sourceEl in sourcesEl.EnumerateArray())
            {
                profile.Sources.Add(ParseSliceSourceConfig(sourceEl));
            }
        }

        if (el.TryGetProperty("Construct", out var constructEl))
            profile.Construct = constructEl.GetString() ?? "";

        if (el.TryGetProperty("FrameJson", out var frameEl) && frameEl.ValueKind == JsonValueKind.Object)
            profile.FrameJson = frameEl.GetRawText();

        if (el.TryGetProperty("Output", out var outputEl))
            profile.Output = ParseSliceOutputConfig(outputEl);

        return profile;
    }

    private static SliceSourceConfig ParseSliceSourceConfig(JsonElement el)
    {
        var source = new SliceSourceConfig();

        if (el.TryGetProperty("Kind", out var kindEl))
            source.Kind = kindEl.GetString() ?? "remote-endpoint";

        if (el.TryGetProperty("Endpoint", out var epEl))
            source.Endpoint = epEl.GetString();

        if (el.TryGetProperty("TimeoutSeconds", out var tsEl) && tsEl.TryGetInt32(out var ts))
            source.TimeoutSeconds = ts;

        if (el.TryGetProperty("DefaultGraphUri", out var dgEl))
            source.DefaultGraphUri = dgEl.GetString();

        if (el.TryGetProperty("Auth", out var authEl))
            source.Auth = ParseSliceAuthConfig(authEl);

        if (el.TryGetProperty("Paths", out var pathsEl) && pathsEl.ValueKind == JsonValueKind.Array)
        {
            source.Paths = [];
            foreach (var p in pathsEl.EnumerateArray())
                source.Paths.Add(p.GetString() ?? "");
        }

        if (el.TryGetProperty("NamedGraphUri", out var ngEl))
            source.NamedGraphUri = ngEl.GetString();

        return source;
    }

    private static SliceAuthConfig ParseSliceAuthConfig(JsonElement el)
    {
        var auth = new SliceAuthConfig();

        if (el.TryGetProperty("Type", out var typeEl))
            auth.Type = typeEl.GetString() ?? "none";

        if (el.TryGetProperty("UsernameEnv", out var ueEl))
            auth.UsernameEnv = ueEl.GetString();

        if (el.TryGetProperty("PasswordEnv", out var peEl))
            auth.PasswordEnv = peEl.GetString();

        return auth;
    }

    private static SliceOutputConfig ParseSliceOutputConfig(JsonElement el)
    {
        var output = new SliceOutputConfig();

        if (el.TryGetProperty("Path", out var pathEl))
            output.Path = pathEl.GetString() ?? output.Path;

        if (el.TryGetProperty("MaxTriples", out var mtEl) && mtEl.TryGetInt32(out var mt))
            output.MaxTriples = mt;

        if (el.TryGetProperty("Compaction", out var compEl) && (compEl.ValueKind == JsonValueKind.True || compEl.ValueKind == JsonValueKind.False))
            output.Compaction = compEl.GetBoolean();

        return output;
    }

    private static void PrintInfo(string profileName, SliceProfile profile, TextWriter output)
    {
        output.WriteLine($"Profile: {profileName}");
        output.WriteLine($"Sources: {profile.Sources.Count}");
        foreach (var source in profile.Sources)
        {
            var detail = source.Kind switch
            {
                "remote-endpoint" => source.Endpoint ?? "(no endpoint)",
                "local-files" => string.Join(", ", source.Paths ?? []),
                _ => "(unknown kind)"
            };
            output.WriteLine($"  - {source.Kind}: {detail}");
        }
        output.WriteLine($"Output: {profile.Output.Path}");
        output.WriteLine($"MaxTriples: {profile.Output.MaxTriples}");
        output.WriteLine($"Compaction: {profile.Output.Compaction}");
    }

    private static void PrintHelp(TextWriter output)
    {
        output.WriteLine("Usage: openclaw graph slice --profile <name> [options]");
        output.WriteLine();
        output.WriteLine("Commands:");
        output.WriteLine("  slice        Execute a graph slice profile");
        output.WriteLine();
        output.WriteLine("Options:");
        output.WriteLine("  --profile    Profile name from graph-slice.json (required)");
        output.WriteLine("  --output     Override output path");
        output.WriteLine("  --dry-run    Validate without writing output");
        output.WriteLine("  --info       Print profile configuration");
    }
}