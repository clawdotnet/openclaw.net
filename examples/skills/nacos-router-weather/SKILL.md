---
name: nacos-router-weather
description: "Opt-in Nacos Router weather PoC; requires a registered weather-mcp server."
kind: meta
final_text_mode: "step:query"
triggers: ["nacos weather"]
composition:
  steps:
    - id: query
      kind: tool_call
      # Static capability slot (issue #231): the runtime auto-adds the pinned
      # server once per process, then proxies every call through use_tool.
      # tool_args are the inner tool's arguments; the executor serialises them
      # for the Router's `params` wire field.
      capability_ref:
        binding: static
        static:
          mcp_server_name: weather-mcp
          tool_name: get_weather
        fallback: fallback_notice
      tool_args:
        city: "{{ input }}"
    - id: fallback_notice
      kind: tool_call
      tool: emit_text
      tool_args:
        text: "weather-mcp unavailable; check Nacos registration and Router logs."
---
This is a static, opt-in contract fixture. Register `weather-mcp` with a
`get_weather(city)` tool before running it. The final output is the tool result;
no model call is needed for this deterministic path. Router prose errors are
not MCP protocol failures: see docs/nacos-mcp-router.md before live use.
