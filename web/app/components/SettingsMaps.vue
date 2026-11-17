<script setup lang="ts">
import type { MapSettingsDto, TileProvider, MapLayerCheckDto } from '~/types/api'
import { errorMessage } from '~/utils/format'
const providers = ref<TileProvider[]>([]), defaults = ref<TileProvider[]>([]), cacheRoot = ref('')
const loading = ref(true), error = ref(''), busy = ref(false), editorOpen = ref(false), editorBusy = ref(false), deleteOpen = ref(false)
const editing = ref<TileProvider>(), deleting = ref<TileProvider>()
const checking = ref<string | null>(null)
const checkResults = ref<Record<string, MapLayerCheckDto>>({})
async function checkLayer(provider: TileProvider) {
  if (checking.value || busy.value) return
  checking.value = provider.id
  const next = { ...checkResults.value }
  delete next[provider.id]
  checkResults.value = next
  try {
    const result = await $fetch<MapLayerCheckDto>(`/api/map/settings/${provider.id}/check`, { retry: 0 })
    checkResults.value = { ...checkResults.value, [provider.id]: result }
  } catch (cause) {
    checkResults.value = { ...checkResults.value, [provider.id]: { status: 'error', message: errorMessage(cause), hasCache: false, hasUrl: Boolean(provider.urlTemplate) } }
  } finally { checking.value = null }
}
const requestOwner = useLatestRequest()
async function load() {
  const request = requestOwner.start(); loading.value = true; error.value = ''
  try {
    const result = await $fetch<MapSettingsDto>('/api/map/settings', { signal: request.signal })
    if (request.isCurrent()) { providers.value = result.providers; defaults.value = result.defaults; cacheRoot.value = result.cacheRoot; checkResults.value = {} }
  } catch (cause) { if (request.isCurrent()) error.value = errorMessage(cause) }
  finally { if (request.isCurrent()) loading.value = false }
}
function edit(provider?: TileProvider) {
  editorBusy.value = false
  editing.value = provider ? { ...provider } : { id: '', name: '', folder: `layer-${Date.now().toString(36)}`, scheme: 'xyz', urlTemplate: '', apiKey: '', mapKey: '', subdomains: '0,1,2,3,4,5,6,7', forceRefresh: false, enabled: true, sortOrder: providers.value.length }
  error.value = ''; editorOpen.value = true
}
async function saved() { editorBusy.value = false; editorOpen.value = false; await load() }
async function toggle(provider: TileProvider, enabled: boolean | 'indeterminate') {
  if (busy.value) return
  busy.value = true; error.value = ''
  try {
    const value = await $fetch<TileProvider>(`/api/map/settings/${provider.id}/enabled`, { method: 'PUT', body: { enabled: enabled === true }, retry: 0 })
    providers.value = providers.value.map(item => item.id === value.id ? value : item)
    delete checkResults.value[provider.id]
  } catch (cause) { error.value = errorMessage(cause) }
  finally { busy.value = false }
}
async function move(index: number, direction: number) {
  if (busy.value || index + direction < 0 || index + direction >= providers.value.length) return
  const ordered = [...providers.value]; [ordered[index], ordered[index + direction]] = [ordered[index + direction]!, ordered[index]!]
  busy.value = true; error.value = ''
  try {
    const value = await $fetch<MapSettingsDto>('/api/map/settings/order', { method: 'PUT', body: { ids: ordered.map(item => item.id) }, retry: 0 })
    providers.value = value.providers
  } catch (cause) { error.value = errorMessage(cause) }
  finally { busy.value = false }
}
async function remove() {
  if (!deleting.value || busy.value) return
  busy.value = true; error.value = ''
  try { await $fetch(`/api/map/settings/${deleting.value.id}`, { method: 'DELETE', query: { confirm: true }, retry: 0 }); deleteOpen.value = false; await load() }
  catch (cause) { error.value = errorMessage(cause) }
  finally { busy.value = false }
}
onMounted(load)
</script>
<template>
  <section class="space-y-3">
    <div class="section-heading"><h2 class="section-title">图层管理</h2><UButton label="新增图层" icon="i-lucide-plus" variant="outline" color="neutral" :disabled="busy" data-testid="map-layer-new" @click="edit()" /></div>
    <p class="text-sm text-muted">上方图层覆盖下方图层。点击“检查”可检测本地图块；配置了调用链接时也会测试一个在线图块。结果仅在此页显示，不代表所有区域都已缓存。</p>
    <UAlert v-if="error && !deleteOpen" role="alert" :description="error" color="error" />
    <PageState :loading="loading" @retry="load">
      <ul v-if="providers.length" class="group-list">
        <li v-for="(provider, index) in providers" :key="provider.id" class="list-row" data-testid="map-layer-row">
          <UCheckbox :model-value="provider.enabled" :aria-label="`启用${provider.name}`" :disabled="busy" @update:model-value="toggle(provider, $event)" />
          <div class="row-body"><p class="font-medium truncate">{{ provider.name }}</p><p class="row-secondary truncate">{{ provider.scheme.toUpperCase() }} · {{ provider.folder }}</p><p v-if="checkResults[provider.id]" role="status" class="mt-1 text-xs" :class="checkResults[provider.id]?.status === 'error' || checkResults[provider.id]?.status === 'unavailable' ? 'text-error' : 'text-muted'">{{ checkResults[provider.id]?.message }}</p></div>
          <div class="flex shrink-0 items-center"><UButton type="button" label="检查" :aria-label="`检查${provider.name}图层可用性`" size="xs" color="neutral" variant="outline" :loading="checking === provider.id" :disabled="busy || checking !== null" @click="checkLayer(provider)" /><UButton icon="i-lucide-arrow-up" :aria-label="`上移${provider.name}`" variant="ghost" color="neutral" :disabled="busy || index === 0" @click="move(index, -1)" /><UButton icon="i-lucide-arrow-down" :aria-label="`下移${provider.name}`" variant="ghost" color="neutral" :disabled="busy || index === providers.length - 1" @click="move(index, 1)" /><UDropdownMenu :items="[[{ label: '编辑图层', onSelect: () => edit(provider) }, { label: '删除图层', color: 'error', onSelect: () => { deleting = provider; error = ''; deleteOpen = true } }]]"><UButton icon="i-lucide-ellipsis" :aria-label="`${provider.name}操作`" variant="ghost" color="neutral" :disabled="busy" /></UDropdownMenu></div>
        </li>
      </ul>
      <EmptyState v-else title="暂无图层"><UButton label="新增图层" @click="edit()" /></EmptyState>
    </PageState>
  </section>
  <UModal v-model:open="editorOpen" :title="editing?.id ? '编辑图层' : '新增图层'" :dismissible="!editorBusy" :close="!editorBusy" :ui="{ content: 'max-w-[600px]' }"><template #body><SettingsMapProvider v-if="editing" :key="editing.id || 'new'" :initial="editing" :defaults="defaults" :cache-root="cacheRoot" @busy="editorBusy = $event" @saved="saved" /></template></UModal>
  <UModal v-model:open="deleteOpen" :title="`删除图层「${deleting?.name ?? ''}」？`" description="只删除图层配置，保留缓存文件" :dismissible="!busy" :close="!busy"><template #body><UAlert v-if="error" role="alert" :description="error" color="error" /></template><template #footer><UButton label="取消" variant="outline" color="neutral" :disabled="busy" @click="deleteOpen = false" /><UButton label="删除" color="error" :loading="busy" @click="remove" /></template></UModal>
</template>
