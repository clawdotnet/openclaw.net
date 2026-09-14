---
name: nacos-router-weather-dynamic
description: "Opt-in Nacos Router weather PoC with a dynamic capability slot."
kind: meta
final_text_mode: "step:query"
composition:
  steps:
    - id: query
      kind: tool_call
      # Dynamic capability slot (issue #231): the runtime resolves the intent
      # through the Router's search/add chain (zero LLM round-trips), then
      # proxies the call through use_tool. tool_args are the inner tool's
      # arguments.
      capability_ref:
        binding: dynamic
        intent:
          type: cap:WeatherQuery
          task_description: weather city
          keywords: [weather, city]
        selection_policy: first
        fallback: fallback_notice
      tool_args:
        city: "{{ input }}"
    - id: fallback_notice
      kind: tool_call
      tool: emit_text
      tool_args:
        text: "no weather capability bound; check Nacos registration and Router logs."
---
This is a dynamic, opt-in contract fixture. The intent is resolved deterministically
by the capability resolver (search → add → bind), then the bound tool is invoked
through use_tool; no model call is needed.
