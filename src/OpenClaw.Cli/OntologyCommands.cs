using System.Text.Json;
using OpenClaw.Core.Models;
using OpenClaw.Ontology;
using VDS.RDF;
using VDS.RDF.Parsing;

namespace OpenClaw.Cli;

internal static class OntologyCommands
{
    public static async Task<int> RunAsync(string[] args)
        => await RunAsync(args, Console.Out, Console.Error);

    internal static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintHelp(output);
            return 0;
        }

        var command = args[0].ToLowerInvariant();
        if (command == "build")
            return await BuildAsync(args.Skip(1).ToArray(), output, error);
        if (command == "validate")
            return await ValidateAsync(args.Skip(1).ToArray(), output, error);
        if (command == "versions" || command == "trace")
            return await VersionsAsync(args.Skip(1).ToArray(), output, error);

        PrintHelp(output);
        return 2;
    }

    private static async Task<int> BuildAsync(string[] args, TextWriter output, TextWriter error)
    {
        var parsed = CliArgs.Parse(args);
        if (parsed.ShowHelp)
        {
            PrintHelp(output);
            return 0;
        }

        var profileName = parsed.GetOption("--profile") ?? "standard";
        var format = parsed.GetOption("--format") ?? "turtle";
        var outputPath = parsed.GetOption("--output");

        // Load config (ontology.json profiles)
        var config = LoadConfig(profileName);
        if (config is null && profileName != "standard")
        {
            await error.WriteLineAsync(
                $"Profile '{profileName}' not found in ontology.json. " +
                "Use --profile standard for the built-in GB/T 48000.3 ontology.");
            return 2;
        }

        config ??= new OntologyBuildConfig
        {
            Namespace = StandardOntology.StandardOntology.Namespace,
            OutputFormat = format,
            OutputPath = outputPath ?? "./tmp/standard-ontology.ttl",
            IncludeStandard = true,
            Comment = "GB/T 48000.3-2026 标准数字化核心本体"
        };

        if (!string.IsNullOrWhiteSpace(outputPath))
            config.OutputPath = outputPath;
        if (!string.IsNullOrWhiteSpace(format))
            config.OutputFormat = format;

        var ontologyFormat = config.OutputFormat.ToLowerInvariant() switch
        {
            "turtle" => OntologyOutputFormat.Turtle,
            "jsonld" => OntologyOutputFormat.JsonLd,
            "rdfxml" => OntologyOutputFormat.RdfXml,
            _ => OntologyOutputFormat.Turtle
        };

        // Build ontology
        var standardOnt = new StandardOntology.StandardOntology();
        var builder = standardOnt.Build();

        if (!string.IsNullOrWhiteSpace(config.Comment))
            builder.WithHeader(config.Namespace, comment: config.Comment);

        // Write output
        var dir = Path.GetDirectoryName(config.OutputPath);
        if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        builder.WriteToFile(config.OutputPath, ontologyFormat);

        var builtGraph = builder.Build();
        var tripleCount = builtGraph.Triples.Count;

        await output.WriteLineAsync(
            $"Ontology built: {config.OutputPath} ({tripleCount} triples, format={config.OutputFormat})");
        return 0;
    }

    private static async Task<int> ValidateAsync(string[] args, TextWriter output, TextWriter error)
    {
        var parsed = CliArgs.Parse(args);
        if (parsed.ShowHelp)
        {
            PrintHelp(output);
            return 0;
        }

        var dataPath = parsed.GetOption("--data");
        var shapesPath = parsed.GetOption("--shapes");
        var profileName = parsed.GetOption("--profile") ?? "standard";

        // Determine data source: --data file, or build from profile
        IGraph dataGraph;
        if (!string.IsNullOrWhiteSpace(dataPath))
        {
            if (!File.Exists(dataPath))
            {
                await error.WriteLineAsync($"Data file not found: {dataPath}");
                return 1;
            }
            dataGraph = new VDS.RDF.Graph();
            VDS.RDF.Parsing.FileLoader.Load(dataGraph, dataPath);
        }
        else
        {
            // Build ontology from standard profile as validation target
            var standardOnt = new StandardOntology.StandardOntology();
            var builder = standardOnt.Build();
            dataGraph = builder.Build();
        }

        // Determine shapes source: --shapes file, or use built-in standard shapes
        IGraph shapesGraph;
        if (!string.IsNullOrWhiteSpace(shapesPath))
        {
            if (!File.Exists(shapesPath))
            {
                await error.WriteLineAsync($"Shapes file not found: {shapesPath}");
                return 1;
            }
            shapesGraph = new VDS.RDF.Graph();
            VDS.RDF.Parsing.FileLoader.Load(shapesGraph, shapesPath);
        }
        else
        {
            shapesGraph = StandardOntology.StandardShapes.BuildShapesGraph();
        }

        // Run SHACL validation
        var validator = new ShaclValidator();
        var report = validator.Validate(dataGraph, shapesGraph);

        // Print report
        await output.WriteLineAsync($"Conforms: {report.Conforms}");
        await output.WriteLineAsync($"Results: {report.ResultCount}");

        foreach (var result in report.Results)
        {
            var icon = result.Severity switch
            {
                ShaclSeverity.Violation => "❌",
                ShaclSeverity.Warning => "⚠️",
                _ => "ℹ️"
            };
            await output.WriteLineAsync(
                $"  {icon} [{result.Severity}] {result.Message}");
            if (!string.IsNullOrWhiteSpace(result.ResultPath))
                await output.WriteLineAsync($"      Path: {result.ResultPath}");
            await output.WriteLineAsync($"      Node: {result.FocusNode}");
        }

        return report.Conforms ? 0 : 1;
    }

    private static async Task<int> VersionsAsync(string[] args, TextWriter output, TextWriter error)
    {
        var parsed = CliArgs.Parse(args);
        if (parsed.ShowHelp) { PrintHelp(output); return 0; }

        var dataPath = parsed.GetOption("--data");
        if (string.IsNullOrWhiteSpace(dataPath))
        { await error.WriteLineAsync("--data is required (path to RDF data file)."); return 2; }
        if (!File.Exists(dataPath))
        { await error.WriteLineAsync($"Data file not found: {dataPath}"); return 1; }

        var stdIri = parsed.GetOption("--standard");
        var diffOld = parsed.GetOption("--diff-old");
        var diffNew = parsed.GetOption("--diff-new");

        var graph = StandardOntology.VersionTracer.LoadGraph(dataPath);

        // Diff mode
        if (!string.IsNullOrWhiteSpace(diffOld) && !string.IsNullOrWhiteSpace(diffNew))
        {
            var diff = StandardOntology.VersionTracer.Diff(graph, diffOld, diffNew);
            await output.WriteLineAsync($"Diff: {diff.OldIri} → {diff.NewIri}");
            await output.WriteLineAsync($"  Added:   {diff.AddedProperties.Count}");
            foreach (var a in diff.AddedProperties) await output.WriteLineAsync($"    + {a}");
            await output.WriteLineAsync($"  Removed: {diff.RemovedProperties.Count}");
            foreach (var r in diff.RemovedProperties) await output.WriteLineAsync($"    - {r}");
            await output.WriteLineAsync($"  Changed: {diff.ChangedProperties.Count}");
            foreach (var c in diff.ChangedProperties) await output.WriteLineAsync($"    ~ {c}");
            return 0;
        }

        // Version chain mode
        if (!string.IsNullOrWhiteSpace(stdIri))
        {
            var chain = StandardOntology.VersionTracer.TraceReplacesChain(graph, stdIri);
            if (chain.Count == 0)
            { await output.WriteLineAsync("No version chain found."); return 0; }
            await output.WriteLineAsync($"Version chain ({chain.Count} entries, newest first):");
            for (int i = 0; i < chain.Count; i++)
            {
                var prefix = i == 0 ? "  ▶ " : "    ";
                await output.WriteLineAsync($"{prefix}{chain[i].StandardNumber} — {chain[i].StandardName} (v{chain[i].VersionNumber})");
            }
            return 0;
        }

        await error.WriteLineAsync("Use --standard <IRI> to trace versions, or --diff-old/--diff-new to compare.");
        return 2;
    }

    private static OntologyBuildConfig? LoadConfig(string profileName)
    {
        var cwd = Directory.GetCurrentDirectory();
        var configPaths = new[]
        {
            Path.Combine(cwd, "ontology.json"),
            Path.Combine(AppContext.BaseDirectory, "ontology.json"),
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

                return ParseBuildConfig(profileEl);
            }
            catch
            {
                // Skip unparseable
            }
        }

        return null;
    }

    private static OntologyBuildConfig ParseBuildConfig(JsonElement el)
    {
        var config = new OntologyBuildConfig();

        if (el.TryGetProperty("Namespace", out var nsEl))
            config.Namespace = nsEl.GetString() ?? config.Namespace;
        if (el.TryGetProperty("OutputFormat", out var fmtEl))
            config.OutputFormat = fmtEl.GetString() ?? config.OutputFormat;
        if (el.TryGetProperty("OutputPath", out var pathEl))
            config.OutputPath = pathEl.GetString() ?? config.OutputPath;
        if (el.TryGetProperty("IncludeStandard", out var stdEl) &&
            (stdEl.ValueKind == JsonValueKind.True || stdEl.ValueKind == JsonValueKind.False))
            config.IncludeStandard = stdEl.GetBoolean();
        if (el.TryGetProperty("Comment", out var cmtEl))
            config.Comment = cmtEl.GetString();

        return config;
    }

    private static void PrintHelp(TextWriter output)
    {
        output.WriteLine("Usage: openclaw ontology <command> [options]");
        output.WriteLine();
        output.WriteLine("Commands:");
        output.WriteLine("  build        Build an ontology from a profile");
        output.WriteLine("  validate     Validate ontology data against SHACL shapes");
        output.WriteLine("  versions     Trace version chains (replaces/isReplacedBy)");
        output.WriteLine("  trace        Alias for versions");
        output.WriteLine();
        output.WriteLine("Build options:");
        output.WriteLine("  --profile    Profile name from ontology.json (default: standard)");
        output.WriteLine("  --format     Output format: turtle, jsonld, rdfxml (default: turtle)");
        output.WriteLine("  --output     Override output file path");
        output.WriteLine();
        output.WriteLine("Validate options:");
        output.WriteLine("  --profile    Build ontology from profile and validate (default: standard)");
        output.WriteLine("  --data       Path to RDF data file to validate (overrides --profile)");
        output.WriteLine("  --shapes     Path to SHACL shapes file (default: built-in standard shapes)");
        output.WriteLine();
        output.WriteLine("Examples:");
        output.WriteLine("  openclaw ontology build --profile standard");
        output.WriteLine("  openclaw ontology build --profile standard --format turtle --output ./tmp/ontology.ttl");
        output.WriteLine("  openclaw ontology validate --profile standard");
        output.WriteLine("  openclaw ontology validate --data ./tmp/my-instances.ttl --shapes ./tmp/my-shapes.ttl");
        output.WriteLine("  openclaw ontology versions --data ./tmp/instances.ttl --standard std:GB-T-12345");
        output.WriteLine("  openclaw ontology versions --data ./tmp/instances.ttl --diff-old std:v1 --diff-new std:v2");
    }
}