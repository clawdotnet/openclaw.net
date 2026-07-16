import type { Ontology } from './types'

async function asJson<T>(res: Response): Promise<T> {
  if (!res.ok) {
    let detail = `${res.status} ${res.statusText}`
    try {
      const body = await res.json()
      if (body?.error) detail = body.error
    } catch {
      /* ignore non-JSON error bodies */
    }
    throw new Error(detail)
  }
  return res.json() as Promise<T>
}

/** Load the ontology bundled with the project (ontology/Resource.owl). */
export function loadDefaultOntology(): Promise<Ontology> {
  return fetch('/api/ontology/default').then((r) => asJson<Ontology>(r))
}

/** Parse an OWL/RDF-XML file chosen by the user. */
export function parseOntologyFile(file: File): Promise<Ontology> {
  const url = `/api/ontology/parse?name=${encodeURIComponent(file.name)}`
  return fetch(url, {
    method: 'POST',
    headers: { 'Content-Type': 'application/rdf+xml' },
    body: file,
  }).then((r) => asJson<Ontology>(r))
}

/** Fetch the raw source of the bundled ontology (for the "view source" panel). */
export function loadDefaultSource(): Promise<string> {
  return fetch('/api/ontology/source').then((r) => r.text())
}

/** File entry returned by GET /api/ontology/files. */
export interface OntologyFileEntry {
  name: string
  displayName: string
}

/** List all .owl files in the server's ontology directory. */
export function listOntologyFiles(): Promise<{ files: OntologyFileEntry[] }> {
  return fetch('/api/ontology/files').then((r) => asJson<{ files: OntologyFileEntry[] }>(r))
}

/** Load a specific ontology file from the server's ontology directory. */
export function loadOntology(fileName: string): Promise<Ontology> {
  return fetch(`/api/ontology/load?file=${encodeURIComponent(fileName)}`).then((r) => asJson<Ontology>(r))
}

/** Export a server-side ontology file as JSON-LD. */
export function exportJsonLd(fileName: string, format: 'compact' | 'expanded'): Promise<string> {
  const url = `/api/ontology/export-jsonld?file=${encodeURIComponent(fileName)}&format=${format}`
  return fetch(url).then((r) => asJsonOrText(r))
}

/** Export an uploaded ontology (raw OWL text) as JSON-LD. */
export function exportUploadedJsonLd(owlText: string, fileName: string, format: 'compact' | 'expanded'): Promise<string> {
  const url = `/api/ontology/export-jsonld?fileName=${encodeURIComponent(fileName)}&format=${format}`
  return fetch(url, {
    method: 'POST',
    headers: { 'Content-Type': 'text/plain' },
    body: owlText,
  }).then((r) => asJsonOrText(r))
}

/** Handle responses that may be text or JSON error. */
async function asJsonOrText(res: Response): Promise<string> {
  if (!res.ok) {
    let detail = `${res.status} ${res.statusText}`
    try {
      const body = await res.json()
      if (body?.error) detail = body.error
    } catch {
      const body = await res.text()
      if (body) detail = body
    }
    throw new Error(detail)
  }
  return res.text()
}
