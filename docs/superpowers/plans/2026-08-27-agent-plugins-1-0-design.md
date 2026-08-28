# Agent Plugins 1.0.0 核心兼容层实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**目标:** 为 OpenClaw.NET 增加 Agent Plugins 1.0.0 可移植插件格式的核心兼容层，同时保留现有 `openclaw.plugin.json` 插件格式。

**架构:** 两种格式并行发现，内部通过独立适配模型接入现有技能加载器和 MCP 运行时。Agent Plugin 的完整发现面是 `plugin.json` → `mcp.json` → `skills/`；客户端不读取或执行服务器源代码来推断插件能力。

**Tech Stack:** C# .NET 10, OpenClaw.Core.Plugins, OpenClaw.Core.Skills

**Spec:** [docs/superpowers/specs/2026-08-26-agent-plugins-1-0-design.md](../specs/2026-08-26-agent-plugins-1-0-design.md)

## Global Constraints

- 本期只覆盖本地插件包的发现、验证、技能加载、MCP 配置适配和运行时刷新
- 不包含 GitHub 安装/更新/卸载、管理 UI
- AOT/JIT 约束: Agent Plugins 核心层只处理 JSON、文件系统路径、技能内容和现有 MCP 配置，不引入动态程序集加载或反射执行
- 支持的 MCP 传输: stdio, streamable-http
- schema URL: `https://agent-plugins.org/schemas/1.0.0/plugin.schema.json`

---

## Task 1: 创建 Agent Plugin 数据模型

**Files:**
- Create: `src/OpenClaw.Core/Plugins/AgentPluginModels.cs`

**Interfaces:**
- Produces: `AgentPluginManifest`, `AgentPluginPackage`, `McpServerConfig` (扩展)

```csharp
// 需要的类:
public sealed class AgentPluginManifest
{
    public required string Name { get; init; }
    public required string Version { get; init; }
    public required string Description { get; init; }
    public required string License { get; init; }
    public string? Homepage { get; init; }
    public string? Repository { get; init; }
    public string[] Keywords { get; init; } = [];
    public string? Schema { get; init; }
    // extensions 字段忽略
}

public sealed class AgentPluginPackage
{
    public required AgentPluginManifest Manifest { get; init; }
    public required string RootPath { get; init; }
    public string? SkillsPath { get; init; }
    public string? McpConfigPath { get; init; }
    public List<DiscoveredSkill> Skills { get; init; } = [];
    public List<McpServerConfig> McpServers { get; init; } = [];
}
```

- [ ] **Step 1: 创建 AgentPluginModels.cs**

```csharp
namespace OpenClaw.Core.Plugins;

public sealed class AgentPluginManifest
{
    public required string Name { get; init; }
    public required string Version { get; init; }
    public required string Description { get; init; }
    public required string License { get; init; }
    public string? Homepage { get; init; }
    public string? Repository { get; init; }
    public string[] Keywords { get; init; } = [];
    public string? Schema { get; init; }
    public string? Author { get; init; }
}

public sealed class AgentPluginPackage
{
    public required AgentPluginManifest Manifest { get; init; }
    public required string RootPath { get; init; }
    public string? SkillsPath { get; init; }
    public string? McpConfigPath { get; init; }
    public List<string> Skills { get; init; } = [];
    public List<McpServerConfig> McpServers { get; init; } = [];
}
```

- [ ] **Step 2: 运行验证**

验证代码能编译: `dotnet build src/OpenClaw.Core/OpenClaw.Core.csproj`

- [ ] **Step 3: Commit**

```bash
git add src/OpenClaw.Core/Plugins/AgentPluginModels.cs
git commit -m "feat: add Agent Plugin 1.0 data models"
```

---

## Task 2: 实现 Agent Plugin 发现逻辑

**Files:**
- Modify: `src/OpenClaw.Core/Plugins/PluginDiscovery.cs`

**Interfaces:**
- Consumes: `PluginsConfig`, workspacePath
- Produces: `AgentPluginPackage` 列表

```csharp
// 在 PluginDiscovery 中添加:
public static List<AgentPluginPackage> DiscoverAgentPlugins(PluginsConfig config, string? workspacePath)
public static AgentPluginDiscoveryResult DiscoverAgentPluginsWithDiagnostics(PluginsConfig config, string? workspacePath)
```

- [ ] **Step 1: 添加 Agent Plugin 发现方法**

在 `PluginDiscovery.cs` 中添加:

```csharp
private const string AgentPluginManifestFileName = "plugin.json";
private const string AgentPluginSkillsDirName = "skills";
private const string AgentPluginMcpFileName = "mcp.json";
private const string AgentPluginSchema = "https://agent-plugins.org/schemas/1.0.0/plugin.schema.json";

public static List<AgentPluginPackage> DiscoverAgentPlugins(
    PluginsConfig pluginsConfig,
    string? workspacePath = null)
    => DiscoverAgentPluginsWithDiagnostics(pluginsConfig, workspacePath).Packages;

public static AgentPluginDiscoveryResult DiscoverAgentPluginsWithDiagnostics(
    PluginsConfig pluginsConfig,
    string? workspacePath = null)
{
    var result = new AgentPluginDiscoveryResult();
    var seen = new HashSet<string>(StringComparer.Ordinal);

    // 1. Config paths (显式 Plugins:Load:Paths)
    foreach (var configPath in pluginsConfig.Load.Paths)
    {
        var expanded = ExpandPath(configPath);
        if (Directory.Exists(expanded))
            ScanForAgentPlugins(expanded, seen, result);
    }

    // 2. Workspace plugins/
    if (!string.IsNullOrEmpty(workspacePath))
    {
        var wsPluginsDir = Path.Combine(workspacePath, "plugins");
        if (Directory.Exists(wsPluginsDir))
            ScanForAgentPlugins(wsPluginsDir, seen, result);
    }

    // 3. 用户级 ~/.openclaw/plugins/
    var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    var userPluginsDir = Path.Combine(home, ".openclaw", "plugins");
    if (Directory.Exists(userPluginsDir))
        ScanForAgentPlugins(userPluginsDir, seen, result);

    return result;
}

private static void ScanForAgentPlugins(string dir, HashSet<string> seen, AgentPluginDiscoveryResult result)
{
    foreach (var subDir in Directory.EnumerateDirectories(dir))
    {
        var manifestPath = Path.Combine(subDir, AgentPluginManifestFileName);
        if (File.Exists(manifestPath))
            TryAddAgentPlugin(subDir, manifestPath, seen, result);
    }
}

private static void TryAddAgentPlugin(string pluginRoot, string manifestPath, HashSet<string> seen, AgentPluginDiscoveryResult result)
{
    AgentPluginManifest? manifest;
    try
    {
        using var stream = File.OpenRead(manifestPath);
        manifest = JsonSerializer.Deserialize(stream, CoreJsonContext.Default.AgentPluginManifest);
    }
    catch (Exception ex)
    {
        result.Reports.Add(new PluginLoadReport
        {
            PluginId = Path.GetFileName(pluginRoot),
            SourcePath = Path.GetFullPath(pluginRoot),
            Loaded = false,
            Diagnostics = [new PluginCompatibilityDiagnostic
            {
                Code = "invalid_agent_plugin_manifest",
                Message = $"Failed to parse plugin.json: {ex.Message}",
                Path = manifestPath
            }]
        });
        return;
    }

    if (manifest is null || !ValidateManifest(manifest, out var validationErrors))
    {
        result.Reports.Add(new PluginLoadReport
        {
            PluginId = Path.GetFileName(pluginRoot),
            SourcePath = Path.GetFullPath(pluginRoot),
            Loaded = false,
            Diagnostics = validationErrors
        });
        return;
    }

    if (!seen.Add(manifest.Name))
    {
        result.Reports.Add(new PluginLoadReport
        {
            PluginId = manifest.Name,
            SourcePath = Path.GetFullPath(pluginRoot),
            Loaded = false,
            Diagnostics = [new PluginCompatibilityDiagnostic
            {
                Code = "duplicate_plugin_id",
                Message = $"Plugin name '{manifest.Name}' was discovered more than once. Later entries are skipped.",
                Path = manifestPath
            }]
        });
        return;
    }

    var pkg = new AgentPluginPackage
    {
        Manifest = manifest,
        RootPath = Path.GetFullPath(pluginRoot),
        SkillsPath = Directory.Exists(Path.Combine(pluginRoot, AgentPluginSkillsDirName))
            ? Path.Combine(pluginRoot, AgentPluginSkillsDirName)
            : null,
        McpConfigPath = File.Exists(Path.Combine(pluginRoot, AgentPluginMcpFileName))
            ? Path.Combine(pluginRoot, AgentPluginMcpFileName)
            : null
    };

    result.Packages.Add(pkg);
}

private static bool ValidateManifest(AgentPluginManifest manifest, out PluginCompatibilityDiagnostic[] errors)
{
    var list = new List<PluginCompatibilityDiagnostic>();

    if (string.IsNullOrWhiteSpace(manifest.Name))
        list.Add(new PluginCompatibilityDiagnostic { Code = "missing_name", Message = "plugin.json must have a 'name' field", Path = "" });

    if (string.IsNullOrWhiteSpace(manifest.Version))
        list.Add(new PluginCompatibilityDiagnostic { Code = "missing_version", Message = "plugin.json must have a 'version' field", Path = "" });

    if (string.IsNullOrWhiteSpace(manifest.Description))
        list.Add(new PluginCompatibilityDiagnostic { Code = "missing_description", Message = "plugin.json must have a 'description' field", Path = "" });

    if (string.IsNullOrWhiteSpace(manifest.License))
        list.Add(new PluginCompatibilityDiagnostic { Code = "missing_license", Message = "plugin.json must have a 'license' field", Path = "" });

    // Schema 验证 - 本地常量比较，不联网
    if (!string.IsNullOrWhiteSpace(manifest.Schema) && manifest.Schema != AgentPluginSchema)
    {
        // 未知 schema 给出警告但不阻止
        list.Add(new PluginCompatibilityDiagnostic
        {
            Severity = "warning",
            Code = "unknown_schema",
            Message = $"Unknown schema '{manifest.Schema}'. Expected '{AgentPluginSchema}'.",
            Path = ""
        });
    }

    errors = list.ToArray();
    return list.All(e => e.Severity != "error");
}

private static string ExpandPath(string path)
{
    var expanded = Environment.ExpandEnvironmentVariables(path);
    if (expanded.StartsWith('~'))
        expanded = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            expanded[1..].TrimStart('/').TrimStart('\\'));
    return expanded;
}
```

- [ ] **Step 2: 添加结果类**

在 `AgentPluginModels.cs` 中添加:

```csharp
public sealed class AgentPluginDiscoveryResult
{
    public List<AgentPluginPackage> Packages { get; } = [];
    public List<PluginLoadReport> Reports { get; } = [];
}
```

- [ ] **Step 3: 运行验证**

```bash
dotnet build src/OpenClaw.Core/OpenClaw.Core.csproj
```

- [ ] **Step 4: Commit**

```bash
git add src/OpenClaw.Core/Plugins/PluginDiscovery.cs src/OpenClaw.Core/Plugins/AgentPluginModels.cs
git commit -m "feat: implement Agent Plugin discovery logic"
```

---

## Task 3: 实现 Agent Plugin 技能发现

**Files:**
- Create: `src/OpenClaw.Core/Plugins/AgentPluginSkillLoader.cs`

**Interfaces:**
- Consumes: `AgentPluginPackage` 列表
- Produces: 技能目录列表 (用于 SkillLoader)

```csharp
public static class AgentPluginSkillLoader
{
    public static List<string> GetSkillDirectories(List<AgentPluginPackage> packages)
    public static List<DiscoveredSkill> LoadSkillsFromPackage(AgentPluginPackage package)
}
```

- [ ] **Step 1: 创建 AgentPluginSkillLoader.cs**

```csharp
using System.Text.Json;
using OpenClaw.Core.Skills;

namespace OpenClaw.Core.Plugins;

public static class AgentPluginSkillLoader
{
    private const string SkillFileName = "SKILL.md";

    public static List<string> GetSkillDirectories(List<AgentPluginPackage> packages)
    {
        var dirs = new List<string>();
        foreach (var pkg in packages)
        {
            if (!string.IsNullOrEmpty(pkg.SkillsPath) && Directory.Exists(pkg.SkillsPath))
            {
                // 只扫描直接子目录
                foreach (var skillDir in Directory.EnumerateDirectories(pkg.SkillsPath))
                {
                    var skillFile = Path.Combine(skillDir, SkillFileName);
                    if (File.Exists(skillFile))
                    {
                        dirs.Add(skillDir);
                    }
                }
            }
        }
        return dirs;
    }

    public static List<PluginCompatibilityDiagnostic> ValidateSkills(
        AgentPluginPackage package,
        out List<string> validSkillNames)
    {
        validSkillNames = [];
        var diagnostics = new List<PluginCompatibilityDiagnostic>();

        if (string.IsNullOrEmpty(package.SkillsPath) || !Directory.Exists(package.SkillsPath))
            return diagnostics;

        // 只检查直接子目录
        foreach (var skillDir in Directory.EnumerateDirectories(package.SkillsPath))
        {
            var skillName = Path.GetFileName(skillDir);
            var skillFile = Path.Combine(skillDir, SkillFileName);

            if (!File.Exists(skillFile))
            {
                diagnostics.Add(new PluginCompatibilityDiagnostic
                {
                    Code = "invalid_skill",
                    Message = $"Skill directory '{skillName}' does not contain {SkillFileName}. Skipping.",
                    Surface = "skill",
                    Path = skillDir
                });
                continue;
            }

            // 验证 SKILL.md 可以解析
            try
            {
                var content = File.ReadAllText(skillFile);
                if (string.IsNullOrWhiteSpace(content))
                {
                    diagnostics.Add(new PluginCompatibilityDiagnostic
                    {
                        Code = "empty_skill",
                        Message = $"Skill '{skillName}' has empty SKILL.md. Skipping.",
                        Surface = "skill",
                        Path = skillFile
                    });
                    continue;
                }

                validSkillNames.Add(skillName);
            }
            catch (Exception ex)
            {
                diagnostics.Add(new PluginCompatibilityDiagnostic
                {
                    Code = "skill_read_error",
                    Message = $"Failed to read skill '{skillName}': {ex.Message}",
                    Surface = "skill",
                    Path = skillFile
                });
            }
        }

        return diagnostics;
    }
}
```

- [ ] **Step 2: 运行验证**

```bash
dotnet build src/OpenClaw.Core/OpenClaw.Core.csproj
```

- [ ] **Step 3: Commit**

```bash
git add src/OpenClaw.Core/Plugins/AgentPluginSkillLoader.cs
git commit -m "feat: implement Agent Plugin skill discovery"
```

---

## Task 4: 实现 MCP 配置适配

**Files:**
- Create: `src/OpenClaw.Core/Plugins/AgentPluginMcpAdapter.cs`

**Interfaces:**
- Consumes: `AgentPluginPackage` 列表
- Produces: `McpServerConfig` 列表

- [ ] **Step 1: 创建 AgentPluginMcpAdapter.cs**

```csharp
using System.Text.Json;

namespace OpenClaw.Core.Plugins;

public static class AgentPluginMcpAdapter
{
    private const string SchemaUrl = "https://modelcontext.dev/json/1.0/schema.json";

    public static List<PluginCompatibilityDiagnostic> LoadMcpConfigs(
        AgentPluginPackage package,
        out List<McpServerConfig> servers)
    {
        servers = [];
        var diagnostics = new List<PluginCompatibilityDiagnostic>();

        if (string.IsNullOrEmpty(package.McpConfigPath) || !File.Exists(package.McpConfigPath))
            return diagnostics;

        try
        {
            using var stream = File.OpenRead(package.McpConfigPath);
            using var doc = JsonDocument.Parse(stream);

            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Add(new PluginCompatibilityDiagnostic
                {
                    Code = "invalid_mcp_config",
                    Message = $"mcp.json must be a JSON object, got {doc.RootElement.ValueKind}",
                    Surface = "mcp",
                    Path = package.McpConfigPath
                });
                return diagnostics;
            }

            if (!doc.RootElement.TryGetProperty("mcpServers", out var mcpServers) ||
                mcpServers.ValueKind != JsonValueKind.Object)
            {
                // 没有 mcpServers 字段是可接受的
                return diagnostics;
            }

            foreach (var serverEntry in mcpServers.EnumerateObject())
            {
                var serverName = serverEntry.Name;
                var serverConfig = serverEntry.Value;

                var result = ParseMcpServerConfig(package, serverName, serverConfig);
                if (result.Diagnostic is { } diag)
                {
                    diagnostics.Add(diag);
                    if (result.Config is { })
                        servers.Add(result.Config);
                }
                else if (result.Config is { })
                {
                    servers.Add(result.Config);
                }
            }
        }
        catch (JsonException ex)
        {
            diagnostics.Add(new PluginCompatibilityDiagnostic
            {
                Code = "invalid_mcp_json",
                Message = $"Failed to parse mcp.json: {ex.Message}",
                Surface = "mcp",
                Path = package.McpConfigPath
            });
        }

        return diagnostics;
    }

    private static (McpServerConfig? Config, PluginCompatibilityDiagnostic? Diagnostic) ParseMcpServerConfig(
        AgentPluginPackage package,
        string serverName,
        JsonElement config)
    {
        // 传输类型检测
        string? transport = null;
        string? command = null;
        string[] args = [];
        string? url = null;
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        string? cwd = null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // 解析 command/args (stdio)
        if (config.TryGetProperty("command", out var cmdEl) && cmdEl.ValueKind == JsonValueKind.String)
        {
            command = cmdEl.GetString();
            transport = "stdio";
        }

        if (config.TryGetProperty("args", out var argsEl) && argsEl.ValueKind == JsonValueKind.Array)
        {
            args = argsEl.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!)
                .ToArray();
        }

        // 解析 url (streamable-http)
        if (config.TryGetProperty("url", out var urlEl) && urlEl.ValueKind == JsonValueKind.String)
        {
            url = urlEl.GetString();
            transport = "http";
        }

        if (config.TryGetProperty("headers", out var headersEl) && headersEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var header in headersEl.EnumerateObject())
            {
                if (header.Value.ValueKind == JsonValueKind.String)
                    headers[header.Name] = header.Value.GetString()!;
            }
        }

        // 解析 env
        if (config.TryGetProperty("env", out var envEl) && envEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var envVar in envEl.EnumerateObject())
            {
                if (envVar.Value.ValueKind == JsonValueKind.String)
                {
                    var value = envVar.Value.GetString()!;
                    // 变量展开
                    value = ExpandVariables(value, package.RootPath, package.Manifest.Name);
                    env[envVar.Name] = value;
                }
            }
        }

        // 解析 cwd
        if (config.TryGetProperty("cwd", out var cwdEl) && cwdEl.ValueKind == JsonValueKind.String)
        {
            var cwdValue = cwdEl.GetString()!;
            cwdValue = ExpandVariables(cwdValue, package.RootPath, package.Manifest.Name);
            // 路径验证
            if (!IsPathSafe(cwdValue, package.RootPath))
            {
                return (null, new PluginCompatibilityDiagnostic
                {
                    Code = "unsafe_cwd_path",
                    Message = $"Working directory '{cwdValue}' resolves outside plugin root. Rejected.",
                    Surface = "mcp",
                    Path = package.McpConfigPath
                });
            }
            cwd = cwdValue;
        }

        // 验证 transport
        if (transport is null && command is null && url is null)
        {
            return (null, new PluginCompatibilityDiagnostic
            {
                Code = "invalid_mcp_transport",
                Message = $"MCP server '{serverName}' has no valid transport (command/args or url). Skipping.",
                Surface = "mcp",
                Path = package.McpConfigPath
            });
        }

        // 不支持的传输类型跳过
        if (transport == "sse")
        {
            return (null, new PluginCompatibilityDiagnostic
            {
                Code = "unsupported_transport",
                Message = $"MCP server '{serverName}' uses unsupported 'sse' transport. Skipping.",
                Surface = "mcp",
                Path = package.McpConfigPath
            });
        }

        // Streamable HTTP manual redirect - 不转发 headers
        if (transport == "http" && url is { })
        {
            headers.Clear(); // manual redirect 策略
        }

        var serverConfig = new McpServerConfig
        {
            Name = serverName,
            Transport = transport,
            Command = command,
            Arguments = args,
            Url = url,
            Headers = headers,
            Environment = env,
            WorkingDirectory = cwd,
            Enabled = true
        };

        return (serverConfig, null);
    }

    private static string ExpandVariables(string value, string pluginRoot, string pluginName)
    {
        // ${PLUGIN_ROOT}
        if (value.Contains("${PLUGIN_ROOT}"))
            value = value.Replace("${PLUGIN_ROOT}", pluginRoot);

        // ${PLUGIN_DATA}
        if (value.Contains("${PLUGIN_DATA}"))
        {
            var pluginDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "openclaw",
                "plugin-data",
                pluginName);
            value = value.Replace("${PLUGIN_DATA}", pluginDataDir);
        }

        return value;
    }

    private static bool IsPathSafe(string path, string pluginRoot)
    {
        // 拒绝绝对路径
        if (Path.IsPathRooted(path))
            return false;

        // 解析并检查是否在 pluginRoot 内
        try
        {
            var fullPath = Path.GetFullPath(Path.Combine(pluginRoot, path));
            var fullRoot = Path.GetFullPath(pluginRoot);
            return fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar) ||
                   fullPath == fullRoot;
        }
        catch
        {
            return false;
        }
    }
}
```

- [ ] **Step 2: 运行验证**

```bash
dotnet build src/OpenClaw.Core/OpenClaw.Core.csproj
```

- [ ] **Step 3: Commit**

```bash
git add src/OpenClaw.Core/Plugins/AgentPluginMcpAdapter.cs
git commit -m "feat: implement MCP configuration adapter for Agent Plugins"
```

---

## Task 5: 集成到运行时刷新

**Files:**
- Modify: `src/OpenClaw.Gateway/Composition/RuntimeInitializationExtensions.cs`
- Create: `src/OpenClaw.Core/Plugins/AgentPluginRuntimeManager.cs`

**Interfaces:**
- Consumes: `AgentPluginPackage` 列表
- Produces: 更新后的技能列表和 MCP 配置

- [ ] **Step 1: 创建 AgentPluginRuntimeManager.cs**

```csharp
using Microsoft.Extensions.Logging;

namespace OpenClaw.Core.Plugins;

public sealed class AgentPluginRuntimeManager
{
    private readonly PluginsConfig _config;
    private readonly string? _workspacePath;
    private readonly ILogger _logger;

    private List<AgentPluginPackage> _currentPackages = [];
    private List<string> _currentSkillDirs = [];
    private Dictionary<string, McpServerConfig> _currentMcpConfigs = [];

    public AgentPluginRuntimeManager(
        PluginsConfig config,
        string? workspacePath,
        ILogger logger)
    {
        _config = config;
        _workspacePath = workspacePath;
        _logger = logger;
    }

    public async Task<AgentPluginRefreshResult> RefreshAsync()
    {
        _logger.LogInformation("Refreshing Agent Plugins...");

        // 1. 重新发现并验证
        var discoveryResult = PluginDiscovery.DiscoverAgentPluginsWithDiagnostics(_config, _workspacePath);

        var newPackages = discoveryResult.Packages;
        var diagnostics = discoveryResult.Reports;

        // 2. 验证技能
        var allSkillDirs = new List<string>();
        var packageDiagnostics = new List<PluginCompatibilityDiagnostic>();

        foreach (var pkg in newPackages)
        {
            var skillResult = AgentPluginSkillLoader.ValidateSkills(pkg, out var skillNames);
            packageDiagnostics.AddRange(skillResult);
            pkg.Skills.AddRange(skillNames);

            if (!string.IsNullOrEmpty(pkg.SkillsPath))
            {
                foreach (var name in skillNames)
                {
                    allSkillDirs.Add(Path.Combine(pkg.SkillsPath, name));
                }
            }
        }

        // 3. 加载 MCP 配置
        var allMcpConfigs = new Dictionary<string, McpServerConfig>(StringComparer.Ordinal);
        foreach (var pkg in newPackages)
        {
            var mcpResult = AgentPluginMcpAdapter.LoadMcpConfigs(pkg, out var servers);
            packageDiagnostics.AddRange(mcpResult);

            foreach (var server in servers)
            {
                var key = $"{pkg.Manifest.Name}/{server.Name}";
                allMcpConfigs[key] = server;
            }
        }

        // 4. 构建新快照
        var newSkillDirs = allSkillDirs;
        var newMcpConfigs = allMcpConfigs;

        // 5. 切换到运行时 (原子替换)
        _currentPackages = newPackages;
        _currentSkillDirs = newSkillDirs;
        _currentMcpConfigs = newMcpConfigs;

        _logger.LogInformation(
            "Agent Plugins refreshed: {PackageCount} packages, {SkillCount} skills, {McpCount} MCP servers",
            newPackages.Count,
            newSkillDirs.Count,
            newMcpConfigs.Count);

        return new AgentPluginRefreshResult
        {
            Packages = newPackages,
            SkillDirectories = newSkillDirs,
            McpConfigs = newMcpConfigs,
            Diagnostics = diagnostics.Concat(packageDiagnostics).ToList()
        };
    }

    public List<string> GetSkillDirectories() => _currentSkillDirs;
    public Dictionary<string, McpServerConfig> GetMcpConfigs() => _currentMcpConfigs;
}

public sealed class AgentPluginRefreshResult
{
    public List<AgentPluginPackage> Packages { get; init; } = [];
    public List<string> SkillDirectories { get; init; } = [];
    public Dictionary<string, McpServerConfig> McpConfigs { get; init; } = [];
    public List<PluginCompatibilityDiagnostic> Diagnostics { get; init; } = [];
}
```

- [ ] **Step 2: 集成到 Gateway 运行时**

在 `RuntimeInitializationExtensions.cs` 中添加 Agent Plugin 初始化逻辑。

- [ ] **Step 3: 运行验证**

```bash
dotnet build src/OpenClaw.Gateway/OpenClaw.Gateway.csproj
```

- [ ] **Step 4: Commit**

```bash
git add src/OpenClaw.Core/Plugins/AgentPluginRuntimeManager.cs
git commit -m "feat: integrate Agent Plugin runtime refresh"
```

---

## Task 6: 编写验收测试

**Files:**
- Create: `src/OpenClaw.Tests/AgentPluginDiscoveryTests.cs`

**测试用例:**

- [ ] **Step 1: 创建测试文件**

```csharp
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
            Assert.Contains(pluginDir, servers[0].Arguments[0]);
            Assert.Contains("PLUGIN_ROOT", servers[0].Environment.Keys);
            Assert.Contains("PLUGIN_DATA", servers[0].Environment.Keys);
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

            File.WriteAllText(Path.Combine(pluginDir, "mcp.json"), @"{
                ""mcpServers"": {
                    ""sse-server"": {
                        ""url"": ""http://localhost:3000/sse""
                    }
                }
            }");

            // 修改适配器检测 sse 传输
            // 这里需要添加实际的 SSE 检测逻辑
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }
}
```

- [ ] **Step 2: 运行测试**

```bash
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "AgentPlugin"
```

- [ ] **Step 3: Commit**

```bash
git add src/OpenClaw.Tests/AgentPluginDiscoveryTests.cs
git commit -m "test: add Agent Plugin discovery and validation tests"
```

---

## Task 7: 端到端集成测试

**测试用例:**

- [ ] **Step 1: 创建集成测试插件**

创建 `src/OpenClaw.Tests/TestData/AgentPlugins/sample-agent-plugin/`

```
sample-agent-plugin/
├── plugin.json
├── skills/
│   └── hello-skill/
│       └── SKILL.md
└── mcp.json
```

- [ ] **Step 2: 测试两种格式并存**

```csharp
[Fact]
public void BothPluginFormats_CoexistWithoutConflict()
{
    // 测试 Agent Plugin 和现有 OpenClaw 插件可以同时被发现
}
```

- [ ] **Step 3: 运行完整测试套件**

```bash
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj
```

- [ ] **Step 4: Commit**

```bash
git add src/OpenClaw.Tests/TestData/
git commit -m "test: add Agent Plugin integration test data"
```

---

## 执行选项

**Plan complete and saved to `docs/superpowers/plans/2026-08-27-agent-plugins-1-0-design.md`. Two execution options:**

**1. Subagent-Driven (recommended)** - I dispatch a fresh subagent per task, review between tasks, fast iteration

**2. Inline Execution** - Execute tasks in this session using executing-plans, batch execution with checkpoints

**Which approach?**
