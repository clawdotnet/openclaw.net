# Workspace Admin APIs

OpenClaw.NET exposes a set of admin endpoints under `/admin/workspace/*` for workspace file management and MCP server configuration. These APIs require the admin role and support workspace administration.

## Endpoints

### Workspace Files

| Method | Endpoint | Purpose |
|--------|----------|---------|
| `GET` | `/admin/workspace/browse?path=<relative-path>` | Flat directory listing with file metadata |
| `GET` | `/admin/workspace/tree?path=<relative-path>&depth=<max-depth>` | Recursive directory tree (default depth: 6) |
| `POST` | `/admin/workspace/upload?dir=<relative-path>` | Upload files (multipart) or extract ZIP archives |
| `GET` | `/admin/workspace/download?path=<relative-path>` | Download a file or a directory ZIP |

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
- The workspace upload limit is 100 MB, including the expanded archive contents.
- Paths beneath the configured workspace root must be free of filesystem links. Listings and directory downloads skip links.

### Workspace MCP

| Method | Endpoint | Purpose |
|--------|----------|---------|
| `GET` | `/admin/workspace/mcp` | Get current workspace MCP server config |
| `PUT` | `/admin/workspace/mcp` | Persist and hot-reload MCP server config |

Workspace MCP configurations hot-reload when `Plugins:Mcp:Enabled=true`. With MCP disabled, updates are persisted for use after enabling MCP and restarting the gateway. See [MCPAPP.md](MCPAPP.md) for the manifest-discovered MCP App flow.

### Media

| Method | Endpoint | Purpose |
|--------|----------|---------|
| `POST` | `/media/upload` | Upload media files for channel use |
| `GET` | `/media/{id}` | Serve uploaded media |

### Digital Employee

| Method | Endpoint | Purpose |
|--------|----------|---------|
| `POST` | `/admin/digital-employee/upload` | Upload a digital employee ZIP package |

## Authentication

Workspace file access, workspace MCP configuration, and digital-employee installation require the admin role. Digital-employee archives have a 30 MB expanded-content limit.

Media uploads require the operator or admin role. Cookie-authenticated mutations also require `X-CSRF-Token`. Media downloads retain their existing authentication requirement and include sandbox and content-type protection headers. Active image formats such as SVG are downloaded as attachments.

The gateway publishes a local file attachment only from a completed `publish_file` tool call whose terminal marker points inside an allowed write root. Sources are authorized by the tool's read policy, and permitted read-only sources can be staged in the workspace downloads directory.

See [GLOSSARY.md](GLOSSARY.md) for authentication modes.

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
