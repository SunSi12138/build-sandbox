<script setup lang="ts">
import type { FormError } from '@nuxt/ui'
import type {
  InspectionDetailDto,
  InspectionUpdateRequest,
  IssueDto,
  IssueUpdateRequest,
} from '~/types/api'
import { downloadWord } from '~/utils/wordDownload'
import {
  errorMessage,
  overdueDays,
  queryText,
  rectifyLabel,
  rectifyTone,
  statusColor,
  statusLabel,
  todayLocal,
} from '~/utils/format'
const { bootstrap } = useAppData()
const route = useRoute(),
  toast = useToast()
const focus = useDialogFocus(
  '[data-testid="batch-feedback"]:not(:disabled), [data-testid="batch-review"]:not(:disabled)',
)
const detail = ref<InspectionDetailDto | null>(null),
  loading = ref(true),
  error = ref('')
const id = computed(() => queryText(route.params.id))
const back = computed(() => {
  const value = queryText(route.query.returnTo)
  return value.startsWith('/points/') || /^\/inspections\/?(?:\?|$)/.test(value)
    ? value
    : '/inspections'
})
const expanded = reactive<Record<string, boolean>>({}),
  historyOpen = reactive<Record<string, boolean>>({})
const batchOpen = ref(false),
  batchMode = ref<'feedback' | 'review'>('feedback'),
  selectedIssue = ref(''),
  batchBusy = ref(false)
const editOpen = ref(false),
  editing = ref(false),
  editError = ref(''),
  promoteOpen = ref(false),
  deleteOpen = ref(false),
  deleting = ref(false),
  deleteError = ref('')
const issueEditOpen = ref(false),
  issueEditing = ref(false),
  issueEditError = ref(''),
  issueToEdit = ref<IssueDto | null>(null)
const returnOpen = ref(false),
  returning = ref(false),
  returnError = ref(''),
  returnReason = ref(''),
  issueToReturn = ref<IssueDto | null>(null)
const editState = reactive<InspectionUpdateRequest>({
  date: '',
  inspector: '',
  noticeNumber: '',
  notes: '',
})
const issueState = reactive<IssueUpdateRequest>({
  description: '',
  requirement: '',
  deadline: '',
  responsibleUnit: '',
  issueTypeId: '',
})
const overlayForm = useState<boolean>('street-eco-overlay-form', () => false)
watch(
  () => editOpen.value || batchOpen.value || issueEditOpen.value,
  (value) => {
    overlayForm.value = value
  },
)
const allClosed = computed(
  () =>
    !!detail.value?.issues.length &&
    detail.value.issues.every((issue) => issue.status === 'closed'),
)
const sitePhotos = computed(
  () =>
    detail.value?.photos.filter(
      (photo) => photo.phase === 'site' && photo.inspectionId && !photo.issueId,
    ) ?? [],
)
const issueDetails = computed(
  () => new Map(detail.value?.issueDetails?.map((item) => [item.issue.id, item]) ?? []),
)
const exportItems = computed(() => [
  {
    label: '导出排查记录单',
    onSelect: () => {
      void exportRecord('record')
    },
  },
  ...(detail.value?.issues.length
    ? [
        {
          label: '导出整改通知单',
          onSelect: () => {
            void exportRecord('notice')
          },
        },
      ]
    : []),
  ...(allClosed.value
    ? [
        {
          label: '导出整改记录单',
          onSelect: () => {
            void exportRecord('rectification')
          },
        },
      ]
    : []),
])
async function exportRecord(kind: string) {
  try {
    await downloadWord(`/api/inspections/${id.value}/export/${kind}`, '排查导出.docx')
  } catch (value) {
    toast.add({ title: errorMessage(value), color: 'error', duration: 0 })
  }
}
const moreItems = computed(() => [
  { label: '编辑排查信息', onSelect: openEdit },
  ...(detail.value?.inspection.pointTemporary
    ? [
        {
          label: '转为常态点位',
          onSelect: () => {
            promoteOpen.value = true
          },
        },
      ]
    : []),
  {
    label: '删除排查',
    color: 'error' as const,
    onSelect: () => {
      deleteError.value = ''
      deleteOpen.value = true
    },
  },
])
useHead({
  title: computed(() => `${detail.value?.inspection.pointName || '排查详情'} · 街道生态环保`),
})
function assign(value: InspectionDetailDto) {
  detail.value = value
  for (const issue of value.issues)
    if (!(issue.id in expanded))
      expanded[issue.id] = value.issues.length <= 3 || issue.status !== 'closed'
  void revealIssue()
}
async function revealIssue() {
  const issue = queryText(route.query.issue)
  if (!issue || !detail.value?.issues.some((item) => item.id === issue)) return
  expanded[issue] = true
  await nextTick()
  document.getElementById(`issue-${issue}`)?.scrollIntoView({ block: 'start', behavior: 'auto' })
}
async function load() {
  if (!id.value) return
  loading.value = true
  error.value = ''
  try {
    assign(await $fetch<InspectionDetailDto>(`/api/inspections/${id.value}`))
  } catch (value) {
    error.value = errorMessage(value)
  } finally {
    loading.value = false
    await nextTick()
    void revealIssue()
  }
}
function openBatch(mode: 'feedback' | 'review', issue = '') {
  batchMode.value = mode
  selectedIssue.value = issue
  batchOpen.value = true
}
function batchSaved(value: InspectionDetailDto) {
  assign(value)
  toast.add({ title: '已保存', color: 'success', duration: 2000 })
}
function openEdit() {
  if (!detail.value) return
  Object.assign(editState, {
    date: detail.value.inspection.date,
    inspector: detail.value.inspection.inspector,
    source: detail.value.inspection.source,
    noticeNumber: detail.value.inspection.noticeNumber,
    notes: detail.value.inspection.notes,
  })
  editError.value = ''
  editOpen.value = true
}
function validateEdit(): FormError[] {
  const errors: FormError[] = []
  if (!editState.date || editState.date > todayLocal())
    errors.push({ name: 'date', message: '排查日期不得晚于今天' })
  if (!editState.inspector.trim()) errors.push({ name: 'inspector', message: '请填写排查人' })
  return errors
}
async function saveEdit() {
  if (editing.value || validateEdit().length) return
  editing.value = true
  editError.value = ''
  try {
    assign(
      await $fetch<InspectionDetailDto>(`/api/inspections/${id.value}`, {
        method: 'PUT',
        body: editState,
        retry: 0,
      }),
    )
    editOpen.value = false
    toast.add({ title: '已保存', duration: 2000 })
  } catch (value) {
    editError.value = errorMessage(value)
  } finally {
    editing.value = false
  }
}
async function remove() {
  deleting.value = true
  deleteError.value = ''
  try {
    await $fetch(`/api/inspections/${id.value}`, {
      method: 'DELETE',
      query: { confirm: true },
      retry: 0,
    })
    deleteOpen.value = false
    toast.add({ title: '已删除', duration: 2000 })
    await navigateTo('/inspections')
  } catch (value) {
    deleteError.value = errorMessage(value)
  } finally {
    deleting.value = false
  }
}
function openIssueEdit(issue: IssueDto) {
  issueToEdit.value = issue
  Object.assign(issueState, {
    description: issue.description,
    requirement: issue.requirement,
    deadline: issue.deadline,
    responsibleUnit: issue.responsibleUnit,
    issueTypeId: issue.issueTypeId,
  })
  issueEditError.value = ''
  issueEditOpen.value = true
}
function validateIssue(): FormError[] {
  const errors: FormError[] = []
  if (!issueState.issueTypeId) errors.push({ name: 'issueTypeId', message: '请选择问题类型' })
  if (!issueState.description.trim())
    errors.push({ name: 'description', message: '请填写问题描述' })
  if (!issueState.deadline) errors.push({ name: 'deadline', message: '请选择整改期限' })
  return errors
}
async function saveIssue() {
  if (issueEditing.value || !issueToEdit.value || validateIssue().length) return
  issueEditing.value = true
  issueEditError.value = ''
  try {
    await $fetch(`/api/issues/${issueToEdit.value.id}`, {
      method: 'PUT',
      body: issueState,
      retry: 0,
    })
    issueEditOpen.value = false
    await load()
    toast.add({ title: '已保存', duration: 2000 })
  } catch (value) {
    issueEditError.value = errorMessage(value)
  } finally {
    issueEditing.value = false
  }
}
function openReturn(issue: IssueDto) {
  issueToReturn.value = issue
  returnReason.value = ''
  returnError.value = ''
  returnOpen.value = true
}
async function returnIssue() {
  if (returning.value || !issueToReturn.value) return
  returning.value = true
  returnError.value = ''
  try {
    await $fetch(`/api/issues/${issueToReturn.value.id}/return`, {
      method: 'POST',
      body: { reason: returnReason.value },
      retry: 0,
    })
    returnOpen.value = false
    await load()
    toast.add({ title: '已退回', duration: 2000 })
  } catch (value) {
    returnError.value = errorMessage(value)
  } finally {
    returning.value = false
  }
}
function beforeUnload(event: BeforeUnloadEvent) {
  if (batchBusy.value || editing.value || issueEditing.value || returning.value || deleting.value) {
    event.preventDefault()
    event.returnValue = ''
  }
}
onBeforeRouteLeave(
  () => !batchBusy.value && !editing.value && !issueEditing.value && !returning.value,
)
onMounted(() => window.addEventListener('beforeunload', beforeUnload))
onBeforeUnmount(() => {
  overlayForm.value = false
  window.removeEventListener('beforeunload', beforeUnload)
})
watch(id, load, { immediate: true })
watch(() => route.query.issue, revealIssue)
</script>
<template>
  <div class="page-stack">
    <PageHeader :title="detail?.inspection.pointName || '排查详情'" :back="back"
      ><template #badge
        ><StatusBadge v-if="detail?.inspection.pointTemporary" tone="outline"
          >临时</StatusBadge
        ></template
      ><template v-if="detail" #actions
        ><div class="hidden items-center gap-2 md:flex">
          <UButton
            @click="exportRecord('record')"
            label="导出排查记录单"
            color="neutral"
            variant="outline"
            data-testid="export-record"
          /><UButton
            v-if="detail.issues.length"
            @click="exportRecord('notice')"
            label="导出整改通知单"
            color="neutral"
            variant="outline"
            data-testid="export-notice"
          /><UButton
            v-if="allClosed"
            @click="exportRecord('rectification')"
            label="导出整改记录单"
            color="neutral"
            variant="outline"
            data-testid="export-rectification"
          />
        </div>
        <UDropdownMenu :items="exportItems"
          ><UButton
            label="导出"
            trailing-icon="i-lucide-chevron-down"
            color="neutral"
            variant="outline"
            class="mobile-only" /></UDropdownMenu
        ><UDropdownMenu :items="moreItems"
          ><UButton
            icon="i-lucide-ellipsis"
            color="neutral"
            variant="ghost"
            aria-label="更多" /></UDropdownMenu></template
    ></PageHeader>
    <PageState :loading="loading" :error="error" @retry="load"
      ><template v-if="detail">
        <p class="page-meta">
          {{ detail.inspection.date }} · <NameTags :value="detail.inspection.inspector" /> ·
          {{ detail.inspection.communityName }}
        </p>
        <div class="flex flex-wrap items-center gap-2">
          <StatusBadge :tone="rectifyTone(detail.inspection.rectifyStatus)">{{
            rectifyLabel(detail.inspection)
          }}</StatusBadge
          ><span v-if="detail.issues.length" class="text-xs text-muted"
            >已闭环 {{ detail.inspection.closedCount }}/{{ detail.inspection.issueCount }}</span
          ><StatusBadge v-if="detail.inspection.overdueCount" tone="danger"
            >逾期 {{ overdueDays(detail.inspection.nearestDeadline) }} 天</StatusBadge
          >
        </div>
        <RecordRelations kind="inspection" :id="detail.inspection.id" :tags="detail.inspection.tags" :matters="detail.inspection.matters" @changed="load" />
        <section class="panel">
          <div class="panel-head">
            <h2 class="section-title">基本信息</h2>
            <UButton
              v-if="detail.inspection.pointTemporary"
              label="转为常态点位"
              variant="link"
              data-testid="promote-point"
              @click="promoteOpen = true"
            />
          </div>
          <dl class="detail-grid panel-body">
            <div>
              <dt>点位</dt>
              <dd>
                <NuxtLink
                  :to="`/points/${detail.inspection.pointId}`"
                  class="inline-flex items-center text-primary"
                  >{{ detail.inspection.pointName }}</NuxtLink
                >
              </dd>
            </div>
            <div>
              <dt>社区</dt>
              <dd>{{ detail.inspection.communityName }}</dd>
            </div>
            <div>
              <dt>类型</dt>
              <dd>{{ detail.inspection.typeName }}</dd>
            </div>
            <div class="detail-wide">
              <dt>地址</dt>
              <dd class="flex items-center justify-between gap-2">
                <span>{{ detail.inspection.address }}</span
                ><MapLinks
                  :name="detail.inspection.pointName"
                  :longitude="detail.inspection.longitude"
                  :latitude="detail.inspection.latitude"
                  labels
                />
              </dd>
            </div>
            <div>
              <dt>联系人</dt>
              <dd><NameTags :value="detail.inspection.contact" /></dd>
            </div>
            <div>
              <dt>电话</dt>
              <dd>
                <a
                  v-if="detail.inspection.phone"
                  :href="`tel:${detail.inspection.phone}`"
                  class="inline-flex items-center text-primary"
                  >{{ detail.inspection.phone }}</a
                ><span v-else>—</span>
              </dd>
            </div>
            <div>
              <dt>排查来源</dt><dd>{{ detail.inspection.source === 'matter' ? '事项核查' : detail.inspection.source === 'other' ? '其他' : '日常巡查' }}</dd>
            </div>
            <div>
              <dt>排查日期</dt>
              <dd>{{ detail.inspection.date }}</dd>
            </div>
            <div>
              <dt>排查人</dt>
              <dd><NameTags :value="detail.inspection.inspector" /></dd>
            </div>
            <div>
              <dt>通知单编号</dt>
              <dd>{{ detail.inspection.noticeNumber || '—' }}</dd>
            </div>
          </dl>
        </section>
        <section class="panel">
          <div class="panel-head">
            <h2 class="section-title">现场照片</h2>
            <span class="text-xs text-muted">{{ sitePhotos.length }} 张</span>
          </div>
          <div class="panel-body"><IssuePhotos :photos="sitePhotos" /></div>
        </section>
        <section v-if="detail.issues.length" class="panel">
          <div class="panel-head">
            <h2 class="section-title">问题与整改</h2>
            <div class="flex gap-1">
              <UButton
                label="记录反馈"
                color="neutral"
                variant="outline"
                :disabled="!detail.issues.some((issue) => issue.status === 'pending')"
                data-testid="batch-feedback"
                @click="openBatch('feedback')"
              /><UButton
                label="复查闭环"
                color="neutral"
                variant="outline"
                :disabled="!detail.issues.some((issue) => issue.status === 'feedback')"
                data-testid="batch-review"
                @click="openBatch('review')"
              />
            </div>
          </div>
          <section
            v-for="(issue, index) in detail.issues"
            :id="`issue-${issue.id}`"
            :key="issue.id"
            class="issue-block scroll-mt-16"
          >
            <button
              type="button"
              class="issue-toggle"
              :aria-expanded="expanded[issue.id]"
              :aria-controls="`issue-body-${issue.id}`"
              @click="expanded[issue.id] = !expanded[issue.id]"
            >
              <UIcon
                :name="expanded[issue.id] ? 'i-lucide-chevron-down' : 'i-lucide-chevron-right'"
                class="size-4 shrink-0 text-muted"
              /><span class="min-w-0 flex-1"
                ><span class="block truncate text-sm font-medium"
                  >{{ index + 1 }} · {{ issue.description }}</span
                ><span class="block text-xs" :class="issue.overdue ? 'text-error' : 'text-muted'"
                  >{{ issue.deadline
                  }}<span v-if="issue.overdue">
                    · 逾期 {{ overdueDays(issue.deadline) }} 天</span
                  ></span
                ></span
              ><StatusBadge :tone="statusColor(issue.status)">{{
                statusLabel(issue.status)
              }}</StatusBadge>
            </button>
            <div v-if="expanded[issue.id]" :id="`issue-body-${issue.id}`" class="issue-body">
              <dl class="detail-grid">
                <div class="detail-wide">
                  <dt>问题类型</dt><dd>{{ issue.issueTypeName }}</dd><dt>问题描述</dt>
                  <dd class="whitespace-pre-wrap">{{ issue.description }}</dd>
                </div>
                <div class="detail-wide">
                  <dt>整改要求</dt>
                  <dd class="whitespace-pre-wrap">{{ issue.requirement || '—' }}</dd>
                </div>
                <div>
                  <dt>责任单位</dt>
                  <dd><NameTags :value="issue.responsibleUnit" /></dd>
                </div>
              </dl>
              <RecordRelations kind="issue" :id="issue.id" :matters="issue.matters" @changed="load" />
              <IssuePhotoGroups :photos="issueDetails.get(issue.id)?.photos ?? []" />
              <UCollapsible v-model:open="historyOpen[issue.id]"
                ><UButton
                  label="办理记录"
                  :trailing-icon="
                    historyOpen[issue.id] ? 'i-lucide-chevron-up' : 'i-lucide-chevron-down'
                  "
                  color="neutral"
                  variant="link"
                  class="px-0"
                /><template #content
                  ><div v-if="issueDetails.get(issue.id)?.actions.length" class="action-timeline">
                    <div v-for="action in issueDetails.get(issue.id)?.actions" :key="action.id">
                      <p class="text-xs text-muted">
                        {{ action.date }} ·
                        {{
                          action.kind === 'feedback'
                            ? '反馈'
                            : action.kind === 'review'
                              ? '复查'
                              : '退回'
                        }}
                      </p>
                      <p class="whitespace-pre-wrap text-sm">{{ action.notes }}</p>
                      <IssuePhotos v-if="action.photos.length" :photos="action.photos" />
                    </div>
                  </div>
                  <p v-else class="text-xs text-muted">暂无办理记录</p></template
                ></UCollapsible
              >
              <div class="flex flex-wrap gap-2">
                <UButton
                  v-if="issue.status === 'pending'"
                  label="记录反馈"
                  color="neutral"
                  variant="outline"
                  @click="openBatch('feedback', issue.id)"
                /><UButton
                  v-if="issue.status === 'feedback'"
                  label="复查闭环"
                  color="neutral"
                  variant="outline"
                  @click="openBatch('review', issue.id)"
                /><UButton
                  v-if="issue.status !== 'pending'"
                  label="退回待整改"
                  color="neutral"
                  variant="outline"
                  @click="openReturn(issue)"
                /><UButton
                  v-if="issue.status === 'pending'"
                  label="编辑"
                  color="neutral"
                  variant="link"
                  @click="openIssueEdit(issue)"
                />
              </div>
            </div>
          </section>
        </section>
        <section v-if="detail.inspection.notes" class="panel">
          <div class="panel-head"><h2 class="section-title">备注</h2></div>
          <p class="panel-body whitespace-pre-wrap">{{ detail.inspection.notes }}</p>
        </section>
      </template></PageState
    >
    <InspectionBatch
      v-if="detail"
      v-model:open="batchOpen"
      :detail="detail"
      :mode="batchMode"
      :selected-issue="selectedIssue"
      @saved="batchSaved"
      @busy="batchBusy = $event"
    /><PointPromote
      v-if="detail"
      v-model:open="promoteOpen"
      :point-id="detail.inspection.pointId"
      @saved="load"
    />
    <UModal
      v-model:open="editOpen"
      :content="focus.content"
      @after:leave="focus.restoreFocus"
      title="编辑排查信息"
      :dismissible="!editing"
      :close="!editing"
      :ui="{ content: 'mobile-full-dialog' }"
      ><template #body
        ><UForm
          id="inspection-edit"
          :state="editState"
          :validate="validateEdit"
          class="field-grid"
          @submit="saveEdit"
          ><UFormField name="date" label="排查日期" required
            ><UInput
              v-model="editState.date"
              type="date"
              :max="todayLocal()"
              class="w-full"
              :disabled="editing" /></UFormField
          ><UFormField name="inspector" label="排查人" required
            ><NameTagsInput
              v-model="editState.inspector"
              :maxlength="100"
              class="w-full"
              :disabled="editing" /></UFormField
          ><UFormField name="source" label="排查来源" class="field-full"><USelect v-model="editState.source" :items="detail?.inspection.matters.length ? [{ label: '事项核查', value: 'matter' }] : [{ label: '日常巡查', value: 'routine' }, { label: '其他', value: 'other' }]" :disabled="editing || !!detail?.inspection.matters.length" class="w-full" /></UFormField
          ><UFormField name="noticeNumber" label="通知单编号" class="field-full"
            ><UInput
              v-model="editState.noticeNumber"
              :maxlength="100"
              class="w-full"
              :disabled="editing" /></UFormField
          ><UFormField name="notes" label="备注" class="field-full"
            ><UTextarea
              v-model="editState.notes"
              :rows="4"
              :maxlength="8000"
              class="w-full"
              :disabled="editing" /></UFormField
          ><UAlert
            v-if="editError"
            :description="editError"
            color="error"
            variant="subtle"
            class="field-full" /></UForm></template
      ><template #footer
        ><UButton
          label="取消"
          color="neutral"
          variant="outline"
          :disabled="editing"
          @click="editOpen = false" /><UButton
          type="submit"
          form="inspection-edit"
          label="保存"
          :loading="editing" /></template
    ></UModal>
    <UModal
      v-model:open="issueEditOpen"
      :content="focus.content"
      @after:leave="focus.restoreFocus"
      title="编辑问题"
      :dismissible="!issueEditing"
      :close="!issueEditing"
      :ui="{ content: 'mobile-full-dialog' }"
      ><template #body
        ><UForm
          id="issue-edit"
          :state="issueState"
          :validate="validateIssue"
          class="field-grid"
          @submit="saveIssue"
          ><UFormField name="issueTypeId" label="问题类型" required class="field-full"><USelect v-model="issueState.issueTypeId" :items="(bootstrap?.issueTypes ?? []).map(item => ({ label: item.name, value: item.id }))" placeholder="请选择" class="w-full" :disabled="issueEditing" /></UFormField><UFormField name="description" label="问题描述" required class="field-full"
            ><UTextarea
              v-model="issueState.description"
              :rows="3"
              :maxlength="8000"
              class="w-full"
              :disabled="issueEditing" /></UFormField
          ><UFormField name="requirement" label="整改要求" class="field-full"
            ><UTextarea
              v-model="issueState.requirement"
              :rows="3"
              :maxlength="8000"
              class="w-full"
              :disabled="issueEditing" /></UFormField
          ><UFormField name="deadline" label="整改期限" required
            ><UInput
              v-model="issueState.deadline"
              type="date"
              class="w-full"
              :disabled="issueEditing" /></UFormField
          ><UFormField name="responsibleUnit" label="责任单位"
            ><NameTagsInput
              v-model="issueState.responsibleUnit"
              :maxlength="200"
              class="w-full"
              :disabled="issueEditing" /></UFormField
          ><UAlert
            v-if="issueEditError"
            :description="issueEditError"
            color="error"
            variant="subtle"
            class="field-full" /></UForm></template
      ><template #footer
        ><UButton
          label="取消"
          color="neutral"
          variant="outline"
          :disabled="issueEditing"
          @click="issueEditOpen = false" /><UButton
          type="submit"
          form="issue-edit"
          label="保存"
          :loading="issueEditing" /></template
    ></UModal>
    <UModal
      v-model:open="returnOpen"
      :content="focus.content"
      @after:leave="focus.restoreFocus"
      title="退回待整改"
      :dismissible="!returning"
      :close="!returning"
      ><template #body
        ><UFormField label="退回原因"
          ><UTextarea
            v-model="returnReason"
            data-testid="return-reason"
            :rows="3"
            :maxlength="8000"
            class="w-full"
            :disabled="returning" /></UFormField
        ><UAlert
          v-if="returnError"
          :description="returnError"
          color="error"
          variant="subtle"
          class="mt-3" /></template
      ><template #footer
        ><UButton
          label="取消"
          color="neutral"
          variant="outline"
          :disabled="returning"
          @click="returnOpen = false" /><UButton
          label="退回"
          data-testid="return-save"
          :loading="returning"
          @click="returnIssue" /></template
    ></UModal>
    <UModal
      v-model:open="deleteOpen"
      title="删除排查？"
      :description="
        detail?.inspection.pointTemporary
          ? '删除后，问题、办理记录和照片无法恢复；若这是该临时点位最后一条排查或整治，该临时点位及其没有其他活动、事项或附件时，点位照片也将删除。'
          : '删除后，问题、办理记录和照片无法恢复'
      "
      :dismissible="!deleting"
      :close="!deleting"
      ><template #body
        ><UAlert
          v-if="deleteError"
          :description="deleteError"
          color="error"
          variant="subtle" /></template
      ><template #footer
        ><UButton
          label="取消"
          color="neutral"
          variant="outline"
          :disabled="deleting"
          @click="deleteOpen = false" /><UButton
          label="删除"
          color="error"
          :loading="deleting"
          @click="remove" /></template
    ></UModal>
  </div>
</template>
