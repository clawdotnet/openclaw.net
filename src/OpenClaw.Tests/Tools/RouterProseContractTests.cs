using OpenClaw.Agent.Tools;
using Xunit;

namespace OpenClaw.Tests.Tools;

/// <summary>
/// Pins the prose markers to the exact literals emitted by the upstream
/// Nacos MCP Router at router.py @ 0ee95f4f353d6f66184dafdb3e0ffd342c4edb09.
/// Changing a marker is a deliberate, single-point change: update the constant
/// and this test together, backed by a live Router capture.
/// </summary>
public sealed class RouterProseContractTests
{
    [Fact]
    public void SearchListMarker_MatchesPinnedUpstream()
        => Assert.Equal("### 1. 当前可用的mcp server列表为：", RouterProseContract.SearchListMarker);

    [Fact]
    public void SearchStepMarker_MatchesPinnedUpstream()
        => Assert.Equal("### 2. ", RouterProseContract.SearchStepMarker);

    [Fact]
    public void AddSuccessMarker_MatchesPinnedUpstream()
        => Assert.Equal("安装完成", RouterProseContract.AddSuccessMarker);

    [Fact]
    public void AddToolListMarker_MatchesPinnedUpstream()
        => Assert.Equal("tool 列表为: ", RouterProseContract.AddToolListMarker);
}
