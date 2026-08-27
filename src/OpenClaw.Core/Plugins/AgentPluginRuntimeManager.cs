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
            Diagnostics = diagnostics.SelectMany(r => r.Diagnostics).Concat(packageDiagnostics).ToList()
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
