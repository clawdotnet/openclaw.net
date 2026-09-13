---
name: nacos-router-weather
description: "Opt-in Nacos Router weather PoC; requires a registered weather-mcp server."
kind: meta
always: false
final_text_mode: "step:query"
triggers: ["nacos weather"]
composition:
  steps:
    - id: bind
      kind: tool_call
      tool: nacos_mcp_router_add_mcp_server
      tool_args:
        mcp_server_name: weather-mcp
    - id: query
      kind: tool_call
      tool: nacos_mcp_router_use_tool
      depends_on: [bind]
      tool_args:
        mcp_server_name: weather-mcp
        mcp_tool_name: get_weather
        params:
          city: "{{ input }}"
      on_failure: fallback_notice
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
