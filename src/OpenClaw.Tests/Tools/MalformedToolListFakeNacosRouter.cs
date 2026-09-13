using ModelContextProtocol.Server;

namespace OpenClaw.Tests.Tools;

/// <summary>
/// Router fixture whose add succeeds in prose terms (it emits 安装完成) but whose
/// tool list is malformed: the first element is an object with no "name" property.
/// Pre-fix this drove <c>ResolveCapabilityTool.TryExtractTool</c> to throw
/// <see cref="KeyNotFoundException"/> instead of returning the documented failure envelope.
/// </summary>
[McpServerToolType]
public sealed class MalformedToolListFakeNacosRouter(NacosRouterFixtureState state)
{
    [McpServerTool(Name = "search_mcp_server")]
    public string Search(string task_description, string key_words)
    {
        state.Calls.Add("search");
        return "## 获取" + task_description + "的步骤如下：\n"
            + "### 1. 当前可用的mcp server列表为："
            + "{\"weather-mcp\":{\"name\":\"weather-mcp\",\"description\":\"weather\"}}\n"
            + "### 2. 从当前可用的mcp server列表中选择你需要的mcp server调add_mcp_server工具安装mcp server";
    }

    [McpServerTool(Name = "add_mcp_server")]
    public string Add(string mcp_server_name)
    {
        state.Calls.Add("add:" + mcp_server_name);
        return "安装完成\ntool 列表为: [{\"description\":\"x\"}]";
    }
}
