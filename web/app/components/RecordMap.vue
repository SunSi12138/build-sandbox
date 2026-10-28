<script setup lang="ts">
import { mapColors, type MapRecord } from '~/utils/map'
const props = defineProps<{ records: MapRecord[]; choose?: boolean }>()
const emit = defineEmits<{ choose: [id: string] }>()
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
function select(ids: string[]) { unlocated.value = false; selected.value = ids }
</script>
<template>
  <div class="record-map-shell">
    <SimpleMap ref="map" :records="markers" :selected="selected" @select="select" />
    <div class="record-map-summary"><span>已标记 {{ records.length - missing.length }} 条</span><button v-if="missing.length" type="button" :aria-pressed="unlocated" @click="unlocated = !unlocated; selected = []">未标记 {{ missing.length }} 条 ›</button></div>
    <div v-if="!records.length" class="record-map-empty">暂无匹配记录</div>
    <section v-if="shown.length" class="record-map-panel" aria-label="地图记录"><header><strong>{{ unlocated ? '未标记位置' : '此处记录' }} · {{ shown.length }}</strong><UButton type="button" icon="i-lucide-x" aria-label="收起地图记录" color="neutral" variant="ghost" @click="selected = []; unlocated = false" /></header><div class="record-map-items"><article v-for="record in shown" :key="record.id"><h3>{{ record.title }}</h3><p>{{ record.subtitle }}</p><div class="flex gap-2"><UButton v-if="choose" type="button" size="sm" label="选择点位" @click="emit('choose', record.id)" /><UButton v-else-if="record.href" :to="record.href" size="sm" label="查看详情" variant="outline" /><slot name="actions" :record="record" /></div></article></div></section>
  </div>
</template>
<style scoped>
.record-map-shell{height:clamp(400px,62dvh,720px);position:relative;border:1px solid var(--divider);border-radius:12px;overflow:hidden}.record-map-summary{position:absolute;top:12px;left:12px;display:flex;gap:14px;align-items:center;padding:9px 14px;background:var(--surface);box-shadow:0 2px 12px var(--border);border-radius:8px;font-size:13px;color:var(--text-2)}.record-map-summary button{color:var(--warning);text-decoration:underline;cursor:pointer}.record-map-panel{position:absolute;left:12px;top:62px;bottom:44px;width:310px;max-width:calc(100% - 72px);border-radius:10px;background:white;box-shadow:0 4px 20px var(--border-strong);display:flex;flex-direction:column}.record-map-panel header{padding:9px 12px;display:flex;justify-content:space-between;align-items:center;border-bottom:1px solid var(--divider);color:var(--text);font-size:14px}.record-map-items{overflow-y:auto;overscroll-behavior:contain}.record-map-items article{padding:12px;border-bottom:1px solid var(--divider)}.record-map-items h3{font-weight:600;font-size:14px;color:var(--text);overflow-wrap:anywhere}.record-map-items p{font-size:12px;color:var(--text-3);margin:5px 0 9px;white-space:pre-line;overflow-wrap:anywhere}.record-map-empty{position:absolute;top:50%;left:50%;transform:translate(-50%,-50%);background:var(--surface);padding:16px;border-radius:8px;font-size:14px;pointer-events:none}
@media(max-width:640px){.record-map-panel{top:auto;bottom:0;left:0;width:100%;max-width:100%;max-height:48%;border-radius:12px 12px 0 0}.record-map-summary{font-size:12px;padding:8px 10px}.record-map-shell{height:60dvh;min-height:380px}}
</style>
