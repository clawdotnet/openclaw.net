// Minimal stdio MCP peer for transport and compatibility regression tests.
import { createInterface } from 'node:readline';
const mode = process.argv[2];
const base = ['search', 'open', 'recent', 'export', 'validate', 'handoff_create', 'index_refresh'];
const workflows = ['read', 'list', 'attention', 'decisions', 'context', 'resume', 'handoff_list',
  'handoff_read', 'node_create', 'update', 'append', 'review', 'doctor', 'import'];
const names = (mode === 'legacy' ? base : [...base, ...workflows]).map(n => `memory_${n}`);
const reply = (id, result) => process.stdout.write(JSON.stringify({ jsonrpc: '2.0', id, result }) + '\n');
for await (const line of createInterface({ input: process.stdin })) {
  const request = JSON.parse(line);
  if (request.id === undefined) continue;
  if (request.method === 'initialize') {
    reply(request.id, { protocolVersion: request.params.protocolVersion, capabilities: { tools: {} }, serverInfo: { name: 'fractal-fixture', version: '1' } });
  } else if (request.method === 'ping') {
    reply(request.id, {});
  } else if (request.method === 'tools/list') {
    reply(request.id, { tools: names.map(name => ({ name, inputSchema: { type: 'object' } })) });
  } else if (request.method === 'tools/call') {
    if (mode === 'protocol-error') {
      process.stdout.write(JSON.stringify({ jsonrpc: '2.0', id: request.id, error: { code: -32603, message: 'repository failed' } }) + '\n');
      continue;
    }
    if (mode === 'no-repository') {
      reply(request.id, { isError: true, content: [{ type: 'text', text: 'No FractalMemory repository found.' }] });
      continue;
    }
    const { name, arguments: args } = request.params;
    const document = { nodePath: args.path, file: args.file ?? 'state.md', sourcePath: `${args.path}/state.md`,
      resourceUri: `memory://document/${encodeURIComponent(args.path)}/state.md`, hash: 'hash-from-disk', content: 'Recorded objective',
      section: null, startLine: 3, endLine: 5 };
    let data;
    if (name === 'memory_validate') data = { hasErrors: false, issues: [] };
    else if (name === 'memory_context') data = { nodePath: args.path, text: 'Bounded objective', truncated: true, sources: [document] };
    else if (name === 'memory_read') data = document;
    else if (name === 'memory_export') data = { relativePath: args.path, title: 'Legacy export', mode: 0, currentState: 'Legacy objective' };
    else if (name === 'memory_open') data = { relativePath: args.path, depth: 2, view: 1, currentState: 'Abridged state', stateTruncated: true };
    else data = { arguments: args, workingDirectory: process.cwd(), repositoryRoot: process.env.FRACTALMEM_REPOSITORY_ROOT };
    reply(request.id, { structuredContent: data, content: [
      { type: 'text', text: JSON.stringify(data) },
      ...(name === 'memory_read' ? [{ type: 'resource_link', uri: document.resourceUri, name: document.sourcePath, mimeType: 'text/markdown' }] : [])
    ] });
  }
}
