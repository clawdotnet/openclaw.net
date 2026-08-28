using Xunit;
using OpenClaw.Core.Plugins;

namespace OpenClaw.Tests;

public class AgentPluginDiscoveryTests
{
    [Fact]
    public void DiscoverAgentPlugins_FindsValidPlugin()
    {
        // 创建临时测试插件目录
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        try
        {
            var pluginDir = Path.Combine(tempDir, "test-plugin");
            Directory.CreateDirectory(pluginDir);
            File.WriteAllText(Path.Combine(pluginDir, "plugin.json"), @"{
                ""name"": ""test-plugin"",
                ""version"": ""1.0.0"",
                ""description"": ""Test plugin"",
                ""license"": ""MIT""
            }");

            var config = new PluginsConfig
            {
                Load = new PluginLoadConfig { Paths = [tempDir] }
            };

            var packages = PluginDiscovery.DiscoverAgentPlugins(config);

            Assert.Single(packages);
            Assert.Equal("test-plugin", packages[0].Manifest.Name);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void DiscoverAgentPlugins_RejectsMissingManifest()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        try
        {
            var pluginDir = Path.Combine(tempDir, "invalid-plugin");
            Directory.CreateDirectory(pluginDir);
            // 没有 plugin.json

            var config = new PluginsConfig
            {
                Load = new PluginLoadConfig { Paths = [tempDir] }
            };

            var packages = PluginDiscovery.DiscoverAgentPlugins(config);

            Assert.Empty(packages);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void DiscoverAgentPlugins_RequiresFields()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        try
        {
            var pluginDir = Path.Combine(tempDir, "incomplete-plugin");
            Directory.CreateDirectory(pluginDir);
            File.WriteAllText(Path.Combine(pluginDir, "plugin.json"), @"{
                ""name"": ""incomplete-plugin""
            }");

            var config = new PluginsConfig
            {
                Load = new PluginLoadConfig { Paths = [tempDir] }
            };

            var result = PluginDiscovery.DiscoverAgentPluginsWithDiagnostics(config);

            Assert.Empty(result.Packages);
            Assert.Single(result.Reports);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void DiscoverAgentPlugins_UnknownSchemaDollar_WarnsAndLoads()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        try
        {
            var pluginDir = Path.Combine(tempDir, "schema-plugin");
            Directory.CreateDirectory(pluginDir);
            // Spec-canonical property name is $schema; a mismatched value must not block
            // loading but must surface an unknown_schema warning.
            File.WriteAllText(Path.Combine(pluginDir, "plugin.json"), """
            {
              "$schema": "https://example.invalid/schema.json",
              "name": "schema-plugin",
              "version": "1.0.0",
              "description": "Test plugin",
              "license": "MIT"
            }
            """);

            var config = new PluginsConfig
            {
                Load = new PluginLoadConfig { Paths = [tempDir] }
            };

            var result = PluginDiscovery.DiscoverAgentPluginsWithDiagnostics(config);

            // The warning is non-fatal: the package still loads...
            Assert.Contains(result.Packages, p => p.Manifest.Name == "schema-plugin");
            // ...but the unknown-schema warning is surfaced on the report, not silently dropped.
            var report = Assert.Single(result.Reports);
            var warning = Assert.Single(report.Diagnostics);
            Assert.Equal("warning", warning.Severity);
            Assert.Equal("unknown_schema", warning.Code);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void AgentPluginSkillLoader_FindsSkillsInDirectSubdirs()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        try
        {
            var pluginDir = Path.Combine(tempDir, "test-plugin");
            Directory.CreateDirectory(pluginDir);
            File.WriteAllText(Path.Combine(pluginDir, "plugin.json"), @"{
                ""name"": ""test-plugin"",
                ""version"": ""1.0.0"",
                ""description"": ""Test"",
                ""license"": ""MIT""
            }");

            var skillsDir = Path.Combine(pluginDir, "skills");
            Directory.CreateDirectory(skillsDir);

            var validSkillDir = Path.Combine(skillsDir, "valid-skill");
            Directory.CreateDirectory(validSkillDir);
            File.WriteAllText(Path.Combine(validSkillDir, "SKILL.md"), "# Valid Skill");

            var invalidSkillDir = Path.Combine(skillsDir, "invalid-skill");
            Directory.CreateDirectory(invalidSkillDir);
            // 没有 SKILL.md

            var pkg = new AgentPluginPackage
            {
                Manifest = new AgentPluginManifest
                {
                    Name = "test-plugin",
                    Version = "1.0.0",
                    Description = "Test",
                    License = "MIT"
                },
                RootPath = pluginDir,
                SkillsPath = skillsDir
            };

            var diagnostics = AgentPluginSkillLoader.ValidateSkills(pkg, out var skillNames);

            Assert.Single(skillNames);
            Assert.Equal("valid-skill", skillNames[0]);
            Assert.Single(diagnostics);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void AgentPluginMcpAdapter_ExpandsVariables()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        try
        {
            var pluginDir = Path.Combine(tempDir, "test-plugin");
            Directory.CreateDirectory(pluginDir);
            File.WriteAllText(Path.Combine(pluginDir, "plugin.json"), @"{
                ""name"": ""test-plugin"",
                ""version"": ""1.0.0"",
                ""description"": ""Test"",
                ""license"": ""MIT""
            }");

            File.WriteAllText(Path.Combine(pluginDir, "mcp.json"), @"{
                ""mcpServers"": {
                    ""test-server"": {
                        ""command"": ""npx"",
                        ""args"": [""-y"", ""${PLUGIN_ROOT}/server.js""],
                        ""env"": {
                            ""PLUGIN_ROOT"": ""${PLUGIN_ROOT}"",
                            ""PLUGIN_DATA"": ""${PLUGIN_DATA}""
                        }
                    }
                }
            }");

            var pkg = new AgentPluginPackage
            {
                Manifest = new AgentPluginManifest
                {
                    Name = "test-plugin",
                    Version = "1.0.0",
                    Description = "Test",
                    License = "MIT"
                },
                RootPath = pluginDir,
                McpConfigPath = Path.Combine(pluginDir, "mcp.json")
            };

            var diagnostics = AgentPluginMcpAdapter.LoadMcpConfigs(pkg, out var servers);

            Assert.Single(servers);
            // The brief targeted Arguments[0] but that slot is "-y"; assert the arg that
            // carried ${PLUGIN_ROOT} was expanded to the plugin root.
            Assert.Contains(servers[0].Arguments, arg => arg.Contains(pluginDir, StringComparison.Ordinal));
            // The adapter expands ${PLUGIN_ROOT} to the plugin root and ${PLUGIN_DATA} to the
            // ~/.openclaw/plugin-data/<name> directory (the toolchain storage root convention,
            // NOT %APPDATA%\openclaw). Assert the actual expanded values, not just that the keys
            // are present (pins the "数据目录保留" contract).
            Assert.Contains(pluginDir, servers[0].Environment["PLUGIN_ROOT"], StringComparison.Ordinal);
            var expectedPluginData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".openclaw",
                "plugin-data",
                "test-plugin");
            Assert.Equal(expectedPluginData, servers[0].Environment["PLUGIN_DATA"]);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void GetAgentPluginDiscoveryRoots_PreservesOrder_ConfigThenWorkspaceThenUser()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        try
        {
            var config = new PluginsConfig();
            config.Load.Paths = [tempDir];
            var workspace = Path.Combine(tempDir, "ws");

            var roots = PluginDiscovery.GetAgentPluginDiscoveryRoots(config, workspace);

            // Precedence: config Plugins:Load:Paths, then workspace plugins/, then user ~/.openclaw/plugins/.
            // The runtime watcher (AgentPluginWatcherService) watches exactly these roots, so pin the order.
            Assert.Equal(tempDir, roots[0]);
            Assert.Equal(Path.Combine(workspace, "plugins"), roots[1]);
            Assert.Equal(
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".openclaw",
                    "plugins"),
                roots[2]);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void AgentPluginMcpAdapter_RejectsUnsafePath()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        try
        {
            var pluginDir = Path.Combine(tempDir, "test-plugin");
            Directory.CreateDirectory(pluginDir);
            File.WriteAllText(Path.Combine(pluginDir, "plugin.json"), @"{
                ""name"": ""test-plugin"",
                ""version"": ""1.0.0"",
                ""description"": ""Test"",
                ""license"": ""MIT""
            }");

            File.WriteAllText(Path.Combine(pluginDir, "mcp.json"), @"{
                ""mcpServers"": {
                    ""test-server"": {
                        ""command"": ""npx"",
                        ""args"": [""-y"", ""server.js""],
                        ""cwd"": ""../../../etc""
                    }
                }
            }");

            var pkg = new AgentPluginPackage
            {
                Manifest = new AgentPluginManifest
                {
                    Name = "test-plugin",
                    Version = "1.0.0",
                    Description = "Test",
                    License = "MIT"
                },
                RootPath = pluginDir,
                McpConfigPath = Path.Combine(pluginDir, "mcp.json")
            };

            var diagnostics = AgentPluginMcpAdapter.LoadMcpConfigs(pkg, out var servers);

            Assert.Empty(servers);
            Assert.Single(diagnostics);
            Assert.Equal("unsafe_cwd_path", diagnostics[0].Code);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void AgentPluginMcpAdapter_SkipsSseTransport()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        try
        {
            var pluginDir = Path.Combine(tempDir, "test-plugin");
            Directory.CreateDirectory(pluginDir);
            File.WriteAllText(Path.Combine(pluginDir, "plugin.json"), @"{
                ""name"": ""test-plugin"",
                ""version"": ""1.0.0"",
                ""description"": ""Test"",
                ""license"": ""MIT""
            }");

            // The adapter only treats an EXPLICIT transport/type equal to "sse" (case-insensitive)
            // as SSE; a bare url is classified as http. So the fixture must declare transport: "sse".
            File.WriteAllText(Path.Combine(pluginDir, "mcp.json"), @"{
                ""mcpServers"": {
                    ""sse-server"": {
                        ""transport"": ""sse"",
                        ""url"": ""http://localhost:3000/sse""
                    }
                }
            }");

            var pkg = new AgentPluginPackage
            {
                Manifest = new AgentPluginManifest
                {
                    Name = "test-plugin",
                    Version = "1.0.0",
                    Description = "Test",
                    License = "MIT"
                },
                RootPath = pluginDir,
                McpConfigPath = Path.Combine(pluginDir, "mcp.json")
            };

            var diagnostics = AgentPluginMcpAdapter.LoadMcpConfigs(pkg, out var servers);

            Assert.Empty(servers);
            var diagnostic = Assert.Single(diagnostics);
            Assert.Equal("unsupported_transport", diagnostic.Code);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void AgentPluginMcpAdapter_PreservesHeadersForHttpInitialRequest()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        try
        {
            var pluginDir = Path.Combine(tempDir, "test-plugin");
            Directory.CreateDirectory(pluginDir);

            File.WriteAllText(Path.Combine(pluginDir, "mcp.json"), """
            {
              "mcpServers": {
                "http-server": {
                  "url": "http://localhost:4000/mcp",
                  "headers": {
                    "Authorization": "Bearer token"
                  }
                }
              }
            }
            """);

            var pkg = new AgentPluginPackage
            {
                Manifest = new AgentPluginManifest
                {
                    Name = "test-plugin",
                    Version = "1.0.0",
                    Description = "Test",
                    License = "MIT"
                },
                RootPath = pluginDir,
                McpConfigPath = Path.Combine(pluginDir, "mcp.json")
            };

            var diagnostics = AgentPluginMcpAdapter.LoadMcpConfigs(pkg, out var servers);

            Assert.Empty(diagnostics);
            var server = Assert.Single(servers);
            Assert.Equal("http", server.Transport);
            // The adapter preserves configured headers for the initial Streamable HTTP request
            // (authentication depends on them). Whether headers are forwarded to a redirect
            // target is a transport-layer policy, not something cleared at parse time.
            Assert.Equal("Bearer token", server.Headers["Authorization"]);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void AgentPluginMcpAdapter_AcceptsPluginRootCwd()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        try
        {
            var pluginDir = Path.Combine(tempDir, "test-plugin");
            Directory.CreateDirectory(pluginDir);

            File.WriteAllText(Path.Combine(pluginDir, "mcp.json"), """
            {
              "mcpServers": {
                "test-server": {
                  "command": "npx",
                  "args": ["server.js"],
                  "cwd": "${PLUGIN_ROOT}"
                }
              }
            }
            """);

            var pkg = new AgentPluginPackage
            {
                Manifest = new AgentPluginManifest
                {
                    Name = "test-plugin",
                    Version = "1.0.0",
                    Description = "Test",
                    License = "MIT"
                },
                RootPath = pluginDir,
                McpConfigPath = Path.Combine(pluginDir, "mcp.json")
            };

            var diagnostics = AgentPluginMcpAdapter.LoadMcpConfigs(pkg, out var servers);

            Assert.Empty(diagnostics);
            var server = Assert.Single(servers);
            Assert.Equal(pluginDir, server.WorkingDirectory);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void AgentPluginMcpAdapter_RejectsRootedCwdOutsideRoot()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        try
        {
            var pluginDir = Path.Combine(tempDir, "test-plugin");
            Directory.CreateDirectory(pluginDir);
            WriteManifest(pluginDir, "test-plugin", "1.0.0");
            var outsideCwd = Path.Combine(Path.GetTempPath(), "outside-plugin-root");
            // 反斜杠必须在 JSON 中转义（Windows 绝对路径），否则 mcp.json 无法解析。
            var escapedCwd = outsideCwd.Replace("\\", "\\\\");
            File.WriteAllText(Path.Combine(pluginDir, "mcp.json"), $$"""
            {
              "mcpServers": {
                "test-server": {
                  "command": "npx",
                  "cwd": "{{escapedCwd}}"
                }
              }
            }
            """);
            var pkg = new AgentPluginPackage
            {
                Manifest = new AgentPluginManifest
                {
                    Name = "test-plugin",
                    Version = "1.0.0",
                    Description = "Test",
                    License = "MIT"
                },
                RootPath = pluginDir,
                McpConfigPath = Path.Combine(pluginDir, "mcp.json")
            };

            var diagnostics = AgentPluginMcpAdapter.LoadMcpConfigs(pkg, out var servers);

            Assert.Empty(servers);
            var diagnostic = Assert.Single(diagnostics);
            Assert.Equal("unsafe_cwd_path", diagnostic.Code);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void AgentPluginMcpAdapter_InvalidMcpConfig_DoesNotAffectSkills()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        try
        {
            var pluginDir = Path.Combine(tempDir, "test-plugin");
            Directory.CreateDirectory(pluginDir);

            // Top-level mcp.json must be an object; an array is invalid.
            File.WriteAllText(Path.Combine(pluginDir, "mcp.json"), "[]");

            var skillsDir = Path.Combine(pluginDir, "skills");
            var validSkillDir = Path.Combine(skillsDir, "valid-skill");
            Directory.CreateDirectory(validSkillDir);
            File.WriteAllText(Path.Combine(validSkillDir, "SKILL.md"), "# Valid Skill");

            var pkg = new AgentPluginPackage
            {
                Manifest = new AgentPluginManifest
                {
                    Name = "test-plugin",
                    Version = "1.0.0",
                    Description = "Test",
                    License = "MIT"
                },
                RootPath = pluginDir,
                SkillsPath = skillsDir,
                McpConfigPath = Path.Combine(pluginDir, "mcp.json")
            };

            // Failure boundary: an invalid top-level mcp.json only closes the plugin's MCP surface.
            var mcpDiagnostics = AgentPluginMcpAdapter.LoadMcpConfigs(pkg, out var servers);
            Assert.Empty(servers);
            var mcpDiagnostic = Assert.Single(mcpDiagnostics);
            Assert.Equal("invalid_mcp_config", mcpDiagnostic.Code);

            // Skills still validate — mcp.json failure must not affect skill loading.
            var skillDiagnostics = AgentPluginSkillLoader.ValidateSkills(pkg, out var skillNames);
            Assert.Empty(skillDiagnostics);
            Assert.Contains("valid-skill", skillNames);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void DiscoverAgentPlugins_IgnoresUnknownFieldsAndExtensions()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        try
        {
            var pluginDir = Path.Combine(tempDir, "extensions-plugin");
            Directory.CreateDirectory(pluginDir);
            File.WriteAllText(Path.Combine(pluginDir, "plugin.json"), """
            {
              "name": "extensions-plugin",
              "version": "1.0.0",
              "description": "Test plugin",
              "license": "MIT",
              "unknownTopLevelField": "ignored",
              "nestedUnknown": { "deeply": [1, 2, 3] },
              "extensions": {
                "com.example.marker": {
                  "hooks": { "enabled": true },
                  "capabilities": ["custom"]
                }
              }
            }
            """);

            // com.* reverse-DNS extension content is not part of the discovery surface.
            Directory.CreateDirectory(Path.Combine(pluginDir, "com.example"));
            File.WriteAllText(Path.Combine(pluginDir, "com.example", "extension.js"), "// not an agent plugin surface");

            var config = new PluginsConfig
            {
                Load = new PluginLoadConfig { Paths = [tempDir] }
            };

            var packages = PluginDiscovery.DiscoverAgentPlugins(config);

            var pkg = Assert.Single(packages);
            Assert.Equal("extensions-plugin", pkg.Manifest.Name);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void DiscoverAgentPlugins_HigherPrecedenceSourceWins()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "openclaw-agent-prec-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempRoot);
        try
        {
            // Precedence: config Load.Paths -> workspace plugins/ -> user-level ~/.openclaw/plugins/.
            // A unique name avoids any collision with the real user-level plugin directory.
            var pluginName = "precedence-plugin-" + Guid.NewGuid().ToString("N")[..6];
            var configRoot = Path.Combine(tempRoot, "config");
            var workspaceRoot = Path.Combine(tempRoot, "workspace");

            WriteManifest(Path.Combine(configRoot, pluginName), pluginName, "1.0.0");
            WriteManifest(Path.Combine(workspaceRoot, "plugins", pluginName), pluginName, "2.0.0");

            var config = new PluginsConfig
            {
                Load = new PluginLoadConfig { Paths = [configRoot] }
            };

            var result = PluginDiscovery.DiscoverAgentPluginsWithDiagnostics(config, workspacePath: workspaceRoot);

            // Filter for the plugin we created so the assertion is not coupled to any agent
            // plugins a developer may have installed in their real ~/.openclaw/plugins/ directory
            // (DiscoverAgentPluginsWithDiagnostics unconditionally scans that location too).
            var matching = result.Packages.Where(p => p.Manifest.Name == pluginName).ToList();
            var pkg = Assert.Single(matching);
            Assert.Equal("1.0.0", pkg.Manifest.Version); // config path wins over workspace plugins/
            Assert.Contains(result.Reports, r => r.Diagnostics.Any(d => d.Code == "duplicate_plugin_id"));
        }
        finally
        {
            Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public void BothPluginFormats_CoexistWithoutConflict()
    {
        // Two plugin formats share one discovery tree; each pipeline must see only its own format.
        // Names are randomized so they cannot collide with real ~/.openclaw/ plugin state.
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var agentName = "agent-" + Guid.NewGuid().ToString("N")[..8];
        var legacyName = "legacy-" + Guid.NewGuid().ToString("N")[..8];
        Directory.CreateDirectory(tempDir);
        try
        {
            // Agent Plugin 1.0 package (plugin.json + skills/ + mcp.json). Legacy discovery is
            // prevented from classifying it as a Claude content bundle by the root plugin.json
            // guard in PluginBundleDetector, so this is a real skills-bearing agent plugin.
            var agentDir = Path.Combine(tempDir, agentName);
            Directory.CreateDirectory(agentDir);
            File.WriteAllText(
                Path.Combine(agentDir, "plugin.json"),
                $$"""{"name":"{{agentName}}","version":"1.0.0","description":"Agent Plugin 1.0 package","license":"MIT"}""");
            Directory.CreateDirectory(Path.Combine(agentDir, "skills", "hello-skill"));
            File.WriteAllText(Path.Combine(agentDir, "skills", "hello-skill", "SKILL.md"), "# hello-skill");
            File.WriteAllText(Path.Combine(agentDir, "mcp.json"), """
            {
              "mcpServers": {
                "srv": {
                  "command": "node",
                  "args": ["${PLUGIN_ROOT}/s.js"]
                }
              }
            }
            """);

            // Legacy OpenClaw plugin (openclaw.plugin.json + index.js entry).
            var legacyDir = Path.Combine(tempDir, legacyName);
            Directory.CreateDirectory(legacyDir);
            File.WriteAllText(
                Path.Combine(legacyDir, "openclaw.plugin.json"),
                $$"""{"id":"{{legacyName}}"}""");
            File.WriteAllText(Path.Combine(legacyDir, "index.js"), "module.exports = {};");

            var config = new PluginsConfig
            {
                Load = new PluginLoadConfig { Paths = [tempDir] }
            };

            // Agent Plugin discovery sees only the agent-plugin package.
            var agentResult = PluginDiscovery.DiscoverAgentPluginsWithDiagnostics(config);
            var agentPkgs = agentResult.Packages.Where(p => p.Manifest.Name == agentName).ToList();
            var agentPkg = Assert.Single(agentPkgs);
            Assert.Equal(agentName, agentPkg.Manifest.Name);

            // Legacy discovery sees only the legacy plugin.
            var legacyResult = PluginDiscovery.DiscoverWithDiagnostics(config);
            var legacyPlugins = legacyResult.Plugins.Where(p => p.Manifest.Id == legacyName).ToList();
            var legacyPlugin = Assert.Single(legacyPlugins);
            Assert.Equal(legacyName, legacyPlugin.Manifest.Id);

            // Cross-format isolation (互不破坏): neither pipeline picks up the other's format.
            Assert.DoesNotContain(agentResult.Packages, p => p.Manifest.Name == legacyName);
            Assert.DoesNotContain(legacyResult.Plugins, p => p.Manifest.Id == agentName);

            // The agent-plugin's MCP config survived discovery intact, with no diagnostics.
            var mcpDiagnostics = AgentPluginMcpAdapter.LoadMcpConfigs(agentPkg, out var servers);
            var server = Assert.Single(servers);
            Assert.Equal("srv", server.Name);
            Assert.Empty(mcpDiagnostics);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void SampleAgentPluginTestData_IsDiscoverable()
    {
        // Committed fixture copied to the test output via the TestData csproj rule.
        var testDataDir = Path.Combine(AppContext.BaseDirectory, "TestData", "AgentPlugins");
        Assert.True(Directory.Exists(testDataDir), $"Committed test data not found at '{testDataDir}'.");

        var config = new PluginsConfig
        {
            Load = new PluginLoadConfig { Paths = [testDataDir] }
        };

        var result = PluginDiscovery.DiscoverAgentPluginsWithDiagnostics(config);

        var matching = result.Packages.Where(p => p.Manifest.Name == "sample-agent-plugin").ToList();
        var pkg = Assert.Single(matching);
        Assert.Equal("sample-agent-plugin", pkg.Manifest.Name);

        // The committed package bundles one skill under skills/hello-skill/SKILL.md.
        var skillDiagnostics = AgentPluginSkillLoader.ValidateSkills(pkg, out var skillNames);
        Assert.Contains("hello-skill", skillNames);
        Assert.Empty(skillDiagnostics);

        // The committed package declares one stdio MCP server; ${PLUGIN_ROOT} in its args
        // is expanded to the package root during adaptation. A valid package yields no diagnostics.
        var mcpDiagnostics = AgentPluginMcpAdapter.LoadMcpConfigs(pkg, out var servers);
        var server = Assert.Single(servers);
        Assert.Equal("sample-server", server.Name);
        Assert.Contains(pkg.RootPath, server.Arguments[0]);
        Assert.Empty(mcpDiagnostics);
    }

    private static void WriteManifest(string pluginDir, string name, string version)
    {
        Directory.CreateDirectory(pluginDir);
        File.WriteAllText(
            Path.Combine(pluginDir, "plugin.json"),
            $$"""{"name":"{{name}}","version":"{{version}}","description":"Test plugin","license":"MIT"}""");
    }
}
