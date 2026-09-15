namespace OpenClaw.Adapters.Nacos;

/// <summary>
/// Literal prose markers emitted by the upstream Nacos MCP Router, pinned to
/// router.py @ 0ee95f4f353d6f66184dafdb3e0ffd342c4edb09. Do not change these
/// without a live Router capture confirming the new shape;
/// <c>RouterProseContractTests</c> pins the literals so drift is a deliberate,
/// single-point change.
/// </summary>
internal static class RouterProseContract
{
    /// <summary>"### 1. 当前可用的mcp server列表为：" — precedes the search candidate JSON.</summary>
    public const string SearchListMarker = "### 1. 当前可用的mcp server列表为：";

    /// <summary>"### 2. " — section marker that closes the search candidate block.</summary>
    public const string SearchStepMarker = "### 2. ";

    /// <summary>"安装完成" — success marker in add_mcp_server prose.</summary>
    public const string AddSuccessMarker = "安装完成";

    /// <summary>"tool 列表为: " — precedes the installed tool-list JSON in add prose.</summary>
    public const string AddToolListMarker = "tool 列表为: ";

    /// <summary>"failed to install mcp server: " — plain-text add failure prefix (live capture 2026-09-14).</summary>
    public const string AddFailureMarker = "failed to install mcp server: ";

    /// <summary>"failed to use tool: " — plain-text use failure prefix (live capture 2026-09-14).</summary>
    public const string UseFailureMarker = "failed to use tool: ";
}
