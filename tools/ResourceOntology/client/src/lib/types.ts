// Auto-generated from server DTOs via OpenAPI spec.
// Run `npm run gentypes` (with server running on :5174) to regenerate.
// Client-only types (Selection, GraphFilters) are defined below.

import type { components } from './types.generated'

/**
 * Recursively convert OpenAPI-generated types (all-optional, all-nullable)
 * into the concrete shape the C# server actually emits.  The server always
 * sends every field and C# `= ""` / `= new()` defaults mean scalars and
 * arrays are never null at runtime.
 */
type Solid<T> = T extends (infer U)[]
  ? Solid<U>[]
  : T extends object
    ? { [K in keyof T]-?: Solid<NonNullable<T[K]>> }
    : NonNullable<T>

// ---- DTO types (mirrored from server/Models/OntologyDtos.cs) -----------------

export type OntologyMeta   = Solid<components['schemas']['OntologyMeta']>
export type Restriction    = Solid<components['schemas']['RestrictionDto']>
export type OwlClass       = Solid<components['schemas']['OwlClassDto']>
export type OwlProperty    = Solid<components['schemas']['OwlPropertyDto']>
export type Assertion      = Solid<components['schemas']['AssertionDto']>
export type OwlIndividual  = Solid<components['schemas']['OwlIndividualDto']>
export type Stats          = Solid<components['schemas']['StatsDto']>
export type Ontology       = Solid<components['schemas']['OntologyDto']>

// ---- Client-only types --------------------------------------------------------

export type EntityKind = 'class' | 'property' | 'individual'

export interface Selection {
  kind: EntityKind
  id: string
}

export interface GraphFilters {
  showClasses: boolean
  showIndividuals: boolean
  subClassOf: boolean
  disjoint: boolean
  restriction: boolean
  domainRange: boolean
  typeOf: boolean
  assertion: boolean
}
