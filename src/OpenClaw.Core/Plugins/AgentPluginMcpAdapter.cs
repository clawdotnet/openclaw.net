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
