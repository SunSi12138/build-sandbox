<script setup lang="ts">
import type { CommunityRecord, UseData, UnitData } from '~/types/community'
import { errorMessage, todayLocal } from '~/utils/format'
const props = defineProps<{ pointId: string }>()
const uses = ref<CommunityRecord<UseData>[]>([]), units = ref<CommunityRecord<UnitData>[]>([])
const error = ref(''), busy = ref(false), open = ref(false), unitOpen = ref(false), showEnded = ref(false)
const current = ref<CommunityRecord<UseData> | null>(null)
const name = ref(''), unitName = ref(''), unitFloor = ref(''), unitArea = ref('')
const empty = (): UseData => ({ unitId: '', purposes: [], contact: '', phone: '', startDate: '', endDate: '', state: '使用中', notes: '', photoIds: [] })
const form = reactive<UseData>(empty())
const purposes = ['商店','仓库','生产加工','出租','居住','办公','其他']
const unitOptions = computed(() => [{ label: '整个场所', value: '' }, ...units.value.map(x => ({ label: x.name, value: x.id }))])
const shown = computed(() => uses.value.filter(x => showEnded.value || (!x.archived && x.data.state !== '已结束')))
async function load() { try { const data = await Promise.all([$fetch<CommunityRecord<UseData>[]>('/api/records/use', { query: { parentId: props.pointId, all: true } }), $fetch<CommunityRecord<UnitData>[]>('/api/records/unit', { query: { parentId: props.pointId } })]); uses.value = data[0]; units.value = data[1] } catch(e) { error.value = errorMessage(e) } }
function edit(value?: CommunityRecord<UseData>) { current.value = value ?? null; name.value = value?.name ?? ''; Object.assign(form, value ? structuredClone(toRaw(value.data)) : empty()); open.value = true; error.value = '' }
async function save() {
  if (!name.value.trim() || !form.purposes.length) { error.value = '请填写名称并选择用途'; return }
  busy.value = true; error.value = ''
  try { await $fetch(current.value ? `/api/records/use/${current.value.id}` : '/api/records/use', { method: current.value ? 'PUT' : 'POST', body: { name: name.value, parentId: props.pointId, data: form, revision: current.value?.revision ?? 0 }, retry: 0 }); open.value = false; await load() } catch(e) { error.value = errorMessage(e) } finally { busy.value = false }
}
async function saveUnit() {
  if (!unitName.value.trim()) return
  busy.value = true; error.value = ''
  try { await $fetch('/api/records/unit', { method: 'POST', body: { name: unitName.value, parentId: props.pointId, data: { floor: unitFloor.value, area: unitArea.value, notes: '' } }, retry: 0 }); unitOpen.value = false; await load() } catch(e) { error.value = errorMessage(e) } finally { busy.value = false }
}
watch(() => props.pointId, load, { immediate: true })
</script>
<template>
  <section class="panel"><div class="panel-head"><h2 class="section-title">经营和使用情况</h2><UButton label="添加用途" icon="i-lucide-plus" variant="outline" @click="edit()" /></div>
    <UAlert v-if="error && !open && !unitOpen" :description="error" color="error" />
    <div class="panel-body flex flex-wrap items-center gap-3"><UCheckbox v-model="showEnded" label="包括已结束" /><UButton label="管理楼层、房间" color="neutral" variant="link" @click="unitName = ''; unitFloor = ''; unitArea = ''; unitOpen = true" /><span class="text-sm text-muted">{{ units.length }}个内部单元</span></div>
    <div v-for="item in shown" :key="item.id" class="use-row"><div class="flex justify-between gap-3"><strong>{{ item.name }}</strong><UBadge :color="item.data.state === '已结束' ? 'neutral' : 'primary'" variant="subtle">{{ item.data.state }}</UBadge></div><div class="flex gap-2 flex-wrap mt-2"><UBadge v-for="purpose in item.data.purposes" :key="purpose" variant="outline" color="neutral">{{ purpose }}</UBadge><span class="text-sm text-muted">{{ units.find(x => x.id === item.data.unitId)?.name ?? '整个场所' }}</span></div><p class="text-sm mt-2">{{ item.data.contact }} {{ item.data.phone }}</p><p v-if="item.data.startDate || item.data.endDate" class="text-xs text-muted">{{ item.data.startDate || '日期不详' }} 至 {{ item.data.endDate || '现在' }}</p><div class="flex gap-2 mt-3"><UButton label="修改" color="neutral" variant="outline" @click="edit(item)" /><UButton v-if="item.data.state !== '已结束'" label="登记检查" :to="`/inspections/new?pointId=${pointId}&useId=${item.id}`" variant="outline" /><UButton v-if="item.data.state !== '已结束'" label="停业 / 退租" color="neutral" variant="ghost" @click="edit(item); form.state = '已结束'; form.endDate = todayLocal()" /></div></div>
    <EmptyState v-if="!shown.length" title="暂无使用记录" />
    <USlideover v-model:open="open" :title="current ? '修改使用情况' : '添加使用情况'" :dismissible="!busy" :ui="{ content: 'w-full max-w-[560px]' }"><template #body><div class="space-y-4"><UAlert v-if="error" :description="error" color="error" /><UFormField label="店名 / 单位 / 使用名称" required><UInput v-model="name" class="w-full" /></UFormField><UFormField label="所在楼层、房间"><USelect v-model="form.unitId" :items="unitOptions" class="w-full" /></UFormField><UFormField label="用途" required><div class="grid grid-cols-3 gap-3"><UCheckbox v-for="p in purposes" :key="p" :label="p" :model-value="form.purposes.includes(p)" @update:model-value="value => { form.purposes = value ? [...form.purposes, p] : form.purposes.filter(x => x !== p) }" /></div></UFormField><div class="field-grid"><UFormField label="联系人"><UInput v-model="form.contact" class="w-full" /></UFormField><UFormField label="联系电话"><UInput v-model="form.phone" type="tel" class="w-full" /></UFormField><UFormField label="开始日期"><UInput v-model="form.startDate" type="date" class="w-full" /></UFormField><UFormField label="结束日期"><UInput v-model="form.endDate" type="date" class="w-full" /></UFormField></div><UFormField label="状态"><USelect v-model="form.state" :items="['使用中','暂停使用','已结束','待核实']" /></UFormField><UFormField label="备注"><UTextarea v-model="form.notes" class="w-full" :rows="3" /></UFormField></div></template><template #footer><UButton label="取消" variant="outline" color="neutral" :disabled="busy" @click="open = false" /><UButton label="保存" :loading="busy" @click="save" /></template></USlideover>
    <UModal v-model:open="unitOpen" title="楼层、房间和分区"><template #body><div class="space-y-3"><div v-for="unit in units" :key="unit.id" class="flex justify-between border-b border-default pb-2"><strong>{{ unit.name }}</strong><span>{{ unit.data.floor }} {{ unit.data.area ? `${unit.data.area}㎡` : '' }}</span></div><UAlert v-if="error" :description="error" color="error" /><UFormField label="新增单元名称"><UInput v-model="unitName" placeholder="例如：一楼东侧" class="w-full" /></UFormField><div class="field-grid"><UFormField label="楼层"><UInput v-model="unitFloor" /></UFormField><UFormField label="面积（㎡）"><UInput v-model="unitArea" type="number" min="0" /></UFormField></div><UButton label="添加" :loading="busy" @click="saveUnit" /></div></template></UModal>
  </section>
</template>
<style scoped>.use-row{padding:16px;border-top:1px solid var(--border)}</style>
