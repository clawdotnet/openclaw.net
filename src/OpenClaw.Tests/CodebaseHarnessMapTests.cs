using System.Text.Json;
using OpenClaw.Cli;
using OpenClaw.Core.Features;
using OpenClaw.Core.Models;
using Xunit;

namespace OpenClaw.Tests;

public sealed class CodebaseHarnessMapTests
{
    [Fact]
    public async Task MapService_ScansDotNetRepoAndDetectsCoreSurfaces()
    {
        var root = CreateFixtureRepo();
        try
        {
            var map = await new CodebaseHarnessMapService().GenerateAsync(root);

            Assert.Equal(1, map.Summary.SolutionFilesCount);
            Assert.True(map.Summary.ProjectFilesCount >= 2);
            Assert.Equal(1, map.Summary.TestProjectsCount);
            Assert.Contains(map.Projects, project => project.Name == "OpenClaw.Gateway" && !project.IsTestProject);
            Assert.Contains(map.Projects, project => project.Name == "OpenClaw.Tests" && project.IsTestProject);
            Assert.Contains(map.Modules, module => module.Kind == CodebaseMapModuleKinds.Gateway);
            Assert.Contains(map.Endpoints, endpoint => endpoint.Method == "GET" && endpoint.Path == "/health");
            Assert.Contains(map.Endpoints, endpoint => endpoint.Method == "POST" && endpoint.Path == "/items");
            Assert.Contains(map.ConfigSurfaces, surface => surface.Key == "OpenClaw:Llm:ApiKey" && surface.Sensitive);

            var json = JsonSerializer.Serialize(map, CoreJsonContext.Default.CodebaseHarnessMap);
            Assert.DoesNotContain("super-secret", json, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MapService_HandlesMalformedProjectWithDiagnostic()
    {
        var root = CreateFixtureRepo(malformedProject: true);
        try
        {
            var map = await new CodebaseHarnessMapService().GenerateAsync(root);

            Assert.Contains(map.Diagnostics, diagnostic => diagnostic.Code == "project_parse_failed");
            Assert.Contains(map.Projects, project => project.Name == "Broken");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MapService_SkipsSymlinkedDirectories()
    {
        var root = CreateFixtureRepo();
        var outside = CreateTempDirectory();
        try
        {
            WriteFile(Path.Join(outside, "Outside.cs"), "public sealed class OutsideTool { }");
            var linkPath = Path.Join(root, "linked-outside");
            try
            {
                Directory.CreateSymbolicLink(linkPath, outside);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return;
            }

            var map = await new CodebaseHarnessMapService().GenerateAsync(root);

            Assert.DoesNotContain(map.Artifacts, artifact => artifact.Path.Contains("Outside.cs", StringComparison.Ordinal));
            Assert.Contains(map.Diagnostics, diagnostic => diagnostic.Code == "directory_reparse_point_skipped");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public async Task HarnessMapCommand_RendersTextAndJson()
    {
        var root = CreateFixtureRepo();
        try
        {
            using var textOutput = new StringWriter();
            using var textError = new StringWriter();
            var textExit = await HarnessCommands.RunAsync(["map", "--root", root, "--category", "endpoints"], textOutput, textError);

            Assert.Equal(0, textExit);
            Assert.Equal(string.Empty, textError.ToString());
            Assert.Contains("OpenClaw Codebase Harness Map", textOutput.ToString(), StringComparison.Ordinal);
            Assert.Contains("Summary:", textOutput.ToString(), StringComparison.Ordinal);
            Assert.Contains("GET /health", textOutput.ToString(), StringComparison.Ordinal);

            using var projectsOutput = new StringWriter();
            using var projectsError = new StringWriter();
            var projectsExit = await HarnessCommands.RunAsync(["map", "--root", root, "--category", "projects"], projectsOutput, projectsError);

            Assert.Equal(0, projectsExit);
            Assert.Equal(string.Empty, projectsError.ToString());
            Assert.Contains("- Endpoints: 0", projectsOutput.ToString(), StringComparison.Ordinal);

            using var jsonOutput = new StringWriter();
            using var jsonError = new StringWriter();
            var jsonExit = await HarnessCommands.RunAsync(["map", "--root", root, "--json"], jsonOutput, jsonError);

            Assert.Equal(0, jsonExit);
            var restored = JsonSerializer.Deserialize(jsonOutput.ToString(), CoreJsonContext.Default.CodebaseHarnessMap);
            Assert.NotNull(restored);
            Assert.Equal(Path.GetFullPath(root), restored!.RepositoryRoot);
            Assert.Contains(restored.Endpoints, endpoint => endpoint.Path == "/health");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateFixtureRepo(bool malformedProject = false)
    {
        var root = CreateTempDirectory();
        WriteFile(Path.Join(root, "OpenClaw.Net.slnx"), "<Solution></Solution>");
        WriteFile(
            Path.Join(root, "src", "OpenClaw.Gateway", "OpenClaw.Gateway.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.AspNetCore.OpenApi" Version="10.0.0" />
              </ItemGroup>
            </Project>
            """);
        WriteFile(
            Path.Join(root, "src", "OpenClaw.Gateway", "Program.cs"),
            """
            var builder = WebApplication.CreateBuilder(args);
            var app = builder.Build();
            app.MapGet("/health", () => "ok").RequireAuthorization();
            app.MapPost("/items", () => Results.Ok());
            app.Run();
            """);
        WriteFile(
            Path.Join(root, "tests", "OpenClaw.Tests", "OpenClaw.Tests.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="xunit" Version="2.9.3" />
              </ItemGroup>
            </Project>
            """);
        WriteFile(
            Path.Join(root, "appsettings.json"),
            """
            {
              "OpenClaw": {
                "Llm": {
                  "Provider": "openai",
                  "ApiKey": "super-secret"
                },
                "Memory": {
                  "StoragePath": "./memory"
                }
              }
            }
            """);

        if (malformedProject)
            WriteFile(Path.Join(root, "src", "Broken", "Broken.csproj"), "<Project><PropertyGroup>");

        return root;
    }

    private static string CreateTempDirectory()
    {
        var folderName = Path.GetFileName($"openclaw-codebase-map-tests-{Guid.NewGuid():N}");
        if (string.IsNullOrWhiteSpace(folderName) || Path.IsPathRooted(folderName))
            throw new InvalidOperationException("Generated codebase map test directory name must be relative.");

        var root = Path.GetFullPath(Path.Join(Path.GetTempPath(), folderName));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void WriteFile(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllText(path, content);
    }
}
