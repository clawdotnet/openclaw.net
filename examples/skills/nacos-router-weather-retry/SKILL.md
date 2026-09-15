---
name: nacos-router-weather-retry
description: "Opt-in Nacos Router weather PoC: dynamic capability slot with step retry."
kind: meta
final_text_mode: "step:query"
composition:
  steps:
    - id: query
      kind: tool_call
      # Dynamic capability slot (issue #231) with node-level retry (issue #233):
      # use_tool failures retry up to three attempts with 100 ms backoff, then
      # the fallback branch fires. The resolver itself is deterministic and
      # adds no LLM turn.
      capability_ref:
        provider: nacos
        binding: dynamic
        intent:
          type: cap:WeatherQuery
          task_description: weather city
          keywords: [weather, city]
        selection_policy: first
        fallback: fallback_notice
      retry:
        max_attempts: 3
        backoff_ms: 100
      tool_args:
        city: "{{ input }}"
    - id: fallback_notice
      kind: tool_call
      tool: emit_text
      tool_args:
        text: "no weather capability bound; check Nacos registration and Router logs."
---
Opt-in contract fixture for #233: the capability step permits up to three attempts
(100 ms backoff) only when the host marks weather-mcp/get_weather retry-safe.
The test harness opts in for its read-only fixture. Default Nacos composition
does not assume retry safety and takes the fallback after the first failure.
No model call is needed.
