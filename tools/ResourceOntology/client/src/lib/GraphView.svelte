<script lang="ts">
  import { onMount } from 'svelte'
  import cytoscape from 'cytoscape'
  import { store, select } from './store.svelte'
  import { buildElements, cyStyle, makeLayout } from './graph'
  import { t, i18n } from './i18n.svelte.ts'

  let container: HTMLDivElement
  let cy: cytoscape.Core | undefined
  let layoutName = $state<'cose' | 'dagre' | 'concentric' | 'breadthfirst'>('cose')
  let lastFocusNonce = -1

  const edgeLegend = $derived.by(() => {
    void i18n.locale // track locale changes for reactivity
    return [
      { type: 'subClassOf', key: 'subClassOf', color: '#5b6784', label: t('graph.subClassOf') },
      { type: 'restriction', key: 'restriction', color: '#f472b6', label: t('graph.restriction') },
      { type: 'domainRange', key: 'domainRange', color: '#c084fc', label: t('graph.objprop') },
      { type: 'disjoint', key: 'disjoint', color: '#f87171', label: t('graph.disjoint') },
      { type: 'typeOf', key: 'typeOf', color: '#4ade80', label: t('graph.instanceOf') },
      { type: 'assertion', key: 'assertion', color: '#38bdf8', label: t('graph.assertion') },
    ] as const
  })

  function runLayout() {
    cy?.layout(makeLayout(layoutName)).run()
  }

  onMount(() => {
    cy = cytoscape({
      container,
      style: cyStyle,
      minZoom: 0.1,
      maxZoom: 4,
    })
    cy.on('tap', 'node', (e) => {
      const id = e.target.id()
      const kind = e.target.data('kind') as 'class' | 'individual'
      select(kind === 'individual' ? 'individual' : 'class', id, false)
    })
    cy.on('tap', (e) => {
      if (e.target === cy) {
        store.selection = null
      }
    })
    return () => cy?.destroy()
  })

  // Rebuild graph whenever the ontology, filters, or layout change.
  $effect(() => {
    if (!cy || !store.ontology) return
    const els = buildElements(store.ontology, {
      showClasses: store.filters.showClasses,
      showIndividuals: store.filters.showIndividuals,
      subClassOf: store.filters.subClassOf,
      disjoint: store.filters.disjoint,
      restriction: store.filters.restriction,
      domainRange: store.filters.domainRange,
      typeOf: store.filters.typeOf,
      assertion: store.filters.assertion,
    })
    cy.elements().remove()
    cy.add(els)
    runLayout()
  })

  // Highlight the current selection and its neighbourhood.
  $effect(() => {
    const sel = store.selection
    if (!cy) return
    cy.batch(() => {
      cy!.elements().removeClass('selected faded highlight')
      if (!sel) return
      const node = cy!.getElementById(sel.id)
      if (node.empty()) return
      const hood = node.closedNeighborhood()
      cy!.elements().addClass('faded')
      hood.removeClass('faded').addClass('highlight')
      node.addClass('selected')
    })
  })

  // Centre the viewport when a focus is requested from a list/tree click.
  $effect(() => {
    const req = store.focusRequest
    if (!cy || !req || req.nonce === lastFocusNonce) return
    lastFocusNonce = req.nonce
    const node = cy.getElementById(req.id)
    if (!node.empty()) cy.animate({ center: { eles: node }, zoom: 1.4 }, { duration: 450 })
  })
</script>

<div class="relative h-full w-full">
  <div bind:this={container} class="graph-grid h-full w-full"></div>

  <!-- Layout + view controls -->
  <div class="absolute right-3 top-3 flex flex-wrap items-center gap-2">
    <select
      bind:value={layoutName}
      class="rounded-md border border-edge bg-panel/90 px-2 py-1 text-xs text-ink shadow-lg outline-none"
      title={t('graph.layout')}
    >
      <option value="cose">{t('graph.force')}</option>
      <option value="dagre">{t('graph.hierarchy')}</option>
      <option value="concentric">{t('graph.concentric')}</option>
      <option value="breadthfirst">{t('graph.breadthfirst')}</option>
    </select>
    <button
      class="rounded-md border border-edge bg-panel/90 px-2 py-1 text-xs text-ink shadow-lg hover:bg-panel2"
      onclick={() => cy?.fit(undefined, 40)}>{t('graph.fit')}</button
    >
    <label class="flex items-center gap-1 rounded-md border border-edge bg-panel/90 px-2 py-1 text-xs shadow-lg">
      <input type="checkbox" bind:checked={store.filters.showClasses} /> {t('graph.showClasses')}
    </label>
    <label class="flex items-center gap-1 rounded-md border border-edge bg-panel/90 px-2 py-1 text-xs shadow-lg">
      <input type="checkbox" bind:checked={store.filters.showIndividuals} /> {t('graph.showIndividuals')}
    </label>
  </div>

  <!-- Interactive edge legend / toggles -->
  <div class="absolute bottom-3 left-3 rounded-lg border border-edge bg-panel/90 p-2 text-xs shadow-lg backdrop-blur">
    <div class="mb-1 font-semibold text-muted">{t('graph.legend')}</div>
    <div class="grid grid-cols-1 gap-1">
      {#each edgeLegend as e}
        <label class="flex cursor-pointer items-center gap-2">
          <input type="checkbox" bind:checked={store.filters[e.key]} />
          <span class="inline-block h-0.5 w-5" style:background-color={e.color}></span>
          <span class="text-ink">{e.label}</span>
        </label>
      {/each}
    </div>
  </div>
</div>
