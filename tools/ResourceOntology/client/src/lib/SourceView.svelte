<script lang="ts">
  import { store } from './store.svelte'
  import { exportJsonLd, exportUploadedJsonLd } from './api'

  let loading = $state(false)

  async function switchFormat(fmt: 'compact' | 'expanded') {
    store.jsonldFormat = fmt
    const cached = fmt === 'compact' ? store.jsonldCompact : store.jsonldExpanded
    if (cached !== null) {
      store.jsonldSource = cached
      return
    }
    loading = true
    try {
      let result: string
      if (store.currentFile) {
        result = await exportJsonLd(store.currentFile, fmt)
      } else {
        result = await exportUploadedJsonLd(store.rawOwlText!, store.rawOwlFileName!, fmt)
      }
      if (fmt === 'compact') store.jsonldCompact = result
      else store.jsonldExpanded = result
      store.jsonldSource = result
    } catch (e) {
      store.error = e instanceof Error ? e.message : String(e)
    } finally {
      loading = false
    }
  }

  function download() {
    if (store.currentFile) {
      const url = `/api/ontology/export-jsonld?file=${encodeURIComponent(store.currentFile)}&format=${store.jsonldFormat}&download=true`
      window.open(url, '_blank')
    } else {
      // For uploaded files, trigger download via Blob
      const blob = new Blob([store.jsonldSource!], { type: 'application/ld+json' })
      const url = URL.createObjectURL(blob)
      const a = document.createElement('a')
      a.href = url
      a.download = `${store.rawOwlFileName?.replace(/\.owl$/i, '') ?? 'ontology'}.jsonld`
      a.click()
      URL.revokeObjectURL(url)
    }
  }
</script>

<div class="flex h-full flex-col">
  <!-- Toolbar -->
  <div class="flex items-center justify-between border-b border-edge px-3 py-2">
    <div class="flex items-center gap-2">
      <span class="text-xs text-muted">Format:</span>
      <select
        class="rounded border border-edge bg-canvas px-2 py-1 text-xs text-ink outline-none hover:border-klass focus:border-klass"
        value={store.jsonldFormat}
        onchange={(e) => switchFormat((e.target as HTMLSelectElement).value as 'compact' | 'expanded')}
      >
        <option value="compact">Compact</option>
        <option value="expanded">Expanded</option>
      </select>
    </div>
    <button
      class="rounded bg-klass px-3 py-1 text-xs font-medium text-canvas hover:opacity-90 disabled:opacity-50"
      disabled={!store.jsonldSource || loading}
      onclick={download}
    >
      Download
    </button>
  </div>

  <!-- Source code -->
  <div class="flex-1 overflow-auto">
    {#if loading}
      <div class="grid h-full place-items-center text-sm text-muted">
        <span class="h-5 w-5 animate-spin rounded-full border-2 border-edge border-t-klass"></span>
      </div>
    {:else if store.jsonldSource}
      <pre class="p-4 text-xs font-mono text-ink whitespace-pre-wrap break-all"><code>{store.jsonldSource}</code></pre>
    {:else}
      <div class="grid h-full place-items-center text-sm text-muted">
        <p>No JSON-LD source loaded.</p>
      </div>
    {/if}
  </div>
</div>
