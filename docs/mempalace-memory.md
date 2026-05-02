# MemPalace.NET memory provider

OpenClaw can use [ElBruno.MempalaceNet](https://github.com/elbruno/ElBruno.MempalaceNet) as an optional memory backend for persistent notes and temporal knowledge graph facts.

## Enable it

Set the memory provider to `mempalace`:

```json
{
  "OpenClaw": {
    "Memory": {
      "Provider": "mempalace",
      "Mempalace": {
        "BasePath": "./memory/mempalace",
        "PalaceId": "openclaw",
        "CollectionName": "memories",
        "EmbeddingDimensions": 384,
        "KnowledgeGraphDbPath": "./memory/mempalace/kg.db",
        "SessionDbPath": "./memory/mempalace/openclaw-sessions.db"
      }
    }
  }
}
```

Existing `file` and `sqlite` providers remain the defaults and are unchanged.

## What is stored in MemPalace

- Persistent memory notes written through `memory`, `memory_get`, `memory_search`, and `project_memory`.
- Note records are stored in a MemPalace SQLite collection under a palace/collection name from configuration.
- Notes are projected into a wings / rooms / drawers hierarchy:
  - `project:demo:decision` becomes wing `project`, room `demo`, drawer `decision`.
  - Keys without enough segments use `DefaultWing` and `DefaultRoom`.
- Each saved note records temporal KG relationships:
  - `memory:<key> stored-in drawer:<drawer>`
  - `drawer:<drawer> located-in room:<room>`
  - `room:<room> located-in wing:<wing>`

Session history, branches, admin listing/search, and retention continue through OpenClaw's existing SQLite session store to preserve gateway compatibility.

## Tools

When the provider is enabled, OpenClaw also registers `mempalace_kg`:

- `add` writes a temporal triple using `subject`, `predicate`, and `object`.
- `query` reads triples by optional `subject`, `predicate`, `object`, and `at`.
- `timeline` lists relationships for `entity`, optionally bounded by `from` and `to`.

Entities use MemPalace's `type:id` format, for example `agent:openclaw` or `memory:project:demo:decision`.

## AOT and dependency implications

The integration is optional and only loaded when `OpenClaw:Memory:Provider` is `mempalace`. It adds `MemPalace.Core`, `MemPalace.Backends.Sqlite`, and `MemPalace.KnowledgeGraph` to the Gateway project.

The adapter uses a deterministic local hashing embedder, so enabling it does not add cloud calls or API-key requirements. Treat the MemPalace provider as the standard gateway lane until NativeAOT publish validation is run for the exact deployment profile.
