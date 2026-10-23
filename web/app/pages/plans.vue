<script setup lang="ts">
import type { CommunityRecord } from '~/types/community'
import type { PointDto, PageDto, InspectionDto } from '~/types/api'
import { errorMessage, todayLocal } from '~/utils/format'
import { downloadWord } from '~/utils/wordDownload'
interface PlanData { inspector: string; from: string; to: string; typeId: string; pointIds: string[]; notes: string; handedOffTo?: string }
const plans = ref<CommunityRecord<PlanData>[]>([]), points = ref<PointDto[]>([]), types = ref<CommunityRecord[]>([]), inspections = ref<InspectionDto[]>([])
const error = ref(''), busy = ref(false), open = ref(false), name = ref(''), search = ref(''), selected = ref(''), showArchived = ref(false)
const editing = ref<CommunityRecord<PlanData> | null>(null), historyId = ref(''), historyOpen = ref(false)
const defaults = (): PlanData => ({ inspector: '', from: todayLocal(), to: todayLocal(), typeId: '', pointIds: [], notes: '' })
const form = reactive(defaults())
const staff = ref<string[]>([])
const staffChoices = computed(() => [...new Set([...staff.value, ...form.inspector.split(/\s+/).filter(Boolean)])])
const selectedStaff = computed({ get: () => form.inspector.split(/\s+/).filter(Boolean), set: (value: string[]) => { form.inspector = value.join(' ') } })
const typeOptions = computed(() => types.value.filter(x => !x.archived || x.id === form.typeId).map(x => ({ label: x.name, value: x.id })))
const choices = computed(() => points.value.filter(x => (!x.disabled || form.pointIds.includes(x.id)) && (!search.value || x.name.includes(search.value) || x.address.includes(search.value))))
const shown = computed(() => plans.value.filter(x => showArchived.value || !x.archived))
function completed(plan: CommunityRecord<PlanData>, id: string) { return inspections.value.some(x => x.pointId === id && x.result !== 'incomplete' && x.date >= plan.data.from && x.date <= plan.data.to && (!plan.data.typeId || x.inspectionTypeId === plan.data.typeId)) }
async function load() {
  error.value = ''
  try {
    const initial = await Promise.all([$fetch<CommunityRecord<PlanData>[]>('/api/records/plan', { query: { all: true } }), $fetch<CommunityRecord[]>('/api/records/inspection-type', { query: { all: true } }), $fetch<CommunityRecord[]>('/api/records/staff')]); plans.value = initial[0]; types.value = initial[1]; staff.value = initial[2].map(x => x.name)
    const allPoints: PointDto[] = []; for (let page = 1;; page++) { const r = await $fetch<PageDto<PointDto>>('/api/points', { query: { page, pageSize: 100, disabled: 'all' } }); allPoints.push(...r.items); if (page * r.pageSize >= r.total) break } points.value = allPoints
    const allInspections: InspectionDto[] = []; for (let page = 1;; page++) { const r = await $fetch<PageDto<InspectionDto>>('/api/inspections', { query: { page, pageSize: 100 } }); allInspections.push(...r.items); if (page * r.pageSize >= r.total) break } inspections.value = allInspections
  } catch (e) { error.value = errorMessage(e) }
}
function edit(plan?: CommunityRecord<PlanData>) { editing.value = plan ?? null; name.value = plan?.name ?? ''; Object.assign(form, plan ? structuredClone(toRaw(plan.data)) : defaults()); search.value = ''; open.value = true }
async function save() {
  if (!name.value.trim() || !form.inspector.trim() || !form.typeId || !form.pointIds.length) { error.value = '请填写名称、人员、检查类型并选择场所'; return }
  busy.value = true; error.value = ''
  try { await $fetch(editing.value ? `/api/records/plan/${editing.value.id}` : '/api/records/plan', { method: editing.value ? 'PUT' : 'POST', body: { name: name.value, parentId: '', data: form, revision: editing.value?.revision ?? 0, archived: editing.value?.archived ?? false }, retry: 0 }); open.value = false; await load() }
  catch (e) { error.value = errorMessage(e) } finally { busy.value = false }
}
async function toggle(plan: CommunityRecord<PlanData>) { busy.value = true; error.value = ''; try { await $fetch(`/api/records/plan/${plan.id}`, { method: 'PUT', body: { ...plan, archived: !plan.archived }, retry: 0 }); await load() } catch (e) { error.value = errorMessage(e) } finally { busy.value = false } }
async function download(plan: CommunityRecord<PlanData>, format: string) { busy.value = true; error.value = ''; try { await downloadWord(`/api/reports/plans/${format}?planId=${plan.id}`, `${plan.name}.${format}`) } catch (e) { error.value = errorMessage(e) } finally { busy.value = false } }
onMounted(load)
</script>
<template><div class="page-stack"><PageHeader title="检查安排" back="/"><UButton label="新增安排" icon="i-lucide-plus" @click="edit()" /></PageHeader><UAlert v-if="error" :description="error" color="error" /><UCheckbox v-model="showArchived" label="包括已取消" />
  <section v-for="plan in shown" :key="plan.id" class="panel"><button class="panel-body w-full text-left" @click="selected = selected === plan.id ? '' : plan.id"><div class="flex justify-between gap-2"><strong>{{ plan.name }}</strong><span>{{ plan.archived ? '已取消' : `已查 ${plan.data.pointIds.filter(id => completed(plan, id)).length} / ${plan.data.pointIds.length}` }}</span></div><p class="mt-2 text-muted text-sm">{{ plan.data.inspector }} · {{ plan.data.from }} 至 {{ plan.data.to }}</p><p v-if="!plan.archived && plan.data.to < todayLocal() && plan.data.pointIds.some(id => !completed(plan, id))" class="text-error text-sm mt-2">已超过完成期限</p></button>
    <div v-if="selected === plan.id"><div class="panel-body flex gap-2 flex-wrap"><UButton label="修改安排" variant="outline" :disabled="busy" @click="edit(plan)" /><UButton label="导出 Excel" variant="outline" :disabled="busy" @click="download(plan, 'xlsx')" /><UButton label="导出 Word" variant="outline" :disabled="busy" @click="download(plan, 'docx')" /><UButton v-if="!plan.data.handedOffTo" :label="plan.archived ? '恢复安排' : '取消安排'" color="neutral" variant="ghost" :disabled="busy" @click="toggle(plan)" /><UButton label="变更记录" color="neutral" variant="ghost" @click="historyId = plan.id; historyOpen = true" /></div><p v-if="plan.data.notes" class="px-4 pb-3 text-sm">{{ plan.data.notes }}</p>
      <div v-for="id in plan.data.pointIds" :key="id" class="list-row"><div class="row-body"><strong>{{ points.find(x => x.id === id)?.name ?? '已停用场所' }}</strong></div><UBadge v-if="completed(plan, id)" label="已检查" variant="subtle" /><UButton v-else-if="!plan.archived" label="登记检查" :to="{ path: '/inspections/new', query: { pointId: id, typeId: plan.data.typeId, inspector: plan.data.inspector } }" variant="outline" /></div>
    </div>
  </section><EmptyState v-if="!shown.length" title="暂无检查安排" />
  <USlideover v-model:open="open" :title="editing ? '修改检查安排' : '新增检查安排'" :dismissible="!busy" :ui="{ content: 'w-full max-w-[600px]' }"><template #body><div class="space-y-4"><UAlert v-if="error" :description="error" color="error" /><UFormField label="名称" required><UInput v-model="name" class="w-full" :disabled="busy" /></UFormField><div class="field-grid"><UFormField label="检查人员" required><USelectMenu v-if="staff.length" v-model="selectedStaff" multiple :items="staffChoices" class="w-full" :disabled="busy" /><NameTagsInput v-else v-model="form.inspector" class="w-full" :disabled="busy" /></UFormField><UFormField label="检查类型" required><USelect v-model="form.typeId" :items="typeOptions" class="w-full" :disabled="busy" /></UFormField><UFormField label="开始日期"><UInput v-model="form.from" type="date" class="w-full" :disabled="busy" /></UFormField><UFormField label="完成期限"><UInput v-model="form.to" type="date" class="w-full" :disabled="busy" /></UFormField></div><UFormField label="选择场所" required><UInput v-model="search" placeholder="搜索场所" class="w-full mb-3" /><UButton label="勾选搜索结果" variant="link" :disabled="busy" @click="form.pointIds = [...new Set([...form.pointIds, ...choices.filter(x => !x.disabled).map(x => x.id)])]" /><div class="max-h-80 overflow-auto space-y-3"><UCheckbox v-for="point in choices" :key="point.id" :label="point.name + (point.disabled ? '（已停用）' : '')" :disabled="busy" :model-value="form.pointIds.includes(point.id)" @update:model-value="v => { form.pointIds = v ? [...form.pointIds, point.id] : form.pointIds.filter(x => x !== point.id) }" /></div></UFormField><span class="text-sm text-muted">已选 {{ form.pointIds.length }} 处</span><UFormField label="备注"><UTextarea v-model="form.notes" class="w-full" :disabled="busy" /></UFormField></div></template><template #footer><UButton label="取消" variant="outline" color="neutral" :disabled="busy" @click="open = false" /><UButton label="保存安排" :loading="busy" @click="save" /></template></USlideover>
  <RecordHistory v-model:open="historyOpen" :record-id="historyId" />
</div></template>
