<script setup lang="ts">
import type { GeoShape, MapFeature, MapState } from '~/types/community'
import { errorMessage } from '~/utils/format'
interface ImportRow { row: number; sourceKey: string; kind: string; name: string; grid: string; type: string; address: string; contact: string; phone: string; notes: string; shape: GeoShape | null; error: string; warning: string; duplicate: boolean; selected?: boolean }
const kind = ref('point'), coordinates = ref('wgs84'), file = ref<File | null>(null), rows = ref<ImportRow[]>([])
const error = ref(''), message = ref(''), busy = ref(false), mapOpen = ref(false), selectedRow = ref<ImportRow | null>(null), map = ref<MapState>({ layers: [], features: [] })
const { bootstrap, load, refresh } = useAppData()
const grids = computed(() => (bootstrap.value?.communities ?? []).map(x => x.name))
const types = computed(() => (bootstrap.value?.pointTypes ?? []).map(x => x.name))
const selected = computed(() => rows.value.filter(x => x.selected))
const mapFeatures = computed<MapFeature[]>(() => selectedRow.value?.shape ? [{ id: 'import', kind: selectedRow.value.kind === 'grid' ? 'grid' : 'point', name: selectedRow.value.name, shape: selectedRow.value.shape, communityId: '', revision: 0, openIssues: 0, disabled: false }] : [])
function pick(event: Event) { file.value = (event.target as HTMLInputElement).files?.[0] ?? null; rows.value = []; message.value = ''; error.value = '' }
watch([kind, coordinates], () => { rows.value = []; message.value = '' })
function rowError(row: ImportRow) {
  if (row.error.startsWith('坐标')) return row.error
  if (!row.name.trim() || row.name.includes('\uFFFD')) return '请修改名称'
  if (kind.value === 'point' && (!row.grid || !row.address.trim())) return '请补充网格和地址'
  return ''
}
async function preview() {
  if (!file.value) return
  busy.value = true; error.value = ''; message.value = ''
  try { const body = new FormData(); body.append('file', file.value); body.append('kind', kind.value); body.append('coordinates', coordinates.value); const result = await $fetch<{ rows: ImportRow[] }>('/api/import/preview', { method: 'POST', body, retry: 0 }); rows.value = result.rows.map(x => ({ ...x, selected: !x.duplicate && !x.error })); map.value = await $fetch('/api/map') }
  catch (e) { error.value = errorMessage(e) } finally { busy.value = false }
}
async function apply() {
  if (!selected.value.length || busy.value) return
  if (selected.value.some(x => rowError(x))) { error.value = '请修改勾选行中的错误，或取消勾选'; return }
  busy.value = true; error.value = ''
  try { const result = await $fetch<{ added: number; skipped: number }>('/api/import/apply', { method: 'POST', body: { rows: selected.value }, retry: 0 }); message.value = `已导入 ${result.added} 条，重复跳过 ${result.skipped} 条`; for (const row of selected.value) { row.duplicate = true; row.selected = false } await refresh() }
  catch (e) { error.value = errorMessage(e) } finally { busy.value = false }
}
onMounted(load)
</script>
<template><div class="page-stack"><PageHeader title="导入资料" back="/settings" />
  <section class="panel panel-body space-y-4"><div class="field-grid"><UFormField label="导入什么"><USelect v-model="kind" :items="[{label:'场所资料',value:'point'},{label:'网格范围',value:'grid'}]" class="w-full" :disabled="busy" /></UFormField><UFormField label="坐标来源"><USelect v-model="coordinates" :items="[{label:'卫星、GPS坐标（WGS84）',value:'wgs84'},{label:'高德、原Map标记（GCJ-02）',value:'gcj02'}]" class="w-full" :disabled="busy" /></UFormField></div><input type="file" accept=".xlsx,.csv" class="block max-w-full" aria-label="选择Excel文件" :disabled="busy" @change="pick" /><div class="flex gap-2 flex-wrap"><UButton label="读取并预览" :disabled="!file || busy" :loading="busy" @click="preview" /><UButton :href="`/api/import/template?kind=${kind}`" label="下载空白模板" variant="outline" external /><UButton v-if="rows.length" :label="`导入勾选的 ${selected.length} 条`" :disabled="busy || !selected.length" @click="apply" /></div><p class="text-sm text-muted">支持 Excel、CSV 和原地图的 GridData.xlsx。已有的同名网格、同名同地址场所会跳过。</p></section>
  <UAlert v-if="error" :description="error" color="error" /><UAlert v-if="message" :description="message" color="success" />
  <section v-if="rows.length" class="panel"><div class="panel-head"><strong>{{ rows.length }} 条资料</strong><UButton label="勾选全部可导入" variant="ghost" :disabled="busy" @click="rows.forEach(x => x.selected = !x.duplicate && !rowError(x))" /></div>
    <div v-for="row in rows" :key="row.sourceKey" class="import-row space-y-3"><div class="flex justify-between items-center gap-2"><UCheckbox v-model="row.selected" :label="`第 ${row.row} 行`" :disabled="busy || row.duplicate" /><UBadge v-if="row.duplicate" label="已存在，将跳过" color="neutral" /><UButton v-if="row.shape" label="查看位置" variant="outline" size="sm" @click="selectedRow = row; mapOpen = true" /></div><div class="field-grid"><UFormField label="名称"><UInput v-model="row.name" class="w-full" :disabled="busy || row.duplicate" /></UFormField><template v-if="kind === 'point'"><UFormField label="网格"><USelect v-model="row.grid" :items="grids" class="w-full" :disabled="busy || row.duplicate" /></UFormField><UFormField label="场所形态"><USelect v-model="row.type" :items="types" class="w-full" :disabled="busy || row.duplicate" /></UFormField><UFormField label="地址"><UInput v-model="row.address" class="w-full" :disabled="busy || row.duplicate" /></UFormField></template></div><p v-if="rowError(row)" class="text-sm text-error">{{ rowError(row) }}</p></div>
  </section>
  <UModal v-model:open="mapOpen" title="核对导入位置" :ui="{content:'max-w-5xl'}"><template #body><ClientOnly><OfflineMap :features="mapFeatures" :layers="map.layers" :editable="false" height="60vh" /></ClientOnly></template></UModal>
</div></template>
<style scoped>.import-row{padding:16px;border-top:1px solid var(--border)}</style>
