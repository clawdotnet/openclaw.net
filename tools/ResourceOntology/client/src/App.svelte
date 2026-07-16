<script lang="ts">
  import { onMount } from 'svelte'
  import { store } from './lib/store.svelte'
  import { parseOntologyFile, listOntologyFiles, loadOntology, exportJsonLd, exportUploadedJsonLd } from './lib/api'
  import { t, i18n, initLocale, setLocale } from './lib/i18n.svelte.ts'
  import GraphView from './lib/GraphView.svelte'
  import Sidebar from './lib/Sidebar.svelte'
  import DetailsPanel from './lib/DetailsPanel.svelte'
  import SourceView from './lib/SourceView.svelte'

  let fileInput: HTMLInputElement
  let dragging = $state(false)

  async function initFiles() {
    try {
      const res = await listOntologyFiles()
      store.fileList = res.files
    } catch {
      store.fileList = []
    }
  }

  async function selectFile(fileName: string) {
    store.jsonldSource = null
    store.jsonldCompact = null
    store.jsonldExpanded = null
    store.activeTab = 'graph'
    store.loading = true
    store.error = null
    store.currentFile = fileName
    store.rawOwlText = null
    store.rawOwlFileName = null
    try {
      store.ontology = await loadOntology(fileName)
      store.selection = null
    } catch (e) {
      store.error = e instanceof Error ? e.message : String(e)
    } finally {
      store.loading = false
    }
  }

  async function loadFile(file: File) {
    store.jsonldSource = null
    store.jsonldCompact = null
    store.jsonldExpanded = null
    store.activeTab = 'graph'
    store.loading = true
    store.error = null
    try {
      const text = await file.text()
      store.rawOwlText = text
      store.rawOwlFileName = file.name
      store.ontology = await parseOntologyFile(file)
      store.selection = null
    } catch (e) {
      store.error = e instanceof Error ? e.message : String(e)
    } finally {
      store.loading = false
    }
  }

  function onPick(e: Event) {
    const f = (e.target as HTMLInputElement).files?.[0]
    if (f) loadFile(f)
  }
  function onDrop(e: DragEvent) {
    e.preventDefault()
    dragging = false
    const f = e.dataTransfer?.files?.[0]
    if (f) loadFile(f)
  }

  async function handleExport() {
    store.loading = true
    store.error = null
    try {
      let compact: string
      if (store.currentFile) {
        compact = await exportJsonLd(store.currentFile, 'compact')
      } else if (store.rawOwlText && store.rawOwlFileName) {
        compact = await exportUploadedJsonLd(store.rawOwlText, store.rawOwlFileName, 'compact')
      } else {
        store.error = 'No ontology loaded.'
        return
      }
      store.jsonldCompact = compact
      store.jsonldSource = compact
      store.jsonldExpanded = null
      store.jsonldFormat = 'compact'
      store.activeTab = 'source'
    } catch (e) {
      store.error = e instanceof Error ? e.message : String(e)
    } finally {
      store.loading = false
    }
  }

  onMount(async () => {
    await initLocale()
    await initFiles()
    if (store.fileList.length > 0) {
      await selectFile(store.fileList[0].name)
    }
  })

  const stats = $derived(store.ontology?.stats)
  const chips = $derived.by(() => {
    void i18n.locale // track locale changes for reactivity
    return stats
      ? [
          { label: t('stats.classes'), value: stats.classes, color: 'bg-klass' },
          { label: t('stats.objProps'), value: stats.objectProperties, color: 'bg-objprop' },
          { label: t('stats.dataProps'), value: stats.datatypeProperties, color: 'bg-datatype' },
          { label: t('stats.instances'), value: stats.individuals, color: 'bg-individual' },
          { label: t('stats.restrictions'), value: stats.restrictions, color: 'bg-restriction' },
          { label: t('stats.disjoint'), value: stats.disjointAxioms, color: 'bg-disjoint' },
        ]
      : []
  })
</script>

<svelte:window
  ondragover={(e) => {
    e.preventDefault()
    dragging = true
  }}
  ondragleave={() => (dragging = false)}
  ondrop={onDrop}
/>

<div class="flex h-screen flex-col">
  <!-- Header -->
  <header class="flex flex-wrap items-center gap-x-4 gap-y-2 border-b border-edge bg-panel px-4 py-2">
    <div class="flex items-center gap-2">
      <div class="grid h-8 w-8 place-items-center rounded-md bg-klass/20 text-lg text-klass">◈</div>
      <div>
        <h1 class="text-sm font-semibold leading-tight text-ink">{t('app.title')}</h1>
        <p class="text-[11px] leading-tight text-muted">
          {store.ontology?.meta.sourceName ?? t('app.fallback')}
        </p>
      </div>
    </div>

    <!-- Stat chips -->
    <div class="flex flex-wrap items-center gap-1.5">
      {#each chips as c}
        <span class="flex items-center gap-1.5 rounded-full border border-edge bg-canvas px-2 py-0.5 text-xs">
          <span class="h-2 w-2 rounded-full {c.color}"></span>
          <span class="text-muted">{c.label}</span>
          <span class="font-semibold text-ink">{c.value}</span>
        </span>
      {/each}
    </div>

    <div class="ml-auto flex items-center gap-1 mr-2">
      <button
        class="rounded px-1.5 py-1 text-[11px] font-medium transition-colors"
        class:bg-klass={i18n.locale === 'en'}
        class:text-canvas={i18n.locale === 'en'}
        class:text-muted={i18n.locale !== 'en'}
        class:hover:text-ink={i18n.locale !== 'en'}
        onclick={() => setLocale('en')}>EN</button
      >
      <button
        class="rounded px-1.5 py-1 text-[11px] font-medium transition-colors"
        class:bg-klass={i18n.locale === 'zh'}
        class:text-canvas={i18n.locale === 'zh'}
        class:text-muted={i18n.locale !== 'zh'}
        class:hover:text-ink={i18n.locale !== 'zh'}
        onclick={() => setLocale('zh')}>中</button
      >
    </div>
    <div class="ml-auto flex items-center gap-2">
      {#if store.fileList.length > 0}
        <select
          class="rounded-md border border-edge bg-canvas px-3 py-1.5 text-xs text-ink outline-none hover:border-klass focus:border-klass"
          value={store.currentFile ?? ''}
          onchange={(e) => {
            const val = (e.target as HTMLSelectElement).value
            if (val) selectFile(val)
          }}
        >
          {#each store.fileList as f}
            <option value={f.name}>{f.displayName}</option>
          {/each}
        </select>
      {:else}
        <span class="text-xs text-muted">{t('app.noFiles')}</span>
      {/if}
      <button
        class="rounded-md bg-klass px-3 py-1.5 text-xs font-medium text-canvas hover:opacity-90"
        onclick={() => fileInput.click()}>{t('app.openFile')}</button
      >
      <button
        class="rounded-md border border-edge bg-canvas px-3 py-1.5 text-xs font-medium text-ink hover:border-klass hover:text-klass disabled:opacity-50"
        disabled={!store.ontology || store.loading}
        onclick={handleExport}
      >Export JSON-LD</button
      >
      <input bind:this={fileInput} type="file" accept=".owl,.rdf,.xml" class="hidden" onchange={onPick} />
    </div>
  </header>

  <!-- Body -->
  <div class="relative flex min-h-0 flex-1">
    {#if store.loading}
      <div class="absolute inset-0 z-20 grid place-items-center bg-canvas/70 backdrop-blur-sm">
        <div class="flex items-center gap-3 text-muted">
          <span class="h-5 w-5 animate-spin rounded-full border-2 border-edge border-t-klass"></span>
          {t('app.loading')}
        </div>
      </div>
    {/if}

    {#if store.error}
      <div
        class="absolute left-1/2 top-4 z-30 -translate-x-1/2 rounded-md border border-disjoint/50 bg-disjoint/15 px-4 py-2 text-sm text-ink shadow-lg"
      >
        {store.error}
      </div>
    {/if}

    <aside class="w-72 shrink-0 border-r border-edge bg-panel"><Sidebar /></aside>
    <main class="min-w-0 flex-1">
      {#if store.jsonldSource}
        <div class="flex border-b border-edge bg-panel">
          <button
            class="px-4 py-2 text-xs font-medium border-b-2 transition-colors"
            class:border-klass={store.activeTab === 'graph'}
            class:text-klass={store.activeTab === 'graph'}
            class:border-transparent={store.activeTab !== 'graph'}
            class:text-muted={store.activeTab !== 'graph'}
            class:hover:text-ink={store.activeTab !== 'graph'}
            onclick={() => (store.activeTab = 'graph')}
          >Graph</button
          >
          <button
            class="px-4 py-2 text-xs font-medium border-b-2 transition-colors"
            class:border-klass={store.activeTab === 'source'}
            class:text-klass={store.activeTab === 'source'}
            class:border-transparent={store.activeTab !== 'source'}
            class:text-muted={store.activeTab !== 'source'}
            class:hover:text-ink={store.activeTab !== 'source'}
            onclick={() => (store.activeTab = 'source')}
          >Source</button
          >
        </div>
      {/if}
      {#if store.activeTab === 'source' && store.jsonldSource}
        <SourceView />
      {:else}
        <GraphView />
      {/if}
    </main>
    <aside class="w-80 shrink-0 border-l border-edge bg-panel"><DetailsPanel /></aside>
  </div>
</div>

{#if dragging}
  <div class="pointer-events-none fixed inset-0 z-50 grid place-items-center bg-canvas/80">
    <div class="rounded-xl border-2 border-dashed border-klass px-10 py-8 text-lg text-klass">
      {t('app.dropHint')}
    </div>
  </div>
{/if}
