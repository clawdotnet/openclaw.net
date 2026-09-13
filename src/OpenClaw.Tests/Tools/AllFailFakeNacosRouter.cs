using ModelContextProtocol.Server;

namespace OpenClaw.Tests.Tools;

[McpServerToolType]
public sealed class AllFailFakeNacosRouter
{
    [McpServerTool(Name = "search_mcp_server")]
    public string Search(string task_description, string key_words) =>
        "## 获取weather city的步骤如下：\n" +
        "### 1. 当前可用的mcp server列表为：{\"weather-mcp\":{\"name\":\"weather-mcp\",\"description\":\"weather\"}}\n" +
        "### 2. 从当前可用的mcp server列表中选择你需要的mcp server调add_mcp_server工具安装mcp server";

    [McpServerTool(Name = "add_mcp_server")]
    public string Add(string mcp_server_name) =>
        "weather-mcp is not found, use search_mcp_server to get mcp servers";
}
