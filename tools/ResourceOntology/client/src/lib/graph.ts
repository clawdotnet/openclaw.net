import cytoscape from 'cytoscape'
import dagre from 'cytoscape-dagre'
import type { ElementDefinition, StylesheetStyle, LayoutOptions } from 'cytoscape'
import type { Ontology, GraphFilters } from './types'
import { prettify } from './store.svelte'

cytoscape.use(dagre)

/** Colours kept in sync with the CSS custom properties in app.css. */
const C = {
  klass: '#6ea8fe',
  individual: '#4ade80',
  subclass: '#5b6784',
  disjoint: '#f87171',
  restriction: '#f472b6',
  objprop: '#c084fc',
  typeof: '#4ade80',
  assertion: '#38bdf8',
  ink: '#e6ebf5',
}

/** Build Cytoscape elements from the ontology, honouring the active filters. */
export function buildElements(onto: Ontology, f: GraphFilters): ElementDefinition[] {
  const nodes: ElementDefinition[] = []
  const edges: ElementDefinition[] = []
  const present = new Set<string>()

  if (f.showClasses) {
    for (const c of onto.classes) {
      present.add(c.id)
      nodes.push({
        data: {
          id: c.id,
          label: c.label || prettify(c.name),
          kind: 'class',
          weight: 1 + c.instanceCount,
        },
      })
    }
  }
  if (f.showIndividuals) {
    for (const i of onto.individuals) {
      present.add(i.id)
      nodes.push({
        data: { id: i.id, label: i.label || prettify(i.name), kind: 'individual', weight: 1 },
      })
    }
  }

  const addEdge = (id: string, s: string, t: string, type: string, label = '') => {
    if (!present.has(s) || !present.has(t)) return
    edges.push({ data: { id, source: s, target: t, type, label } })
  }

  if (f.subClassOf) {
    for (const c of onto.classes)
      for (const parent of c.subClassOf) addEdge(`sub:${c.id}->${parent}`, c.id, parent, 'subclass')
  }

  if (f.disjoint) {
    const seen = new Set<string>()
    for (const c of onto.classes)
      for (const d of c.disjointWith) {
        const key = [c.id, d].sort().join('|')
        if (seen.has(key)) continue
        seen.add(key)
        addEdge(`dis:${key}`, c.id, d, 'disjoint')
      }
  }

  if (f.restriction) {
    for (const c of onto.classes)
      for (let r = 0; r < c.restrictions.length; r++) {
        const res = c.restrictions[r]
        if (res.filler) addEdge(`res:${c.id}:${r}`, c.id, res.filler, 'restriction', res.onPropertyName ?? '')
      }
  }

  if (f.domainRange) {
    for (const p of onto.properties) {
      if (p.kind !== 'object') continue
      for (const d of p.domains)
        for (const rg of p.ranges) addEdge(`dr:${p.id}:${d}:${rg}`, d, rg, 'objprop', p.label || p.name)
    }
  }

  if (f.showIndividuals && f.typeOf) {
    for (const i of onto.individuals)
      for (const t of i.types) addEdge(`type:${i.id}->${t}`, i.id, t, 'typeof')
  }

  if (f.showIndividuals && f.assertion) {
    for (const i of onto.individuals)
      for (const a of i.assertions)
        if (a.isObject) addEdge(`as:${i.id}:${a.property}:${a.value}`, i.id, a.value, 'assertion', a.propertyName)
  }

  return [...nodes, ...edges]
}

export const cyStyle: StylesheetStyle[] = [
  {
    selector: 'node',
    style: {
      label: 'data(label)',
      color: C.ink,
      'font-size': 11,
      'text-valign': 'center',
      'text-halign': 'center',
      'text-wrap': 'wrap',
      'text-max-width': '120px',
      'text-outline-width': 2,
      width: 'mapData(weight, 1, 12, 26, 64)',
      height: 'mapData(weight, 1, 12, 26, 64)',
      'border-width': 2,
    },
  },
  {
    selector: 'node[kind="class"]',
    style: { 'background-color': C.klass, 'text-outline-color': '#10203f', 'border-color': '#9cc2ff' },
  },
  {
    selector: 'node[kind="individual"]',
    style: {
      'background-color': C.individual,
      'text-outline-color': '#0f2c1c',
      'border-color': '#86efac',
      shape: 'round-rectangle',
      width: 30,
      height: 22,
    },
  },
  {
    selector: 'edge',
    style: {
      width: 1.4,
      'line-color': C.subclass,
      'target-arrow-color': C.subclass,
      'curve-style': 'bezier',
      'arrow-scale': 0.9,
      'font-size': 9,
      color: '#aab6d4',
      'text-rotation': 'autorotate',
      'text-background-color': '#0b0f1a',
      'text-background-opacity': 0.85,
      'text-background-padding': '2px',
    },
  },
  {
    selector: 'edge[type="subclass"]',
    style: { 'line-color': C.subclass, 'target-arrow-color': C.subclass, 'target-arrow-shape': 'triangle' },
  },
  {
    selector: 'edge[type="disjoint"]',
    style: { 'line-color': C.disjoint, 'line-style': 'dashed', 'target-arrow-shape': 'none', width: 1 },
  },
  {
    selector: 'edge[type="restriction"]',
    style: {
      'line-color': C.restriction,
      'target-arrow-color': C.restriction,
      'line-style': 'dashed',
      'target-arrow-shape': 'vee',
      label: 'data(label)',
    },
  },
  {
    selector: 'edge[type="objprop"]',
    style: {
      'line-color': C.objprop,
      'target-arrow-color': C.objprop,
      'target-arrow-shape': 'triangle',
      label: 'data(label)',
    },
  },
  {
    selector: 'edge[type="typeof"]',
    style: { 'line-color': C.typeof, 'target-arrow-color': C.typeof, 'line-style': 'dotted', 'target-arrow-shape': 'triangle' },
  },
  {
    selector: 'edge[type="assertion"]',
    style: {
      'line-color': C.assertion,
      'target-arrow-color': C.assertion,
      'target-arrow-shape': 'triangle',
      label: 'data(label)',
    },
  },
  // Selection / hover emphasis.
  { selector: 'node.selected', style: { 'border-width': 4, 'border-color': '#ffffff' } },
  { selector: '.faded', style: { opacity: 0.12 } },
  { selector: '.highlight', style: { opacity: 1 } },
  { selector: 'edge.highlight', style: { width: 2.6 } },
]

export function makeLayout(name: 'dagre' | 'cose' | 'concentric' | 'breadthfirst'): LayoutOptions {
  switch (name) {
    case 'dagre':
      return {
        name: 'dagre',
        rankDir: 'BT',
        nodeSep: 22,
        rankSep: 70,
        animate: true,
        animationDuration: 500,
        fit: true,
        padding: 40,
      } as LayoutOptions
    case 'concentric':
      return {
        name: 'concentric',
        concentric: (n: cytoscape.NodeSingular) => n.data('weight') ?? 1,
        levelWidth: () => 2,
        minNodeSpacing: 26,
        animate: true,
        animationDuration: 500,
        padding: 40,
      } as LayoutOptions
    case 'breadthfirst':
      return { name: 'breadthfirst', directed: true, spacingFactor: 1.1, animate: true, padding: 40 } as LayoutOptions
    case 'cose':
    default:
      return {
        name: 'cose',
        animate: true,
        animationDuration: 700,
        nodeRepulsion: () => 12000,
        idealEdgeLength: () => 90,
        edgeElasticity: () => 120,
        gravity: 0.3,
        padding: 40,
        fit: true,
      } as LayoutOptions
  }
}
