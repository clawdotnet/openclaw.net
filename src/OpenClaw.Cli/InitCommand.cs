using System.Security.Cryptography;

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

        var authToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        Write(Path.Combine(outputDir, ".env.example"), BuildEnvExample(authToken));

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
        Console.WriteLine($"- auth token: {authToken}");
        Console.WriteLine("- workspace/: ready for file tools");
        Console.WriteLine("- memory/: ready for memory/session persistence");
        Console.WriteLine("- .env.example: provider/auth placeholders");
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

    private static string BuildEnvExample(string authToken)
        => $"""
MODEL_PROVIDER_KEY=replace-me
OPENCLAW_AUTH_TOKEN={authToken}
OPENCLAW_WORKSPACE={Path.GetFullPath(".openclaw-init/workspace")}
""";

    private static string BuildLocalConfig()
        => """
{
  "OpenClaw": {
    "BindAddress": "127.0.0.1",
    "Port": 18789,
    "Memory": {
      "Provider": "file",
      "StoragePath": "./memory"
    },
    "Security": {
      "AllowQueryStringToken": false,
      "BrowserSessionIdleMinutes": 60,
      "BrowserRememberDays": 30
    },
    "Tooling": {
      "WorkspaceRoot": "./workspace",
      "WorkspaceOnly": true,
      "AllowShell": true,
      "AllowedReadRoots": [ "./workspace" ],
      "AllowedWriteRoots": [ "./workspace" ]
    },
    "Sandbox": {
      "Provider": "None"
    },
    "Plugins": {
      "Enabled": false
    }
  }
}
""";

    private static string BuildPublicConfig()
        => """
{
  "OpenClaw": {
    "BindAddress": "0.0.0.0",
    "Port": 18789,
    "Memory": {
      "Provider": "file",
      "StoragePath": "/app/memory"
    },
    "Security": {
      "AllowQueryStringToken": false,
      "BrowserSessionIdleMinutes": 60,
      "BrowserRememberDays": 30,
      "TrustForwardedHeaders": true
    },
    "Tooling": {
      "WorkspaceRoot": "/app/workspace",
      "WorkspaceOnly": true,
      "AllowShell": false,
      "AllowedReadRoots": [ "/app/workspace" ],
      "AllowedWriteRoots": [ "/app/workspace" ]
    },
    "Sandbox": {
      "Provider": "None"
    },
    "Plugins": {
      "Enabled": false
    }
  }
}
""";

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
