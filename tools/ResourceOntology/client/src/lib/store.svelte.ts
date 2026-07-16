import type { Ontology, Selection, GraphFilters, OwlClass, OwlProperty, OwlIndividual } from './types'
import type { OntologyFileEntry } from './api'

export const store = $state({
  ontology: null as Ontology | null,
  loading: true,
  error: null as string | null,
  selection: null as Selection | null,
  search: '',
  /** Requested when the user clicks an entity in a list/tree — graph centres on it. */
  focusRequest: null as { id: string; nonce: number } | null,
  filters: {
    showClasses: true,
    showIndividuals: false,
    subClassOf: true,
    disjoint: false,
    restriction: true,
    domainRange: true,
    typeOf: true,
    assertion: true,
  } as GraphFilters,
  /** Available ontology files from the server directory. */
  fileList: [] as OntologyFileEntry[],
  /** Currently selected ontology file name (e.g. "Resource.owl"), or null if uploaded. */
  currentFile: null as string | null,
  /** JSON-LD export state */
  jsonldSource: null as string | null,
  jsonldCompact: null as string | null,
  jsonldExpanded: null as string | null,
  jsonldFormat: 'compact' as 'compact' | 'expanded',
  activeTab: 'graph' as 'graph' | 'source',
  rawOwlText: null as string | null,
  rawOwlFileName: null as string | null,
})

let focusNonce = 0

export function select(kind: Selection['kind'], id: string, focus = true) {
  store.selection = { kind, id }
  if (focus) store.focusRequest = { id, nonce: ++focusNonce }
}

export function clearSelection() {
  store.selection = null
}

// ---- Lookups --------------------------------------------------------------
export function classById(id: string): OwlClass | undefined {
  return store.ontology?.classes.find((c) => c.id === id)
}
export function propertyById(id: string): OwlProperty | undefined {
  return store.ontology?.properties.find((p) => p.id === id)
}
export function individualById(id: string): OwlIndividual | undefined {
  return store.ontology?.individuals.find((i) => i.id === id)
}

/** Display name for any IRI, falling back to its local name. */
export function nameOf(id: string): string {
  const o = store.ontology
  if (!o) return localName(id)
  const c = o.classes.find((x) => x.id === id)
  if (c) return c.label || prettify(c.name)
  const p = o.properties.find((x) => x.id === id)
  if (p) return p.label || p.name
  const ind = o.individuals.find((x) => x.id === id)
  if (ind) return ind.label || prettify(ind.name)
  return prettify(localName(id))
}

export function localName(iri: string): string {
  const hash = iri.lastIndexOf('#')
  if (hash >= 0) return iri.slice(hash + 1)
  const slash = iri.lastIndexOf('/')
  if (slash >= 0) return iri.slice(slash + 1)
  return iri
}

/** Turn snake_case identifiers into readable labels for display. */
export function prettify(name: string): string {
  return name.replace(/^_+/, '').replace(/_/g, ' ').trim() || name
}
