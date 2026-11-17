<script setup lang="ts">
import type { CommunityRecord } from '~/types/community'
import type { PointDto, PageDto, InspectionDto } from '~/types/api'
import { errorMessage, todayLocal } from '~/utils/format'
import { downloadWord } from '~/utils/wordDownload'
import { gridDescendants } from '~/utils/gridTree'
interface PlanData { inspector: string; from: string; to: string; typeId: string; pointIds: string[]; notes: string; handedOffTo?: string }
const route=useRoute()
const {bootstrap,load:loadApp}=useAppData()
const grid=ref('all'),onlySelected=ref(false),loading=ref(true),initialSnapshot=ref('')
const plans = ref<CommunityRecord<PlanData>[]>([]), points = ref<PointDto[]>([]), types = ref<CommunityRecord[]>([]), inspections = ref<InspectionDto[]>([])
const error = ref(''), busy = ref(false), open = ref(false), name = ref(''), search = ref(''), selected = ref(''), showArchived = ref(false)
const editing = ref<CommunityRecord<PlanData> | null>(null), historyId = ref(''), historyOpen = ref(false)
const defaults = (): PlanData => ({ inspector: '', from: todayLocal(), to: todayLocal(), typeId: '', pointIds: [], notes: '', handedOffTo: '' })
const form = reactive(defaults())
const staff = ref<string[]>([])
const staffChoices = computed(() => [...new Set([...staff.value, ...form.inspector.split(/\s+/).filter(Boolean)])])
const selectedStaff = computed({ get: () => form.inspector.split(/\s+/).filter(Boolean), set: (value: string[]) => { form.inspector = value.join(' ') } })
const typeOptions = computed(() => types.value.filter(x => !x.archived || x.id === form.typeId).map(x => ({ label: x.name, value: x.id })))
const gridIds=computed(()=>grid.value==='all'?null:gridDescendants(bootstrap.value?.communities??[],grid.value))
const choices=computed(()=>points.value.filter(x=>(!x.disabled||form.pointIds.includes(x.id))&&(!gridIds.value||gridIds.value.has(x.communityId))&&(!onlySelected.value||form.pointIds.includes(x.id))&&(!search.value.trim()||[x.name,x.address,x.communityName].some(value=>value.includes(search.value.trim())))))
function snapshot(){return JSON.stringify({name:name.value,form})}
function changeOpen(value:boolean){if(value){open.value=true;return}if(busy.value)return;if(snapshot()!==initialSnapshot.value&&!window.confirm('检查安排尚未保存，放弃修改吗？'))return;open.value=false}
onBeforeRouteLeave(()=>{if(!open.value)return true;changeOpen(false);return !open.value})
const shown = computed(() => plans.value.filter(x => showArchived.value || !x.archived))
function completed(plan: CommunityRecord<PlanData>, id: string) { return inspections.value.some(x => x.pointId === id && x.result !== 'incomplete' && x.date >= plan.data.from && x.date <= plan.data.to && (!plan.data.typeId || x.inspectionTypeId === plan.data.typeId)) }
async function load() {
  error.value = '';loading.value=true
  try {
    await loadApp()
    const initial = await Promise.all([$fetch<CommunityRecord<PlanData>[]>('/api/records/plan', { query: { all: true } }), $fetch<CommunityRecord[]>('/api/records/inspection-type', { query: { all: true } }), $fetch<CommunityRecord[]>('/api/records/staff')]); plans.value = initial[0]; types.value = initial[1]; staff.value = initial[2].map(x => x.name)
    const allPoints: PointDto[] = []; for (let page = 1;; page++) { const r = await $fetch<PageDto<PointDto>>('/api/points', { query: { page, pageSize: 100, disabled: 'all' } }); allPoints.push(...r.items); if (page * r.pageSize >= r.total) break } points.value = allPoints
    const allInspections: InspectionDto[] = []; for (let page = 1;; page++) { const r = await $fetch<PageDto<InspectionDto>>('/api/inspections', { query: { page, pageSize: 100 } }); allInspections.push(...r.items); if (page * r.pageSize >= r.total) break } inspections.value = allInspections
  } catch (e) { error.value = errorMessage(e) }finally{loading.value=false}
}
function edit(plan?: CommunityRecord<PlanData>) { editing.value = plan ?? null; name.value = plan?.name ?? ''; Object.assign(form, defaults(), plan ? structuredClone(toRaw(plan.data)) : {}); search.value = '';grid.value='all';onlySelected.value=false;error.value='';initialSnapshot.value=snapshot();open.value = true }
async function save() {
  if (!name.value.trim() || !form.inspector.trim() || !form.typeId || !form.pointIds.length) { error.value = '请填写名称、人员、检查类型并选择场所'; return }
  if(!form.from||!form.to||form.from>form.to){error.value='请填写正确的开始日期和完成期限';return}
  busy.value = true; error.value = ''
  try { await $fetch(editing.value ? `/api/records/plan/${editing.value.id}` : '/api/records/plan', { method: editing.value ? 'PUT' : 'POST', body: { name: name.value, parentId: '', data: form, revision: editing.value?.revision ?? 0, archived: editing.value?.archived ?? false }, retry: 0 }); open.value = false; await load() }
  catch (e) { error.value = errorMessage(e) } finally { busy.value = false }
}
async function toggle(plan: CommunityRecord<PlanData>) { busy.value = true; error.value = ''; try { await $fetch(`/api/records/plan/${plan.id}`, { method: 'PUT', body: { ...plan, archived: !plan.archived }, retry: 0 }); await load() } catch (e) { error.value = errorMessage(e) } finally { busy.value = false } }
async function download(plan: CommunityRecord<PlanData>, format: string) { busy.value = true; error.value = ''; try { await downloadWord(`/api/reports/plans/${format}?planId=${plan.id}`, `${plan.name}.${format}`) } catch (e) { error.value = errorMessage(e) } finally { busy.value = false } }
onMounted(async()=>{await load();if(typeof route.query.plan==='string')selected.value=route.query.plan})
</script>
<template><div class="page-stack"><PageHeader title="检查安排" back="/"><UButton label="新增安排" :disabled="loading" icon="i-lucide-plus" @click="edit()" /></PageHeader><UAlert v-if="error" :description="error" color="error" /><UCheckbox v-if="plans.some(x=>x.archived)" v-model="showArchived" label="包括已取消" />
  <section v-for="plan in shown" :key="plan.id" class="panel"><button class="panel-body w-full text-left" @click="selected = selected === plan.id ? '' : plan.id"><div class="flex justify-between gap-2"><strong>{{ plan.name }}</strong><span>{{ plan.archived ? '已取消' : `已查 ${plan.data.pointIds.filter(id => completed(plan, id)).length} / ${plan.data.pointIds.length}` }}</span></div><p class="mt-2 text-muted text-sm">{{ plan.data.inspector }} · {{ plan.data.from }} 至 {{ plan.data.to }}</p><p v-if="!plan.archived && plan.data.to < todayLocal() && plan.data.pointIds.some(id => !completed(plan, id))" class="text-error text-sm mt-2">已超过完成期限</p></button>
    <div v-if="selected === plan.id"><div class="panel-body flex gap-2 flex-wrap"><UButton label="修改安排" variant="outline" :disabled="busy" @click="edit(plan)" /><UButton label="导出 Excel" variant="outline" :disabled="busy" @click="download(plan, 'xlsx')" /><UButton label="导出 Word" variant="outline" :disabled="busy" @click="download(plan, 'docx')" /><UButton v-if="!plan.data.handedOffTo" :label="plan.archived ? '恢复安排' : '取消安排'" color="neutral" variant="ghost" :disabled="busy" @click="toggle(plan)" /><UButton label="变更记录" color="neutral" variant="ghost" @click="historyId = plan.id; historyOpen = true" /></div><p v-if="plan.data.notes" class="px-4 pb-3 text-sm">{{ plan.data.notes }}</p>
      <div v-for="id in plan.data.pointIds" :key="id" class="list-row"><div class="row-body"><strong>{{ points.find(x => x.id === id)?.name ?? '已停用场所' }}</strong></div><UBadge v-if="completed(plan, id)" label="已检查" variant="subtle" /><UButton v-else-if="!plan.archived" label="登记检查" :to="{ path: '/inspections/new', query: { pointId: id, typeId: plan.data.typeId, inspector: plan.data.inspector,returnTo:`/plans?plan=${plan.id}` } }" variant="outline" /></div>
    </div>
  </section><p v-if="loading" class="text-muted">正在读取检查安排…</p><EmptyState v-else-if="!shown.length&&!error" title="暂无检查安排" />
  <USlideover :open="open" @update:open="changeOpen" :close="!busy" :title="editing ? '修改检查安排' : '新增检查安排'" :dismissible="!busy" :ui="{ content: 'w-full max-w-[600px]' }"><template #body><div class="space-y-4"><UAlert v-if="error" :description="error" color="error" /><UFormField label="名称" required><UInput v-model="name" class="w-full" :disabled="busy" /></UFormField><div class="field-grid"><UFormField label="检查人员" required><USelectMenu v-if="staff.length" v-model="selectedStaff" multiple :items="staffChoices" create-item class="w-full" :disabled="busy" @create="value=>{selectedStaff=[...new Set([...selectedStaff,value.trim()])].filter(Boolean)}"><template #create-item-label="{item}">使用姓名：{{item}}</template></USelectMenu><NameTagsInput v-else v-model="form.inspector" class="w-full" :disabled="busy" /></UFormField><UFormField label="检查类型" required><USelect v-model="form.typeId" :items="typeOptions" class="w-full" :disabled="busy" /></UFormField><UFormField label="开始日期" required><UInput v-model="form.from" type="date" :max="form.to||undefined" class="w-full" :disabled="busy" /></UFormField><UFormField label="完成期限" required><UInput v-model="form.to" type="date" :min="form.from||undefined" class="w-full" :disabled="busy" /></UFormField></div><fieldset class="min-w-0"><legend class="mb-2 text-sm font-medium">选择场所 <span class="text-error">*</span></legend><div class="grid grid-cols-1 sm:grid-cols-[160px_minmax(0,1fr)] gap-2 mb-3"><div class="min-w-0"><GridTreeSelect v-model="grid" allow-all :disabled="busy" /></div><UInput v-model="search" placeholder="搜索场所、地址" aria-label="搜索安排场所" class="min-w-0 flex-1" :disabled="busy" /></div><div class="flex gap-3 items-center mb-2"><UCheckbox v-model="onlySelected" label="只看已选" :disabled="busy" /><UButton v-if="form.pointIds.length" label="取消全部勾选" color="neutral" variant="link" :disabled="busy" @click="form.pointIds=[];onlySelected=false" /></div><UButton :label="`勾选当前结果（${choices.filter(x=>!x.disabled).length}）`" variant="link" :disabled="busy" @click="form.pointIds = [...new Set([...form.pointIds, ...choices.filter(x => !x.disabled).map(x => x.id)])]" /><div class="max-h-80 overflow-auto space-y-3"><p v-if="!choices.length" class="text-sm text-muted py-3">{{onlySelected?'当前条件下没有已选场所':'没有匹配的场所'}}</p><UCheckbox v-for="point in choices" :key="point.id" :label="point.name + ' · ' + point.communityName + (point.disabled ? '（已停用）' : '')" :disabled="busy" :model-value="form.pointIds.includes(point.id)" @update:model-value="v => { form.pointIds = v ? [...form.pointIds, point.id] : form.pointIds.filter(x => x !== point.id) }" /></div></fieldset><span class="text-sm text-muted">已选 {{ form.pointIds.length }} 处</span><UFormField label="备注"><UTextarea v-model="form.notes" class="w-full" :disabled="busy" /></UFormField></div></template><template #footer><UButton label="取消" variant="outline" color="neutral" :disabled="busy" @click="changeOpen(false)" /><UButton label="保存安排" :loading="busy" @click="save" /></template></USlideover>
  <RecordHistory v-model:open="historyOpen" :record-id="historyId" />
</div></template>
