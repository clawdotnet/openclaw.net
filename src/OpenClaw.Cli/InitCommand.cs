using System.Text.Json;
using System.Text.Json.Nodes;
using OpenClaw.Core.Models;

namespace OpenClaw.Cli;

internal static class InitCommand
{
    public static int Run(string[] args)
    {
        var outputDir = Path.GetFullPath(".openclaw-init");
        var preset = "both";
        var force = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "-h":
                case "--help":
                    PrintHelp();
                    return 0;
                case "--output":
                    if (i + 1 >= args.Length)
                        throw new ArgumentException("Missing value for --output");
                    outputDir = Path.GetFullPath(args[++i]);
                    break;
                case "--preset":
                    if (i + 1 >= args.Length)
                        throw new ArgumentException("Missing value for --preset");
                    preset = args[++i].Trim().ToLowerInvariant();
                    if (preset is not ("local" or "public" or "both"))
                        throw new ArgumentException("Invalid value for --preset (expected: local|public|both)");
                    break;
                case "--force":
                    force = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown option: {arg}");
            }
        }

        if (Directory.Exists(outputDir) && Directory.EnumerateFileSystemEntries(outputDir).Any() && !force)
        {
            throw new ArgumentException($"Output directory '{outputDir}' is not empty. Re-run with --force to overwrite generated files.");
        }

        Directory.CreateDirectory(outputDir);
        Directory.CreateDirectory(Path.Combine(outputDir, "workspace"));
        Directory.CreateDirectory(Path.Combine(outputDir, "memory"));
        Directory.CreateDirectory(Path.Combine(outputDir, "deploy"));

        Write(Path.Combine(outputDir, ".env.example"), BuildEnvExample(outputDir));

        if (preset is "local" or "both")
            Write(Path.Combine(outputDir, "config.local.json"), BuildLocalConfig());
        if (preset is "public" or "both")
        {
            Write(Path.Combine(outputDir, "config.public.json"), BuildPublicConfig());
            Write(Path.Combine(outputDir, "deploy", "Caddyfile.sample"), BuildCaddyfileSample());
            Write(Path.Combine(outputDir, "deploy", "docker-compose.override.sample.yml"), BuildDockerOverrideSample());
        }

        Console.WriteLine($"Initialized OpenClaw bootstrap files in {outputDir}");
        Console.WriteLine($"- preset: {preset}");
        Console.WriteLine("- workspace/: ready for file tools");
        Console.WriteLine("- memory/: ready for memory/session persistence");
        Console.WriteLine("- .env.example: provider/auth placeholders (fill in secrets there instead of passing them on the CLI)");
        Console.WriteLine("- config.local.json/config.public.json: ready-to-edit presets");
        return 0;
    }

    private static void PrintHelp()
    {
        Console.WriteLine(
            """
            openclaw init

            Usage:
              openclaw init [--preset local|public|both] [--output <dir>] [--force]

            Defaults:
              --preset both
              --output ./.openclaw-init
            """);
    }

    private static void Write(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }

    private static string BuildEnvExample(string outputDir)
        => $"""
MODEL_PROVIDER_KEY=replace-me
OPENCLAW_AUTH_TOKEN=replace-me
OPENCLAW_WORKSPACE={Path.Combine(outputDir, "workspace")}
""";

    private static string BuildLocalConfig()
        => SerializeConfig(
            BootstrapConfigFactory.CreateProfileConfig(
                "local",
                "127.0.0.1",
                18789,
                authToken: "",
                workspacePath: "./workspace",
                memoryPath: "./memory",
                provider: "openai",
                model: new GatewayConfig().Llm.Model,
                apiKey: "env:MODEL_PROVIDER_KEY"));

    private static string BuildPublicConfig()
        => SerializeConfig(
            BootstrapConfigFactory.CreateProfileConfig(
                "public",
                "0.0.0.0",
                18789,
                authToken: "",
                workspacePath: "/app/workspace",
                memoryPath: "/app/memory",
                provider: "openai",
                model: new GatewayConfig().Llm.Model,
                apiKey: "env:MODEL_PROVIDER_KEY"));

    private static string SerializeConfig(GatewayConfig config)
    {
        var payload = new JsonObject
        {
            ["OpenClaw"] = JsonNode.Parse(JsonSerializer.Serialize(config, CoreJsonContext.Default.GatewayConfig))
        };

        return payload.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static string BuildCaddyfileSample()
        => """
:443 {
    encode zstd gzip

    reverse_proxy 127.0.0.1:18789
}
""";

    private static string BuildDockerOverrideSample()
        => """
services:
  openclaw:
    environment:
      - OPENCLAW_AUTH_TOKEN=${OPENCLAW_AUTH_TOKEN}
      - OpenClaw__BindAddress=0.0.0.0
      - OpenClaw__Tooling__AllowShell=false
      - OpenClaw__Tooling__AllowedReadRoots__0=/app/workspace
      - OpenClaw__Tooling__AllowedWriteRoots__0=/app/workspace
      - OpenClaw__Plugins__Enabled=false
""";
}
