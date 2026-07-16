<script lang="ts">
  import { store, select, prettify } from './store.svelte'
  import { t } from './i18n.svelte.ts'
  import type { OwlClass } from './types'

  let tab = $state<'hierarchy' | 'classes' | 'properties' | 'individuals'>('hierarchy')
  let expanded = $state<Record<string, boolean>>({})

  const q = $derived(store.search.trim().toLowerCase())
  const match = (s: string) => !q || s.toLowerCase().includes(q)

  // Build parent -> children adjacency for the class hierarchy.
  const tree = $derived.by(() => {
    const classes = store.ontology?.classes ?? []
    const byId = new Map(classes.map((c) => [c.id, c]))
    const children = new Map<string, OwlClass[]>()
    const roots: OwlClass[] = []
    for (const c of classes) {
      const parents = c.subClassOf.filter((p) => byId.has(p))
      if (parents.length === 0) roots.push(c)
      for (const p of parents) {
        if (!children.has(p)) children.set(p, [])
        children.get(p)!.push(c)
      }
    }
    const sort = (a: OwlClass, b: OwlClass) => a.name.localeCompare(b.name)
    roots.sort(sort)
    children.forEach((v) => v.sort(sort))
    return { roots, children }
  })

  const filteredClasses = $derived((store.ontology?.classes ?? []).filter((c) => match(c.name) || match(c.label ?? '')))
  const filteredProps = $derived((store.ontology?.properties ?? []).filter((p) => match(p.name)))
  const filteredInds = $derived((store.ontology?.individuals ?? []).filter((i) => match(i.name)))

  function toggle(id: string) {
    expanded[id] = !expanded[id]
  }
  const isSelected = (id: string) => store.selection?.id === id

  const tabs = [
    { id: 'hierarchy', label: t('sidebar.hierarchy') },
    { id: 'classes', label: t('sidebar.classes') },
    { id: 'properties', label: t('sidebar.properties') },
    { id: 'individuals', label: t('sidebar.instances') },
  ] as const
</script>

{#snippet treeNode(cls: OwlClass, depth: number)}
  {@const kids = tree.children.get(cls.id) ?? []}
  <div>
    <div
      class="group flex items-center gap-1 rounded px-1 py-0.5 text-sm hover:bg-panel2"
      class:bg-panel2={isSelected(cls.id)}
      style:padding-left="{depth * 12 + 4}px"
    >
      {#if kids.length}
        <button class="w-4 shrink-0 text-muted hover:text-ink" onclick={() => toggle(cls.id)}>
          {expanded[cls.id] ? '▾' : '▸'}
        </button>
      {:else}
        <span class="w-4 shrink-0 text-center text-edge">·</span>
      {/if}
      <button
        class="flex-1 truncate text-left"
        class:text-klass={isSelected(cls.id)}
        title={cls.name}
        onclick={() => select('class', cls.id)}
      >
        {cls.label || prettify(cls.name)}
        {#if cls.instanceCount > 0}<span class="ml-1 text-xs text-muted">({cls.instanceCount})</span>{/if}
      </button>
    </div>
    {#if expanded[cls.id]}
      {#each kids as kid (kid.id)}
        {@render treeNode(kid, depth + 1)}
      {/each}
    {/if}
  </div>
{/snippet}

<div class="flex h-full flex-col">
  <!-- Search -->
  <div class="border-b border-edge p-2">
    <input
      type="search"
      placeholder={t('sidebar.search')}
      bind:value={store.search}
      class="w-full rounded-md border border-edge bg-canvas px-2 py-1.5 text-sm text-ink outline-none focus:border-klass"
    />
  </div>

  <!-- Tabs -->
  <div class="flex border-b border-edge text-xs">
    {#each tabs as t}
      <button
        class="flex-1 px-1 py-2 font-medium hover:text-ink"
        class:text-klass={tab === t.id}
        class:text-muted={tab !== t.id}
        class:border-b-2={tab === t.id}
        class:border-klass={tab === t.id}
        onclick={() => (tab = t.id)}>{t.label}</button
      >
    {/each}
  </div>

  <div class="flex-1 overflow-y-auto p-2">
    {#if tab === 'hierarchy'}
      {#if q}
        <p class="mb-2 px-1 text-xs text-muted">{t('sidebar.searchHint').replace('{q}', store.search)}</p>
        {#each filteredClasses as c (c.id)}
          <button
            class="block w-full truncate rounded px-2 py-1 text-left text-sm hover:bg-panel2"
            class:text-klass={isSelected(c.id)}
            onclick={() => select('class', c.id)}>{c.label || prettify(c.name)}</button
          >
        {/each}
      {:else}
        {#each tree.roots as root (root.id)}
          {@render treeNode(root, 0)}
        {/each}
      {/if}
    {:else if tab === 'classes'}
      {#each filteredClasses as c (c.id)}
        <button
          class="flex w-full items-center gap-2 rounded px-2 py-1 text-left text-sm hover:bg-panel2"
          class:text-klass={isSelected(c.id)}
          onclick={() => select('class', c.id)}
        >
          <span class="h-2 w-2 shrink-0 rounded-full bg-klass"></span>
          <span class="flex-1 truncate" title={c.name}>{c.label || prettify(c.name)}</span>
        </button>
      {/each}
    {:else if tab === 'properties'}
      {#each filteredProps as p (p.id)}
        <button
          class="flex w-full items-center gap-2 rounded px-2 py-1 text-left text-sm hover:bg-panel2"
          class:text-objprop={isSelected(p.id)}
          onclick={() => select('property', p.id)}
        >
          <span
            class="shrink-0 rounded px-1 text-[10px] uppercase"
            class:bg-objprop={p.kind === 'object'}
            class:text-canvas={p.kind === 'object'}
            class:bg-datatype={p.kind === 'datatype'}
            class:bg-edge={p.kind === 'annotation'}>{p.kind[0]}</span
          >
          <span class="flex-1 truncate" title={p.name}>{p.name}</span>
        </button>
      {/each}
    {:else}
      {#each filteredInds as i (i.id)}
        <button
          class="flex w-full items-center gap-2 rounded px-2 py-1 text-left text-sm hover:bg-panel2"
          class:text-individual={isSelected(i.id)}
          onclick={() => select('individual', i.id)}
        >
          <span class="h-2 w-2 shrink-0 rounded-sm bg-individual"></span>
          <span class="flex-1 truncate" title={i.name}>{i.label || prettify(i.name)}</span>
        </button>
      {/each}
    {/if}
  </div>
</div>
