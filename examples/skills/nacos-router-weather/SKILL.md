---
name: nacos-router-weather
description: "PoC meta-skill that demonstrates the Gateway ↔ Nacos MCP Router integration. It first binds the upstream `weather-mcp` server through the Router (add_mcp_server), then dispatches a `use_tool` call whose tool name and parameters are templated from the runtime. When the Router cannot add or invoke the upstream tool, the `on_failure` branch emits a fallback notice and the runtime still returns a structured `final_text`. Use this skill as the canonical reference for any new Router-backed meta-skill; do not rely on it for production weather data."
kind: meta
meta_priority: 50
always: false
user_invocable: true
final_text_mode: "step:answer"
triggers:
  - "nacos router weather"
  - "nacos weather"
  - "weather via nacos"
  - "router weather poC"
metadata:
  risk: low
  capabilities:
    - "tool_call"
provenance: {"origin": "openclaw.net", "license": "MIT", "issue": 229}
composition:
  steps:
    - id: bind
      kind: tool_call
      tool: nacos-mcp-router_add_mcp_server
      tool_args:
        mcp_server_name: weather-mcp

    - id: query
      kind: tool_call
      depends_on: [bind]
      tool: nacos-mcp-router_use_tool
      tool_args:
        mcp_server_name: weather-mcp
        tool_name: "{{ steps.bind.tool_name | default('get_current_weather') }}"
        params:
          city: "{{ input }}"
      on_failure: query_fallback

    - id: query_fallback
      kind: tool_call
      tool: emit_text
      tool_args:
        text: "weather-mcp 通过 Nacos Router 不可用：{{ input }}（PoC 占位）"

    - id: answer
      kind: llm_chat
      depends_on: [query]
      with:
        text: |
          Summarize the weather lookup result for the user.

          City requested: {{ input }}
          Router tool output (may be the query fallback notice if the
          upstream `use_tool` call failed — both shapes are valid here):
          {{ outputs.query | default("(no query output)") }}