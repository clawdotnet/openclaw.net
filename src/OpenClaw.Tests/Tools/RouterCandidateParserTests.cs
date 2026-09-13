using System.Linq;
using OpenClaw.Agent.Tools;
using OpenClaw.Core.Skills.Meta;
using Xunit;

namespace OpenClaw.Tests.Tools;

public sealed class RouterCandidateParserTests
{
    private const string UpstreamEnvelope = """
        ## 获取weather city的步骤如下：
        ### 1. 当前可用的mcp server列表为：{"weather-mcp":{"name":"weather-mcp","description":"weather forecast"},"candidate-1":{"name":"candidate-1","description":"other"}}
        ### 2. 从当前可用的mcp server列表中选择你需要的mcp server调add_mcp_server工具安装mcp server
        """;

    [Fact]
    public void Parse_UpstreamEnvelope_ReturnsRankedCandidates()
    {
        var parsed = RouterCandidateParser.Parse(UpstreamEnvelope);
        Assert.Equal(2, parsed.Count);
        Assert.Equal("weather-mcp", parsed[0].Name);
        Assert.Equal("weather forecast", parsed[0].Description);
        Assert.Equal(1.0, parsed[0].Score); // rank 1 → 1/1
        Assert.Equal(0.5, parsed[1].Score); // rank 2 → 1/2
    }

    [Fact]
    public void Parse_CapsAtFiveEntries()
    {
        var six = string.Join(",", Enumerable.Range(0, 6).Select(i => $"\"k{i}\":{{\"name\":\"k{i}\",\"description\":\"d\"}}"));
        var prose = "## 获取t的步骤如下：\n### 1. 当前可用的mcp server列表为：{" + six + "}\n### 2. ...";
        var parsed = RouterCandidateParser.Parse(prose);
        Assert.Equal(5, parsed.Count);
    }

    [Fact]
    public void Parse_MalformedEnvelope_ReturnsEmpty()
    {
        var parsed = RouterCandidateParser.Parse("garbage with no JSON");
        Assert.Empty(parsed);
    }
}
