<script setup lang="ts">
import { mapColors, type MapRecord } from '~/utils/map'
const props = defineProps<{ records: MapRecord[]; choose?: boolean; loading?: boolean; error?: string }>()
const emit = defineEmits<{ choose: [id: string]; retry: [] }>()
const selected = ref<string[]>([]), unlocated = ref(false), map = ref<{ focus: (id: string) => void }>()
// Co-located records share one marker, but every record remains selectable in the panel.
const groups = computed(() => {
  const result = new Map<string, MapRecord[]>()
  for (const record of props.records) { if (!record.shape) continue; const key = JSON.stringify(record.shape); const group = result.get(key) ?? []; group.push(record); result.set(key, group) }
  return [...result.values()]
})
const markers = computed(() => groups.value.map(group => ({ ...group[0]!, title: group.length > 1 ? `${group[0]!.title} · ${group.length} 条` : group[0]!.title, color: group.some(r => r.color === mapColors.danger) ? mapColors.danger : group.some(r => r.color === mapColors.warning) ? mapColors.warning : group[0]!.color })))
const missing = computed(() => props.records.filter(record => !record.shape))
const shown = computed(() => unlocated.value ? missing.value : groups.value.filter(group => selected.value.includes(group[0]!.id)).flat())
watch(() => props.records, () => { selected.value = []; unlocated.value = false })
function select(ids: string[]) { if (props.loading || props.error) return; unlocated.value = false; selected.value = ids }

const fullscreen = ref(false), fullscreenError = ref('')
const ready = ref(false)
const background = useState<boolean>('main-map-expanded', () => false)
function syncFullscreen() {
  fullscreen.value = document.fullscreenElement === document.documentElement
}
async function toggleFullscreen() {
  fullscreenError.value = ''
  try {
    if (document.fullscreenElement === document.documentElement) {
      await document.exitFullscreen()
    } else if (!document.fullscreenElement && document.documentElement.requestFullscreen) {
      // This runs directly from the user's click to retain browser user activation.
      await document.documentElement.requestFullscreen()
    } else {
      fullscreenError.value = '当前浏览器不支持网页全屏'
    }
  } catch {
    fullscreenError.value = '无法切换浏览器全屏，请检查浏览器权限'
  }
  syncFullscreen()
}
onMounted(() => {
  // The map fills the page for the entire map-view lifetime. Browser fullscreen
  // changes the browser chrome only; never reparent or resize the map on click.
  ready.value = true
  background.value = true
  syncFullscreen()
  document.addEventListener('fullscreenchange', syncFullscreen)
})
onBeforeUnmount(() => {
  background.value = false
  document.removeEventListener('fullscreenchange', syncFullscreen)
  if (document.fullscreenElement === document.documentElement)
    void document.exitFullscreen().catch(() => {})
})
</script>
<template>
  <div class="record-map-shell" :aria-busy="loading">
    <Teleport to="#main-map-layer" :disabled="!ready"><div class="record-map-surface"><SimpleMap ref="map" :records="markers" :selected="selected" :show-zoom-controls="false" @select="select" /></div></Teleport>
    <UButton v-if="!choose" type="button" :icon="fullscreen ? 'i-lucide-minimize' : 'i-lucide-maximize'" :aria-label="fullscreen ? '退出浏览器全屏' : '浏览器全屏'" :title="fullscreen ? '退出浏览器全屏' : '浏览器全屏'" :aria-pressed="fullscreen" color="neutral" variant="ghost" class="map-fullscreen" data-testid="map-fullscreen" @click="toggleFullscreen" />
    <span v-if="fullscreenError" class="record-map-fullscreen-error" role="alert">{{ fullscreenError }}</span>
    <div class="record-map-summary" :class="{ 'with-fullscreen': !choose }"><span v-if="loading" role="status">加载中…</span><span>已标记 {{ records.length - missing.length }} 条</span><button v-if="missing.length" type="button" :aria-pressed="unlocated" @click="unlocated = !unlocated; selected = []">未标记 {{ missing.length }} 条 ›</button></div>
    <UAlert v-if="error" :description="error" color="error" class="record-map-error"><template #actions><UButton label="重试" @click="emit('retry')" /></template></UAlert>
    <section v-if="shown.length && !error" class="record-map-panel" aria-label="地图记录"><header><strong>{{ unlocated ? '未标记位置' : '此处记录' }} · {{ shown.length }}</strong><UButton type="button" icon="i-lucide-x" aria-label="收起地图记录" color="neutral" variant="ghost" @click="selected = []; unlocated = false" /></header><div class="record-map-items"><article v-for="record in shown" :key="record.id"><h3>{{ record.title }}</h3><p>{{ record.subtitle }}</p><div class="flex gap-2"><UButton v-if="choose" type="button" size="sm" label="选择点位" @click="emit('choose', record.id)" /><UButton v-else-if="record.href" :to="record.href" size="sm" label="查看详情" variant="outline" /><slot name="actions" :record="record" /></div></article></div></section>
  </div>
</template>
<style scoped>
.record-map-surface{position:absolute;inset:0;overflow:hidden;border-radius:0;pointer-events:auto}.map-fullscreen{position:absolute;top:12px;left:12px;z-index:2;width:38px;height:38px;background:var(--surface);box-shadow:0 2px 12px var(--border);pointer-events:auto}.record-map-summary.with-fullscreen{left:60px}.record-map-summary,.record-map-panel,.record-map-error{pointer-events:auto}.record-map-error{position:absolute;left:12px;top:62px;right:12px}.record-map-fullscreen-error{position:absolute;top:62px;left:12px;z-index:2;padding:6px 10px;border-radius:6px;background:var(--surface);color:var(--danger);pointer-events:auto}

.record-map-shell{position:relative;flex:1 0 280px;min-height:280px;border:0;overflow:visible;pointer-events:none}.record-map-summary{position:absolute;top:12px;left:12px;display:flex;gap:14px;align-items:center;padding:9px 14px;background:var(--surface);box-shadow:0 2px 12px var(--border);border-radius:8px;font-size:13px;color:var(--text-2)}.record-map-summary button{color:var(--warning);text-decoration:underline;cursor:pointer}.record-map-panel{position:absolute;left:12px;top:62px;max-height:calc(100% - 106px);width:310px;max-width:calc(100% - 72px);border-radius:10px;background:var(--surface);box-shadow:0 4px 20px var(--border-strong);display:flex;flex-direction:column}.record-map-panel header{flex-shrink:0;padding:9px 12px;display:flex;justify-content:space-between;align-items:center;border-bottom:1px solid var(--divider);color:var(--text);font-size:14px}.record-map-items{min-height:0;overflow-y:auto;overscroll-behavior:contain}.record-map-items article{padding:12px;border-bottom:1px solid var(--divider)}.record-map-items h3{font-weight:600;font-size:14px;color:var(--text);overflow-wrap:anywhere}.record-map-items p{font-size:12px;color:var(--text-3);margin:5px 0 9px;white-space:pre-line;overflow-wrap:anywhere}
@media(max-width:640px){.record-map-panel{top:auto;bottom:0;left:0;width:100%;max-width:100%;max-height:calc(100% - 64px);border-radius:12px 12px 0 0}.record-map-summary{font-size:12px;padding:8px 10px}.record-map-shell{min-height:280px}}
</style>
