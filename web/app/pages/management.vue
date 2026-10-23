<script setup lang="ts">
import type { CommunityRecord } from '~/types/community'
import type { PageDto, PointDto } from '~/types/api'
import { errorMessage } from '~/utils/format'
interface MergePreview { source: PointDto; target: PointDto; inspections: number; uses: number; units: number; photos: number; campaigns: number; plans: number }
interface HandoffPreview { fromId: string; fromName: string; toId: string; toName: string; version: string; plans: { id: string; name: string; pendingPoints: string[] }[]; issues: { id: string; name: string }[] }
const mode = ref('merge'), sourceId = ref(''), targetId = ref(''), fromId = ref(''), toId = ref(''), reason = ref(''), disablePrevious = ref(false)
const points = ref<PointDto[]>([]), staff = ref<CommunityRecord[]>([]), merge = ref<MergePreview | null>(null), handoff = ref<HandoffPreview | null>(null)
const busy = ref(false), error = ref(''), message = ref(''), confirm = ref(false)
const pointChoices = computed(() => points.value.map(x => ({ label: `${x.name} · ${x.address}${x.disabled ? '（已停用）' : ''}`, value: x.id })))
const staffChoices = computed(() => staff.value.map(x => ({ label: x.name + (x.archived ? '（已停用）' : ''), value: x.id })))
const activeStaff = computed(() => staff.value.filter(x => !x.archived).map(x => ({ label: x.name, value: x.id })))
watch([mode, sourceId, targetId, fromId, toId], () => { merge.value = null; handoff.value = null; confirm.value = false; error.value = ''; message.value = '' })
async function load() {
  try { staff.value = await $fetch('/api/records/staff', { query: { all: true } }); const data: PointDto[] = []; for (let page = 1;; page++) { const result = await $fetch<PageDto<PointDto>>('/api/points', { query: { page, pageSize: 100, disabled: 'all', temporary: 'all' } }); data.push(...result.items); if (page * result.pageSize >= result.total) break } points.value = data }
  catch (e) { error.value = errorMessage(e) }
}
async function preview() {
  busy.value = true; error.value = ''; message.value = ''; merge.value = null; handoff.value = null; confirm.value = false
  try { if (mode.value === 'merge') merge.value = await $fetch('/api/management/merge', { query: { sourceId: sourceId.value, targetId: targetId.value } }); else handoff.value = await $fetch('/api/management/handoff', { query: { fromId: fromId.value, toId: toId.value } }) }
  catch (e) { error.value = errorMessage(e) } finally { busy.value = false }
}
async function apply() {
  if (!reason.value.trim()) { error.value = '请填写办理原因'; return }
  busy.value = true; error.value = ''
  try {
    if (mode.value === 'merge' && merge.value) {
      const result = await $fetch<PointDto>('/api/management/merge', { method: 'POST', body: { sourceId: sourceId.value, targetId: targetId.value, sourceRevision: merge.value.source.revision, targetRevision: merge.value.target.revision, reason: reason.value }, retry: 0 }); await navigateTo(`/points/${result.id}`)
    } else if (handoff.value) {
      const result = await $fetch<{ plans: number; issues: number }>('/api/management/handoff', { method: 'POST', body: { fromId: fromId.value, toId: toId.value, version: handoff.value.version, notes: reason.value, disablePrevious: disablePrevious.value }, retry: 0 }); message.value = `已交接 ${result.plans} 项检查安排、${result.issues} 条问题`; handoff.value = null; confirm.value = false; reason.value = ''; await load()
    }
  } catch (e) { error.value = errorMessage(e) } finally { busy.value = false }
}
onMounted(load)
</script>
<template><div class="page-stack"><PageHeader title="资料管理" back="/settings" /><div class="flex gap-2"><UButton label="合并重复场所" :variant="mode === 'merge' ? 'solid' : 'outline'" @click="mode = 'merge'" /><UButton label="人员工作交接" :variant="mode === 'handoff' ? 'solid' : 'outline'" @click="mode = 'handoff'" /></div>
  <section class="panel panel-body space-y-4"><div v-if="mode === 'merge'" class="field-grid"><UFormField label="重复登记的场所"><USelectMenu v-model="sourceId" :items="pointChoices" value-key="value" placeholder="选择要并走的场所" class="w-full" :disabled="busy" /></UFormField><UFormField label="保留的场所"><USelectMenu v-model="targetId" :items="pointChoices.filter(x => x.value !== sourceId)" value-key="value" placeholder="资料归入这里" class="w-full" :disabled="busy" /></UFormField></div><div v-else class="field-grid"><UFormField label="交出人员"><USelect v-model="fromId" :items="staffChoices" placeholder="请选择" class="w-full" :disabled="busy" /></UFormField><UFormField label="接收人员"><USelect v-model="toId" :items="activeStaff.filter(x => x.value !== fromId)" placeholder="请选择" class="w-full" :disabled="busy" /></UFormField></div><UButton label="查看涉及的资料" :loading="busy" :disabled="mode === 'merge' ? !sourceId || !targetId : !fromId || !toId" @click="preview" /></section>
  <UAlert v-if="error" :description="error" color="error" /><UAlert v-if="message" :description="message" color="success" />
  <section v-if="merge" class="panel panel-body space-y-3"><h2 class="section-title">{{ merge.source.name }} → {{ merge.target.name }}</h2><p>转入 {{ merge.inspections }} 次检查、{{ merge.uses }} 条使用记录、{{ merge.units }} 个内部单元、{{ merge.photos }} 张场所照片、{{ merge.campaigns }} 条整治记录。</p><p>同步调整 {{ merge.plans }} 项检查安排。</p><p class="text-sm text-muted">保留场所的名称、地址和地图位置不变。原场所停用，历史检查中的原名称保留。</p></section>
  <section v-if="handoff" class="panel panel-body space-y-3"><h2 class="section-title">{{ handoff.fromName }} → {{ handoff.toName }}</h2><h3>未完成的检查安排（{{ handoff.plans.length }}）</h3><div v-for="plan in handoff.plans" :key="plan.id" class="text-sm">{{ plan.name }} · {{ plan.pendingPoints.length }} 个场所</div><h3>正在跟进的问题（{{ handoff.issues.length }}）</h3><div v-for="issue in handoff.issues" :key="issue.id" class="text-sm">{{ issue.name }}</div><p class="text-sm text-muted">只交接待办工作，已完成的检查和历史办理人保留。</p><UCheckbox v-model="disablePrevious" label="交接后停用原人员" /></section>
  <section v-if="merge || handoff" class="panel panel-body space-y-4"><UFormField label="办理原因" required><UTextarea v-model="reason" class="w-full" :disabled="busy" /></UFormField><UCheckbox v-model="confirm" :label="mode === 'merge' ? '已核对，确认是同一处场所' : '已核对以上待办工作'" :disabled="busy" /><UButton :label="mode === 'merge' ? '确认合并' : '确认交接'" :disabled="busy || !confirm || !reason.trim()" :loading="busy" @click="apply" /></section>
</div></template>
