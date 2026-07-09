# Workspace Admin APIs

OpenClaw.NET exposes a set of admin endpoints under `/admin/workspace/*` for workspace file management and MCP server configuration. These APIs are designed for operator workflows and are separate from the MCP App host/proxy surface.

## Endpoints

### Workspace Files

| Method | Endpoint | Purpose |
|--------|----------|---------|
| `GET` | `/admin/workspace/browse?path=<relative-path>` | Flat directory listing with file metadata |
| `GET` | `/admin/workspace/tree?path=<relative-path>&depth=<max-depth>` | Recursive directory tree (default depth: 6) |
| `POST` | `/admin/workspace/upload?dir=<relative-path>` | Upload files (multipart) or extract ZIP archives |
| `GET` | `/admin/workspace/download?path=<relative-path>` | Download a single file |

#### Browse Response

```json
{
  "success": true,
  "files": [
    { "name": "readme.md", "path": "docs/readme.md", "isDirectory": false, "size": 2048 }
  ]
}
```

#### Tree Response

```json
{
  "success": true,
  "root": "docs",
  "entries": [
    {
      "name": "docs",
      "path": "docs",
      "isDir": true,
      "children": [
        { "name": "readme.md", "path": "docs/readme.md", "isDir": false, "size": 2048 }
      ]
    }
  ]
}
```

#### Upload

- **Single ZIP file**: extracted into the target directory with ZIP-slip protection
- **Multiple regular files**: saved directly to the target directory
- Maximum file size: configurable via `MaxUploadBytes`

### Workspace MCP

| Method | Endpoint | Purpose |
|--------|----------|---------|
| `GET` | `/admin/workspace/mcp` | Get current workspace MCP server config |
| `PUT` | `/admin/workspace/mcp` | Persist and hot-reload MCP server config |

This surface is for ordinary `Plugins:Mcp` server definitions. When the config is updated via `PUT`, the gateway hot-reloads the live MCP tool surface without a restart. This is **separate** from MCP Apps — see [MCPAPP.md](MCPAPP.md) for the manifest-discovered MCP App flow.

### Media

| Method | Endpoint | Purpose |
|--------|----------|---------|
| `POST` | `/admin/media/upload` | Upload media files for channel use |
| `GET` | `/admin/media/{id}` | Serve uploaded media |

### Digital Employee

| Method | Endpoint | Purpose |
|--------|----------|---------|
| `GET` | `/admin/digital-employee` | List digital employee configs |
| `POST` | `/admin/digital-employee` | Create or update a digital employee |

## Authentication

All `/admin/*` endpoints require operator authentication. See [GLOSSARY.md](GLOSSARY.md) for auth mode details (bootstrap token, operator account token, browser session, OIDC JWT).

## Security

- ZIP-slip protection: all extracted paths are validated to stay within the target directory
- File size limits prevent resource exhaustion
- Path traversal is rejected with a 400 response
- Audit entries are written for upload operations

## Related Docs

- [MCP Apps](MCPAPP.md) — manifest-discovered MCP Apps (separate from workspace MCP)
- [Glossary](GLOSSARY.md) — workspace management definitions
- [Security](../SECURITY.md) — overall security posture
- [Enterprise Channels](ENTERPRISE_CHANNELS.md) — enterprise IM channel adapters
