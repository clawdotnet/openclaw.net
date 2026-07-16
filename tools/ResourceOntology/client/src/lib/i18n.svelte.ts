type Locale = 'en' | 'zh'

const enFallback: Record<string, string> = {
  'app.title': 'Resource Ontology Visualiser',
  'app.fallback': 'Describing Visualization Resources',
  'app.noFiles': 'No ontology files found',
  'app.openFile': 'Open OWL file…',
  'app.loading': 'Parsing ontology…',
  'app.dropHint': 'Drop an .owl file to visualise',
  'stats.classes': 'Classes',
  'stats.objProps': 'Obj. props',
  'stats.dataProps': 'Data props',
  'stats.instances': 'Instances',
  'stats.restrictions': 'Restrictions',
  'stats.disjoint': 'Disjoint',
  'sidebar.search': 'Search entities…',
  'sidebar.hierarchy': 'Hierarchy',
  'sidebar.classes': 'Classes',
  'sidebar.properties': 'Properties',
  'sidebar.instances': 'Instances',
  'sidebar.searchHint': 'Showing all classes matching "{q}". Clear search to see the tree.',
  'graph.layout': 'Graph layout',
  'graph.force': 'Force layout',
  'graph.hierarchy': 'Hierarchy (tree)',
  'graph.concentric': 'Concentric',
  'graph.breadthfirst': 'Breadth-first',
  'graph.fit': 'Fit',
  'graph.showClasses': 'Classes',
  'graph.showIndividuals': 'Individuals',
  'graph.legend': 'Relationships',
  'graph.subClassOf': 'sub-class of',
  'graph.restriction': 'restriction',
  'graph.objprop': 'object property',
  'graph.disjoint': 'disjoint with',
  'graph.instanceOf': 'instance of',
  'graph.assertion': 'assertion',
  'details.empty1': 'Select a class, property, or instance to inspect it.',
  'details.empty2': 'Click nodes in the graph or items in the sidebar.',
  'details.subClassOf': 'Sub-class of',
  'details.subClasses': 'Sub-classes ({n})',
  'details.restrictions': 'Restrictions',
  'details.disjointWith': 'Disjoint with',
  'details.equivalentTo': 'Equivalent to',
  'details.referencedBy': 'Referenced by properties',
  'details.instances': 'Instances ({n})',
  'details.type': 'Type',
  'details.domain': 'Domain',
  'details.range': 'Range',
  'details.inverseOf': 'Inverse of',
  'details.subPropertyOf': 'Sub-property of',
  'details.propertyValues': 'Property values',
  'details.objectProperty': 'object property',
  'details.datatypeProperty': 'datatype property',
  'details.more': '…and {n} more',
}

let currentDict: Record<string, string> = { ...enFallback }

export const i18n = $state({
  locale: loadLocale() as Locale,
})

export function t(key: string): string {
  return currentDict[key] ?? enFallback[key] ?? key
}

export async function setLocale(lang: Locale) {
  i18n.locale = lang
  saveLocale(lang)
  try {
    const mod = await import(`./locales/${lang}.json`) as { default: Record<string, string> }
    currentDict = { ...enFallback, ...mod.default }
  } catch {
    // JSON load failed — keep current dict (or fallback).
  }
}

export async function initLocale() {
  await setLocale(i18n.locale)
}

function loadLocale(): Locale {
  try {
    const v = localStorage.getItem('ontology-lang')
    if (v === 'zh') return 'zh'
  } catch { /* localStorage unavailable */ }
  return 'zh'
}

function saveLocale(lang: Locale) {
  try { localStorage.setItem('ontology-lang', lang) } catch { /* ignore */ }
}
