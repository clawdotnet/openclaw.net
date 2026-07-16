<script lang="ts">
  import { store, select, classById, propertyById, individualById, nameOf, prettify } from './store.svelte'
  import { t } from './i18n.svelte.ts'
  import type { EntityKind } from './types'

  const sel = $derived(store.selection)
  const cls = $derived(sel?.kind === 'class' ? classById(sel.id) : undefined)
  const prop = $derived(sel?.kind === 'property' ? propertyById(sel.id) : undefined)
  const ind = $derived(sel?.kind === 'individual' ? individualById(sel.id) : undefined)

  // Sub-classes and instances are derived (not stored directly on the class DTO).
  const subclasses = $derived(
    cls ? (store.ontology?.classes ?? []).filter((c) => c.subClassOf.includes(cls.id)) : []
  )
  const instances = $derived(
    cls ? (store.ontology?.individuals ?? []).filter((i) => i.types.includes(cls.id)) : []
  )
  const usedBy = $derived(
    cls
      ? (store.ontology?.properties ?? []).filter((p) => p.domains.includes(cls!.id) || p.ranges.includes(cls!.id))
      : []
  )

  function kindOf(id: string): EntityKind | null {
    if (classById(id)) return 'class'
    if (propertyById(id)) return 'property'
    if (individualById(id)) return 'individual'
    return null
  }
  function go(id: string) {
    const k = kindOf(id)
    if (k) select(k, id)
  }
</script>

{#snippet ref(id: string)}
  {@const k = kindOf(id)}
  <button
    class="rounded px-1.5 py-0.5 text-xs hover:underline"
    class:bg-klass={k === 'class'}
    class:bg-objprop={k === 'property'}
    class:bg-individual={k === 'individual'}
    class:text-canvas={!!k}
    class:bg-edge={!k}
    class:text-muted={!k}
    onclick={() => go(id)}
    title={id}>{nameOf(id)}</button
  >
{/snippet}

{#snippet section(title: string)}
  <h4 class="mb-1 mt-4 text-xs font-semibold uppercase tracking-wide text-muted">{title}</h4>
{/snippet}

<div class="flex h-full flex-col">
  {#if !sel}
    <div class="flex h-full flex-col items-center justify-center gap-2 p-6 text-center text-sm text-muted">
      <div class="text-3xl">◎</div>
      <p>{t('details.empty1')}</p>
      <p class="text-xs">{t('details.empty2')}</p>
    </div>
  {:else}
    <div class="border-b border-edge p-3">
      <div class="text-[10px] uppercase tracking-wide text-muted">{sel.kind === 'class' ? t('sidebar.classes') : sel.kind === 'property' ? t('sidebar.properties') : t('sidebar.instances')}</div>
      <div class="text-lg font-semibold text-ink">{nameOf(sel.id)}</div>
      <div class="mt-1 break-all font-mono text-[10px] text-muted">{sel.id}</div>
    </div>

    <div class="flex-1 overflow-y-auto p-3 text-sm">
      {#if cls}
        {#if cls.comment}<p class="text-muted">{cls.comment}</p>{/if}

        {#if cls.subClassOf.length}
          {@render section(t('details.subClassOf'))}
          <div class="flex flex-wrap gap-1">{#each cls.subClassOf as p}{@render ref(p)}{/each}</div>
        {/if}

        {#if subclasses.length}
          {@render section(t('details.subClasses').replace('{n}', String(subclasses.length)))}
          <div class="flex flex-wrap gap-1">{#each subclasses as c}{@render ref(c.id)}{/each}</div>
        {/if}

        {#if cls.restrictions.length}
          {@render section(t('details.restrictions'))}
          <ul class="space-y-1">
            {#each cls.restrictions as r}
              <li class="rounded bg-panel2 px-2 py-1 font-mono text-xs text-restriction">{r.display}</li>
            {/each}
          </ul>
        {/if}

        {#if cls.disjointWith.length}
          {@render section(t('details.disjointWith'))}
          <div class="flex flex-wrap gap-1">{#each cls.disjointWith as d}{@render ref(d)}{/each}</div>
        {/if}

        {#if cls.equivalentClasses.length}
          {@render section(t('details.equivalentTo'))}
          <div class="flex flex-wrap gap-1">{#each cls.equivalentClasses as e}{@render ref(e)}{/each}</div>
        {/if}

        {#if usedBy.length}
          {@render section(t('details.referencedBy'))}
          <div class="flex flex-wrap gap-1">{#each usedBy as p}{@render ref(p.id)}{/each}</div>
        {/if}

        {#if instances.length}
          {@render section(t('details.instances').replace('{n}', String(instances.length)))}
          <div class="flex flex-wrap gap-1">{#each instances.slice(0, 60) as i}{@render ref(i.id)}{/each}</div>
          {#if instances.length > 60}<p class="mt-1 text-xs text-muted">{t('details.more').replace('{n}', String(instances.length - 60))}</p>{/if}
        {/if}
      {:else if prop}
        {#if prop.comment}<p class="text-muted">{prop.comment}</p>{/if}
        {@render section(t('details.type'))}
        <div class="flex flex-wrap gap-1">
          <span class="rounded bg-panel2 px-2 py-0.5 text-xs">{prop.kind === 'object' ? t('details.objectProperty') : t('details.datatypeProperty')}</span>
          {#each prop.characteristics as ch}
            <span class="rounded bg-panel2 px-2 py-0.5 text-xs text-datatype">{ch}</span>
          {/each}
        </div>

        {#if prop.domains.length}
          {@render section(t('details.domain'))}
          <div class="flex flex-wrap gap-1">{#each prop.domains as d}{@render ref(d)}{/each}</div>
        {/if}
        {#if prop.ranges.length}
          {@render section(t('details.range'))}
          <div class="flex flex-wrap gap-1">
            {#each prop.ranges as r}
              {#if kindOf(r)}{@render ref(r)}{:else}<span class="rounded bg-edge px-1.5 py-0.5 font-mono text-xs text-muted">{prettify(r.split('#').pop() ?? r)}</span>{/if}
            {/each}
          </div>
        {/if}
        {#if prop.inverseOf}
          {@render section(t('details.inverseOf'))}
          <div>{@render ref(prop.inverseOf)}</div>
        {/if}
        {#if prop.subPropertyOf.length}
          {@render section(t('details.subPropertyOf'))}
          <div class="flex flex-wrap gap-1">{#each prop.subPropertyOf as s}{@render ref(s)}{/each}</div>
        {/if}
      {:else if ind}
        {#if ind.comment}<p class="text-muted">{ind.comment}</p>{/if}
        {#if ind.types.length}
          {@render section(t('details.type'))}
          <div class="flex flex-wrap gap-1">{#each ind.types as t}{@render ref(t)}{/each}</div>
        {/if}
        {#if ind.assertions.length}
          {@render section(t('details.propertyValues'))}
          <ul class="space-y-1">
            {#each ind.assertions as a}
              <li class="rounded bg-panel2 px-2 py-1 text-xs">
                <span class="text-objprop">{a.propertyName}</span>
                <span class="text-muted"> → </span>
                {#if a.isObject}{@render ref(a.value)}{:else}<span class="font-mono text-datatype">{a.value}</span>{/if}
              </li>
            {/each}
          </ul>
        {/if}
      {/if}
    </div>
  {/if}
</div>
