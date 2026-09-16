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
    public void Parse_PreservesCandidatesForExactNameSelection()
    {
        var six = string.Join(",", Enumerable.Range(0, 6).Select(i => $"\"k{i}\":{{\"name\":\"k{i}\",\"description\":\"d\"}}"));
        var prose = "## 获取t的步骤如下：\n" + RouterProseContract.SearchListMarker + "{" + six + "}\n" + RouterProseContract.SearchStepMarker + "...";
        var parsed = RouterCandidateParser.Parse(prose);
        Assert.Equal(6, parsed.Count);
    }

    [Fact]
    public void Parse_MalformedEnvelope_ReturnsEmpty()
    {
        var parsed = RouterCandidateParser.Parse("garbage with no JSON");
        Assert.Empty(parsed);
    }

    // Live capture from a real nacos-mcp-router 0.2.2 + Nacos 3.2.4 (2026-09-14,
    // local test bed). Note the spaced JSON (Python json.dumps default
    // separators), the slash-containing server name and the bilingual
    // descriptions — the parser must eat the envelope exactly as upstream
    // emits it, not the compact form the fixtures above assume.
    private static readonly string LiveCapturedSearchEnvelope =
        "## 获取weather city的步骤如下：\n"
        + RouterProseContract.SearchListMarker
        + """{"weather-mcp": {"name": "weather-mcp", "description": "天气服务：提供城市天气查询、天气预报等工具，Weather service for city weather query and forecast."}, "cn.pianam.mcp/weather-mcp-china": {"name": "cn.pianam.mcp/weather-mcp-china", "description": "MCP server for current weather and multi-day forecasts worldwide, Chinese city names and output."}}"""
        + "\n" + RouterProseContract.SearchStepMarker
        + "从当前可用的mcp server列表中选择你需要的mcp server调add_mcp_server工具安装mcp server";

    [Fact]
    public void Parse_LiveCapturedEnvelope_ReturnsTwoRankedCandidates()
    {
        var parsed = RouterCandidateParser.Parse(LiveCapturedSearchEnvelope);
        Assert.Equal(2, parsed.Count);
        Assert.Equal("weather-mcp", parsed[0].Name);
        Assert.Equal(1, parsed[0].Rank);
        Assert.Contains("天气服务", parsed[0].Description);
        Assert.Equal("cn.pianam.mcp/weather-mcp-china", parsed[1].Name);
        Assert.Equal(2, parsed[1].Rank);
        Assert.Contains("multi-day forecasts", parsed[1].Description);
    }

    [Theory]
    [InlineData("{broken}")]
    [InlineData("[]")]
    [InlineData("null")]
    public void Parse_InvalidEnclosedJson_ReturnsEmpty(string json)
        => Assert.Empty(RouterCandidateParser.Parse(RouterProseContract.SearchListMarker + json + "\n" + RouterProseContract.SearchStepMarker));

    [Fact]
    public void Parse_NonObjectEntries_KeepOriginalRankWithoutHidingLaterCandidates()
    {
        var parsed = RouterCandidateParser.Parse(RouterProseContract.SearchListMarker +
            """{"bad":null,"bad2":42,"bad3":[],"bad4":"x","bad5":false,"weather":{}}""" + "\n" + RouterProseContract.SearchStepMarker);
        Assert.Equal("weather", Assert.Single(parsed).Name);
        Assert.Equal(6, parsed[0].Rank);
    }
}
