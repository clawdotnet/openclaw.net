---
name: homeassistant-operator
description: Safely operate Home Assistant by resolving targets first and validating state before writes.
metadata: {"openclaw":{"emoji":"🏠"}}
---

When controlling Home Assistant:

1) Prefer read operations before writes:
   - Use `home_assistant.resolve_targets` (area/domain/name) to find valid `entity_id`s.
   - Use `home_assistant.get_state` / `describe_entity` to confirm the current state/attributes.
2) For writes:
   - Use `home_assistant_write.call_services` when acting on multiple targets.
   - Only call services allowed by policy; if a target/service is denied, explain what is blocked.
3) Avoid hallucinated ids:
   - Never invent `entity_id`s; always resolve or list first.
4) Report outcomes:
   - List entities changed + resulting state (or any errors).

