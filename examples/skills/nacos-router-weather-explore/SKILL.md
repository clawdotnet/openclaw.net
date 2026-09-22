---
name: nacos-router-weather-explore
description: "Opt-in model-driven Nacos Router weather baseline."
kind: standard
triggers: ["explore nacos weather"]
---
For the input city, call `nacos_mcp_router_search_mcp_server` with
`task_description` and `key_words` (a comma-separated string). Select an appropriate
server from the actual returned text. Call `nacos_mcp_router_add_mcp_server` with
`mcp_server_name`, inspect its actual tool list, then call
`nacos_mcp_router_use_tool` with `mcp_server_name`, `mcp_tool_name`, and a JSON-encoded
string `params` (e.g. `{"city": "Oslo"}`). Upstream Router declares `params` as a
string and decodes it via `json.loads`; passing a nested object will raise TypeError
and silently produce `failed to use tool: <tool_name>` as plain text.
Treat Router initialization, missing-server, unhealthy-server, and failed-install
messages as failures even when the MCP response is not marked as an error.
Report a failure rather than inventing weather. Summarize successful weather data.
