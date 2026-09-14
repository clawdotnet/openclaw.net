using System.Linq;
using OpenClaw.Agent.Tools;
using OpenClaw.Core.Skills.Meta;
using Xunit;

namespace OpenClaw.Tests.Tools;

public sealed class RouterCandidateParserTests
{
    private static readonly string UpstreamEnvelope =
        "## 获取weather city的步骤如下：\n"
        + RouterProseContract.SearchListMarker
        + """{"weather-mcp":{"name":"weather-mcp","description":"weather forecast"},"candidate-1":{"name":"candidate-1","description":"other"}}"""
        + "\n" + RouterProseContract.SearchStepMarker
        + "从当前可用的mcp server列表中选择你需要的mcp server调add_mcp_server工具安装mcp server";

    [Fact]
    public void Parse_UpstreamEnvelope_ReturnsRankedCandidates()
    {
        var parsed = RouterCandidateParser.Parse(UpstreamEnvelope);
        Assert.Equal(2, parsed.Count);
        Assert.Equal("weather-mcp", parsed[0].Name);
        Assert.Equal("weather forecast", parsed[0].Description);
        // Upstream supplies no scores; Rank is the position in the upstream's
        // deterministic ordering and must not be dressed up as a score.
        Assert.Equal(1, parsed[0].Rank);
        Assert.Equal(2, parsed[1].Rank);
    }

    [Fact]
    public void Parse_CapsAtFiveEntries()
    {
        var six = string.Join(",", Enumerable.Range(0, 6).Select(i => $"\"k{i}\":{{\"name\":\"k{i}\",\"description\":\"d\"}}"));
        var prose = "## 获取t的步骤如下：\n" + RouterProseContract.SearchListMarker + "{" + six + "}\n" + RouterProseContract.SearchStepMarker + "...";
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
