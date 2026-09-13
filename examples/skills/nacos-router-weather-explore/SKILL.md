---
name: nacos-router-weather-explore
description: "Exploration variant of nacos-router-weather. Drives an agent through the full Nacos Router lifecycle (search → add → use) so the runtime can measure token cost of each Router round trip. Use this only when benchmarking Router integration; the production skill is nacos-router-weather."
kind: meta
meta_priority: 40
always: false
user_invocable: true
final_text_mode: "step:answer"
triggers:
  - "nacos router weather explore"
  - "router weather benchmark"
  - "nacos router token benchmark"
metadata:
  risk: low
  capabilities:
    - "tool_call"
provenance: {"origin": "openclaw.net", "license": "MIT", "issue": 229}
composition:
  steps:
    - id: search
      kind: tool_call
      tool: nacos-mcp-router_search_mcp_server
      tool_args:
        query: "weather forecast"
        limit: 5

    - id: bind
      kind: tool_call
      depends_on: [search]
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
          Token benchmark trace:
          - search candidates: {{ outputs.search | default("(empty)") }}
          - add status: {{ outputs.bind | default("(empty)") }}
          - use_tool result: {{ outputs.query | default("(empty)") }}
          - query may be the fallback notice when upstream use_tool failed.

          Reply with a one-line summary that records the first three
          facts verbatim.