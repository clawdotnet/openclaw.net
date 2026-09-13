using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace OpenClaw.Tests;

public sealed class NacosRouterFixtureState
{
    public List<string> Calls { get; } = [];
    public bool FailUse { get; set; }
    public bool PlainTextFailure { get; set; }
}

// Parameters and prose envelopes follow the pinned upstream Python Router.
// Scores are deliberately absent: upstream search does not supply them.
[McpServerToolType]
public sealed class FakeNacosRouterMcpTools(NacosRouterFixtureState state)
{
    [McpServerTool(Name = "search_mcp_server", ReadOnly = true), Description("Search by task and comma-separated keywords.")]
    public string Search(string task_description, string key_words)
    {
        state.Calls.Add("search");
        var candidates = Enumerable.Range(0, 5).ToDictionary(
            i => i == 0 ? "weather-mcp" : $"candidate-{i}",
            i => new { name = i == 0 ? "weather-mcp" : $"candidate-{i}", description = "weather city" });
        return "### 1. 当前可用的mcp server列表为：" + JsonSerializer.Serialize(candidates)
            + "\n### 2. 从当前可用的mcp server列表中选择你需要的mcp server调add_mcp_server工具安装mcp server";
    }

    [McpServerTool(Name = "add_mcp_server"), Description("Bind a registered server.")]
    public string Add(string mcp_server_name)
    {
        state.Calls.Add("add:" + mcp_server_name);
        return "1. " + mcp_server_name + "安装完成, tool 列表为: "
            + "[{\"name\":\"get_weather\",\"description\":\"weather city\",\"inputSchema\":{\"type\":\"object\",\"properties\":{\"city\":{\"type\":\"string\"}},\"required\":[\"city\"]}}]";
    }

    [McpServerTool(Name = "use_tool"), Description("Proxy an installed tool.")]
    public CallToolResult Use(string mcp_server_name, string mcp_tool_name, Dictionary<string, JsonElement> @params)
    {
        state.Calls.Add("use:" + mcp_server_name + ":" + mcp_tool_name);
        var invalid = mcp_server_name != "weather-mcp" || mcp_tool_name != "get_weather";
        var failed = state.FailUse || invalid;
        var text = failed ? "failed to use tool: " + mcp_tool_name : "Weather for " + @params["city"].GetString() + ": sunny";
        return new CallToolResult { IsError = failed && !state.PlainTextFailure, Content = [new TextContentBlock { Text = text }] };
    }
}
