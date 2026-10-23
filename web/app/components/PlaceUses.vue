<script setup lang="ts">
import type { CommunityRecord, UseData, UnitData } from '~/types/community'
import type { PhotoUploadDto } from '~/types/api'
import { errorMessage, todayLocal } from '~/utils/format'
const props = defineProps<{ pointId: string }>()
const { bootstrap } = useAppData()
const uses = ref<CommunityRecord<UseData>[]>([]), units = ref<CommunityRecord<UnitData>[]>([])
const error = ref(''), busy = ref(false), loading = ref(false), open = ref(false), unitOpen = ref(false), showEnded = ref(false)
const current = ref<CommunityRecord<UseData> | null>(null), currentUnit = ref<CommunityRecord<UnitData> | null>(null)
const name = ref(''), unitName = ref(''), historyId = ref(''), historyOpen = ref(false)
const photos = ref<PhotoUploadDto[]>([]), photosBusy = ref(false), photosFailed = ref(false), photosLoaded = ref(false)
const empty = (): UseData => ({ unitId: '', purposes: [], contact: '', phone: '', startDate: '', endDate: '', state: '使用中', notes: '', photoIds: [], business: '', owner: '', ownerPhone: '', tenantCount: '', storedGoods: '', area: '' })
const form = reactive<UseData>(empty())
const emptyUnit = (): UnitData => ({ parentUnitId: '', floor: '', area: '', notes: '' })
const unitForm = reactive<UnitData>(emptyUnit())
const purposes = ['商店', '仓库', '生产加工', '出租', '居住', '办公', '其他']
const locked = computed(() => busy.value || loading.value || photosBusy.value)
function unitLabel(id: string): string {
  const names: string[] = [], seen = new Set<string>()
  while (id && !seen.has(id)) { seen.add(id); const unit = units.value.find(x => x.id === id); if (!unit) break; names.unshift(unit.name); id = unit.data.parentUnitId ?? '' }
  return names.join(' / ') || '整个场所'
}
const unitOptions = computed(() => [{ label: '整个场所', value: '' }, ...units.value.filter(x => !x.archived || x.id === form.unitId).map(x => ({ label: unitLabel(x.id) + (x.archived ? '（已停用）' : ''), value: x.id }))])
const parentOptions = computed(() => [{ label: '直接属于场所', value: '' }, ...units.value.filter(x => !x.archived && x.id !== currentUnit.value?.id).map(x => ({ label: unitLabel(x.id), value: x.id }))])
const shown = computed(() => uses.value.filter(x => showEnded.value || (!x.archived && x.data.state !== '已结束')))
async function load() {
  try { const data = await Promise.all([$fetch<CommunityRecord<UseData>[]>('/api/records/use', { query: { parentId: props.pointId, all: true } }), $fetch<CommunityRecord<UnitData>[]>('/api/records/unit', { query: { parentId: props.pointId, all: true } })]); uses.value = data[0]; units.value = data[1] }
  catch (e) { error.value = errorMessage(e) }
}
async function edit(value?: CommunityRecord<UseData>, end = false) {
  current.value = value ?? null; name.value = value?.name ?? ''; Object.assign(form, empty(), value ? structuredClone(toRaw(value.data)) : {})
  if (end) { form.state = '已结束'; form.endDate = todayLocal() }
  photos.value = []; photosLoaded.value = false; open.value = true; loading.value = true; error.value = ''
  try { if (value) photos.value = await $fetch<PhotoUploadDto[]>(`/api/record-photos/${value.id}`); photosLoaded.value = true }
  catch (e) { error.value = errorMessage(e) }
  finally { loading.value = false }
}
async function save() {
  if (locked.value || photosFailed.value || !photosLoaded.value) return
  if (!name.value.trim() || !form.purposes.length) { error.value = '请填写名称并选择用途'; return }
  busy.value = true; error.value = ''
  try { await $fetch(current.value ? `/api/records/use/${current.value.id}` : '/api/records/use', { method: current.value ? 'PUT' : 'POST', body: { name: name.value, parentId: props.pointId, data: { ...form, photoIds: photos.value.map(x => x.id) }, revision: current.value?.revision ?? 0, archived: current.value?.archived ?? false }, retry: 0 }); open.value = false; await load() }
  catch (e) { error.value = errorMessage(e) } finally { busy.value = false }
}
function editUnit(unit?: CommunityRecord<UnitData>) { currentUnit.value = unit ?? null; unitName.value = unit?.name ?? ''; Object.assign(unitForm, emptyUnit(), unit ? structuredClone(toRaw(unit.data)) : {}); error.value = '' }
async function saveUnit() {
  if (!unitName.value.trim() || busy.value) return
  busy.value = true; error.value = ''
  try { await $fetch(currentUnit.value ? `/api/records/unit/${currentUnit.value.id}` : '/api/records/unit', { method: currentUnit.value ? 'PUT' : 'POST', body: { name: unitName.value, parentId: props.pointId, data: unitForm, revision: currentUnit.value?.revision ?? 0, archived: currentUnit.value?.archived ?? false }, retry: 0 }); editUnit(); await load() }
  catch (e) { error.value = errorMessage(e) } finally { busy.value = false }
}
async function toggleUnit(unit: CommunityRecord<UnitData>) {
  busy.value = true; error.value = ''
  try { await $fetch(`/api/records/unit/${unit.id}`, { method: 'PUT', body: { ...unit, archived: !unit.archived }, retry: 0 }); await load(); if (currentUnit.value?.id === unit.id) editUnit() }
  catch (e) { error.value = errorMessage(e) } finally { busy.value = false }
}
function history(id: string) { historyId.value = id; historyOpen.value = true }
watch(() => props.pointId, load, { immediate: true })
</script>
<template>
  <section class="panel">
    <div class="panel-head"><h2 class="section-title">经营和使用情况</h2><UButton label="添加使用情况" icon="i-lucide-plus" variant="outline" @click="edit()" /></div>
    <UAlert v-if="error && !open && !unitOpen" :description="error" color="error" />
    <div class="panel-body flex flex-wrap items-center gap-3"><UCheckbox v-model="showEnded" label="包括已结束" /><UButton label="管理楼层、房间" color="neutral" variant="link" @click="editUnit(); unitOpen = true" /></div>
    <div v-for="item in shown" :key="item.id" class="use-row">
      <div class="flex justify-between gap-3"><strong>{{ item.name }}</strong><UBadge :color="item.data.state === '已结束' ? 'neutral' : 'primary'" variant="subtle">{{ item.data.state }}</UBadge></div>
      <div class="flex gap-2 flex-wrap mt-2"><UBadge v-for="purpose in item.data.purposes" :key="purpose" variant="outline" color="neutral">{{ purpose }}</UBadge><span class="text-sm text-muted">{{ unitLabel(item.data.unitId) }}</span></div>
      <p class="text-sm mt-2">{{ item.data.contact }} {{ item.data.phone }}</p>
      <p v-if="item.data.startDate || item.data.endDate" class="text-xs text-muted">{{ item.data.startDate || '日期不详' }} 至 {{ item.data.endDate || '现在' }}</p>
      <div v-if="item.data.photoIds?.length" class="flex gap-2 mt-2 overflow-x-auto"><a v-for="photo in item.data.photoIds" :key="photo" :href="`/photos/${photo}`" target="_blank" aria-label="查看照片"><img :src="`/photos/${photo}?size=thumb`" class="size-16 rounded object-cover" loading="lazy" alt="使用情况照片" /></a></div>
      <div class="flex gap-2 flex-wrap mt-3"><UButton label="修改" color="neutral" variant="outline" @click="edit(item)" /><UButton v-if="item.data.state !== '已结束'" label="登记检查" :to="`/inspections/new?pointId=${pointId}&useId=${item.id}`" variant="outline" /><UButton v-if="item.data.state !== '已结束'" label="停业 / 退租" color="neutral" variant="ghost" @click="edit(item, true)" /><UButton label="变更记录" color="neutral" variant="ghost" @click="history(item.id)" /></div>
    </div>
    <EmptyState v-if="!shown.length" title="还没有登记商户、住户或其他使用情况" />
    <USlideover v-model:open="open" :title="current ? '修改使用情况' : '添加使用情况'" :dismissible="!locked" :close="!locked" :ui="{ content: 'w-full max-w-[560px]' }">
      <template #body><div class="space-y-4"><UAlert v-if="error" :description="error" color="error" />
        <UFormField label="店名 / 单位 / 使用名称" required><UInput v-model="name" class="w-full" :disabled="locked" /></UFormField>
        <UFormField label="所在楼层、房间"><USelect v-model="form.unitId" :items="unitOptions" class="w-full" :disabled="locked" /></UFormField>
        <UFormField label="用途（可多选）" required><div class="grid grid-cols-3 gap-3"><UCheckbox v-for="p in purposes" :key="p" :label="p" :disabled="locked" :model-value="form.purposes.includes(p)" @update:model-value="value => { form.purposes = value ? [...form.purposes, p] : form.purposes.filter(x => x !== p) }" /></div></UFormField>
        <div class="field-grid"><UFormField label="联系人"><UInput v-model="form.contact" class="w-full" :disabled="locked" /></UFormField><UFormField label="联系电话"><UInput v-model="form.phone" type="tel" class="w-full" :disabled="locked" /></UFormField><UFormField label="开始日期"><UInput v-model="form.startDate" type="date" class="w-full" :disabled="locked" /></UFormField><UFormField label="面积（㎡）"><UInput v-model="form.area" type="number" min="0" class="w-full" :disabled="locked" /></UFormField></div>
        <UFormField v-if="form.purposes.includes('商店') || form.purposes.includes('生产加工')" label="经营内容"><UInput v-model="form.business" class="w-full" :disabled="locked" /></UFormField>
        <UFormField v-if="form.purposes.includes('仓库')" label="存放物品"><UInput v-model="form.storedGoods" class="w-full" :disabled="locked" /></UFormField>
        <div v-if="form.purposes.includes('出租') || form.purposes.includes('居住')" class="field-grid"><UFormField label="房东"><UInput v-model="form.owner" class="w-full" :disabled="locked" /></UFormField><UFormField label="房东电话"><UInput v-model="form.ownerPhone" type="tel" class="w-full" :disabled="locked" /></UFormField><UFormField label="居住人数"><UInput v-model="form.tenantCount" type="number" min="0" class="w-full" :disabled="locked" /></UFormField></div>
        <div class="field-grid"><UFormField label="状态"><USelect v-model="form.state" :items="['使用中','暂停使用','已结束','待核实']" class="w-full" :disabled="locked" @update:model-value="v => { form.endDate = v === '已结束' ? (form.endDate || todayLocal()) : '' }" /></UFormField><UFormField v-if="form.state === '已结束'" label="结束日期" required><UInput v-model="form.endDate" type="date" class="w-full" :disabled="locked" /></UFormField></div>
        <UFormField label="照片"><PhotoUploader v-model="photos" phase="point" :max="20" deferred-removal :disabled="busy || loading" :quality="bootstrap?.settings.jpegQuality" @busy="photosBusy = $event" @failed="photosFailed = $event" /></UFormField>
        <UFormField label="备注"><UTextarea v-model="form.notes" class="w-full" :rows="3" :disabled="locked" /></UFormField>
      </div></template>
      <template #footer><UButton label="取消" variant="outline" color="neutral" :disabled="locked" @click="open = false" /><UButton label="保存" :loading="busy" :disabled="locked || photosFailed || !photosLoaded" @click="save" /></template>
    </USlideover>
    <USlideover v-model:open="unitOpen" title="楼层、房间和分区" :dismissible="!busy" :ui="{ content: 'w-full max-w-[560px]' }"><template #body><div class="space-y-4">
      <UAlert v-if="error" :description="error" color="error" />
      <div v-for="unit in units" :key="unit.id" class="border-b border-default pb-3"><div class="flex justify-between gap-2"><strong>{{ unitLabel(unit.id) }}</strong><UBadge v-if="unit.archived" label="已停用" color="neutral" /></div><p class="text-sm text-muted">{{ unit.data.floor }} {{ unit.data.area ? `${unit.data.area}㎡` : '' }}</p><div class="flex gap-2 mt-2"><UButton label="修改" size="sm" variant="outline" color="neutral" :disabled="busy" @click="editUnit(unit)" /><UButton :label="unit.archived ? '恢复使用' : '停用'" size="sm" variant="ghost" color="neutral" :disabled="busy" @click="toggleUnit(unit)" /><UButton label="变更记录" size="sm" variant="ghost" color="neutral" @click="history(unit.id)" /></div></div>
      <div class="flex justify-between items-center"><h3>{{ currentUnit ? '修改楼层 / 房间' : '添加楼层 / 房间' }}</h3><UButton v-if="currentUnit" label="改为新增" variant="link" @click="editUnit()" /></div>
      <UFormField label="名称" required><UInput v-model="unitName" placeholder="例如：一楼东侧" class="w-full" :disabled="busy" /></UFormField>
      <UFormField label="上级楼层或房间"><USelect v-model="unitForm.parentUnitId" :items="parentOptions" class="w-full" :disabled="busy" /></UFormField>
      <div class="field-grid"><UFormField label="楼层"><UInput v-model="unitForm.floor" class="w-full" :disabled="busy" /></UFormField><UFormField label="面积（㎡）"><UInput v-model="unitForm.area" type="number" min="0" class="w-full" :disabled="busy" /></UFormField></div>
      <UFormField label="备注"><UTextarea v-model="unitForm.notes" class="w-full" :disabled="busy" /></UFormField>
      <UButton :label="currentUnit ? '保存修改' : '添加楼层 / 房间'" :loading="busy" @click="saveUnit" />
    </div></template></USlideover>
    <RecordHistory v-model:open="historyOpen" :record-id="historyId" />
  </section>
</template>
<style scoped>.use-row{padding:16px;border-top:1px solid var(--border)}</style>
