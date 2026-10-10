<script setup lang="ts">
import type { FormError } from '@nuxt/ui'
import type { FeedbackRequest, IssueDetailDto, IssueUpdateRequest, PhotoUploadDto, ReviewRequest } from '~/types/api'
import { errorMessage, queryText, statusColor, statusLabel, todayLocal } from '~/utils/format'

const route = useRoute()
const { bootstrap, load } = useAppData()
const toast = useToast()
const detail = ref<IssueDetailDto | null>(null)
const loading = ref(true)
const error = ref('')
const saving = ref(false)
const actionError = ref('')
const feedbackOpen = ref(false)
const reviewOpen = ref(false)
const returnOpen = ref(false)
const editOpen = ref(false)
const feedbackBusy = ref(false)
const reviewBusy = ref(false)
const feedbackPhotos = ref<PhotoUploadDto[]>([])
const reviewPhotos = ref<PhotoUploadDto[]>([])
const feedback = reactive({ date: todayLocal(), notes: '' })
const review = reactive({ date: todayLocal(), conclusion: '' })
const returnForm = reactive({ reason: '' })
const edit = reactive<IssueUpdateRequest>({ description: '', requirement: '', deadline: '', responsibleUnit: '' })
const issue = computed(() => detail.value?.issue)
const today = computed(() => bootstrap.value?.today || todayLocal())
const quality = computed(() => bootstrap.value?.settings.jpegQuality ?? 75)
const beforePhotos = computed(() => detail.value?.photos.filter(photo => photo.phase === 'site') ?? [])
const afterPhotos = computed(() => detail.value?.photos.filter(photo => photo.phase === 'after') ?? [])
const reviewImages = computed(() => detail.value?.photos.filter(photo => photo.phase === 'review') ?? [])
const returnTo = computed(() => {
  const target = queryText(route.query.returnTo)
  return /^\/issues\/?(?:\?|$)/.test(target) ? target : '/issues'
})
const actionLabels = { feedback: '社区反馈', review: '复查闭环', return: '退回待整改' }
const pageElement = ref<HTMLElement | null>(null)
const actionModalContent = { onCloseAutoFocus: (event: Event) => event.preventDefault() }
let requestVersion = 0
let disposed = false
useHead({ title: computed(() => issue.value ? `${issue.value.pointName} · 整改跟踪` : '问题详情') })

async function restoreActionFocus() {
  // Persistent photo dialogs do not emit closeAutoFocus. Reka emits after-leave
  // before changing presence, so wait for that update before restoring focus.
  await nextTick()
  await nextTick()
  if (disposed || !issue.value || !pageElement.value?.isConnected || feedbackOpen.value || reviewOpen.value || returnOpen.value || editOpen.value) return
  const action = { pending: 'feedback-open', feedback: 'review-open', closed: 'return-open' }[issue.value.status]
  const target = pageElement.value.querySelector<HTMLElement>(`[data-testid="${action}"]`) ?? document.getElementById('main-content')
  target?.focus({ preventScroll: true })
}

async function fetchDetail() {
  const version = ++requestVersion
  loading.value = true
  error.value = ''
  try {
    const id = queryText(route.params.id)
    const [result] = await Promise.all([$fetch<IssueDetailDto>(`/api/issues/${encodeURIComponent(id)}`), load()])
    if (version === requestVersion) detail.value = result
  } catch (cause) {
    if (version === requestVersion) error.value = errorMessage(cause)
  } finally {
    if (version === requestVersion) loading.value = false
  }
}

function beginFeedback() { actionError.value = ''; feedback.date = today.value; feedbackOpen.value = true }
function beginReview() { actionError.value = ''; review.date = today.value; reviewOpen.value = true }
function beginReturn() { actionError.value = ''; returnOpen.value = true }
function beginEdit() {
  if (!issue.value) return
  Object.assign(edit, { description: issue.value.description, requirement: issue.value.requirement, deadline: issue.value.deadline, responsibleUnit: issue.value.responsibleUnit })
  actionError.value = ''
  editOpen.value = true
}

function validateFeedback(state: typeof feedback): FormError[] {
  const errors: FormError[] = []
  if (!state.date) errors.push({ name: 'date', message: '请选择反馈日期。' })
  if (!state.notes.trim()) errors.push({ name: 'notes', message: '请填写社区反馈的整改情况。' })
  return errors
}
function validateReview(state: typeof review): FormError[] {
  const errors: FormError[] = []
  if (!state.date) errors.push({ name: 'date', message: '请选择复查日期。' })
  if (!state.conclusion.trim()) errors.push({ name: 'conclusion', message: '请填写现场复查结论。' })
  return errors
}
function validateEdit(state: IssueUpdateRequest): FormError[] {
  const errors: FormError[] = []
  if (!state.description.trim()) errors.push({ name: 'description', message: '请填写问题描述。' })
  if (!state.deadline) errors.push({ name: 'deadline', message: '请选择整改期限。' })
  if (!state.responsibleUnit.trim()) errors.push({ name: 'responsibleUnit', message: '请填写责任单位。' })
  return errors
}

async function saveFeedback() {
  if (!issue.value || feedbackBusy.value || saving.value) return
  saving.value = true
  actionError.value = ''
  try {
    const body: FeedbackRequest = { date: feedback.date, notes: feedback.notes.trim(), photoIds: feedbackPhotos.value.map(photo => photo.id) }
    detail.value = await $fetch<IssueDetailDto>(`/api/issues/${issue.value.id}/feedback`, { method: 'POST', body })
    feedbackOpen.value = false
    feedback.notes = ''
    feedbackPhotos.value = []
    toast.add({ title: '社区反馈已记录', description: '问题已进入待复查阶段。', color: 'success' })
  } catch (cause) { actionError.value = errorMessage(cause) }
  finally { saving.value = false }
}

async function saveReview() {
  if (!issue.value || reviewBusy.value || saving.value) return
  saving.value = true
  actionError.value = ''
  try {
    const body: ReviewRequest = { date: review.date, conclusion: review.conclusion.trim(), photoIds: reviewPhotos.value.map(photo => photo.id) }
    detail.value = await $fetch<IssueDetailDto>(`/api/issues/${issue.value.id}/review`, { method: 'POST', body })
    reviewOpen.value = false
    review.conclusion = ''
    reviewPhotos.value = []
    toast.add({ title: '已复查闭环', description: '本次结论和照片已保存到办理记录。', color: 'success' })
  } catch (cause) { actionError.value = errorMessage(cause) }
  finally { saving.value = false }
}

async function saveReturn() {
  if (!issue.value || saving.value) return
  saving.value = true
  actionError.value = ''
  try {
    detail.value = await $fetch<IssueDetailDto>(`/api/issues/${issue.value.id}/return`, { method: 'POST', body: { reason: returnForm.reason.trim() } })
    returnOpen.value = false
    returnForm.reason = ''
    toast.add({ title: '已退回待整改', description: '此前的反馈、复查与照片仍保留在办理记录中。', color: 'success' })
  } catch (cause) { actionError.value = errorMessage(cause) }
  finally { saving.value = false }
}

async function saveEdit() {
  if (!issue.value || saving.value) return
  saving.value = true
  actionError.value = ''
  try {
    detail.value = await $fetch<IssueDetailDto>(`/api/issues/${issue.value.id}`, { method: 'PUT', body: { ...edit } })
    editOpen.value = false
    toast.add({ title: '问题信息已更新', color: 'success' })
  } catch (cause) { actionError.value = errorMessage(cause) }
  finally { saving.value = false }
}

watch(() => route.params.id, () => {
  detail.value = null
  feedbackOpen.value = reviewOpen.value = returnOpen.value = editOpen.value = false
  feedback.notes = review.conclusion = returnForm.reason = ''
  feedbackPhotos.value = []
  reviewPhotos.value = []
  void fetchDetail()
}, { immediate: true })
onBeforeUnmount(() => { disposed = true; requestVersion++ })
</script>

<template>
  <div ref="pageElement" class="page-stack">
    <div><UButton :to="returnTo" icon="i-lucide-arrow-left" color="neutral" variant="ghost" class="min-h-12 -ml-2">返回问题清单</UButton></div>
    <PageHeader :title="issue?.pointName || '问题详情'" eyebrow="整改跟踪" :description="issue ? `${issue.communityName} · ${issue.inspectionDate} 排查发现` : '查看问题情况与办理记录。'">
      <template v-if="issue" #actions><UBadge :color="statusColor(issue.status)" size="lg" variant="subtle">{{ statusLabel(issue.status) }}</UBadge></template>
    </PageHeader>

    <UAlert v-if="error" title="暂时未能载入问题" :description="error" color="error" variant="subtle" icon="i-lucide-circle-alert">
      <template #actions><UButton color="error" variant="soft" class="min-h-12" @click="fetchDetail">重试</UButton></template>
    </UAlert>
    <div v-else-if="loading && !detail" class="space-y-4" aria-busy="true" aria-label="正在载入问题详情"><USkeleton class="h-64 w-full rounded-xl" /><USkeleton class="h-72 w-full rounded-xl" /></div>
    <template v-else-if="detail && issue">
      <UAlert v-if="issue.overdue" title="整改期限已过，尚未闭环" :description="`原定 ${issue.deadline} 前完成整改，请及时跟进办理情况。`" color="error" variant="subtle" icon="i-lucide-clock-alert" />
      <UAlert v-else-if="issue.status === 'closed'" title="本问题已完成复查闭环" :description="`闭环日期：${issue.closedDate}。排查、反馈和复查记录均已留存。`" color="success" variant="subtle" icon="i-lucide-circle-check" />

      <div class="grid items-start gap-5 lg:grid-cols-[minmax(0,1fr)_320px]">
        <UCard>
          <template #header>
            <div class="flex items-center justify-between gap-3"><h2 class="font-semibold text-highlighted">问题与整改要求</h2><UButton v-if="issue.status === 'pending'" icon="i-lucide-pencil" color="neutral" variant="ghost" class="min-h-12" @click="beginEdit">编辑</UButton></div>
          </template>
          <div class="space-y-6">
            <div><p class="mb-2 text-sm font-medium text-muted">现场发现</p><p class="whitespace-pre-wrap leading-7 text-highlighted">{{ issue.description }}</p></div>
            <div><p class="mb-2 text-sm font-medium text-muted">整改要求</p><p class="whitespace-pre-wrap text-sm leading-7">{{ issue.requirement || '按现场发现的问题完成整改。' }}</p></div>
            <div class="grid gap-4 border-t border-default pt-5 sm:grid-cols-2">
              <div><p class="mb-1 text-xs text-muted">整改期限</p><p :class="issue.overdue ? 'text-error' : 'text-highlighted'" class="font-semibold">{{ issue.deadline }}</p></div>
              <div><p class="mb-1 text-xs text-muted">责任单位</p><p class="font-medium">{{ issue.responsibleUnit }}</p></div>
            </div>
          </div>
        </UCard>
        <div class="space-y-5">
          <UCard>
            <template #header><h2 class="font-semibold text-highlighted">下一步办理</h2></template>
            <div class="space-y-3">
              <template v-if="issue.status === 'pending'">
                <p class="text-sm leading-6 text-muted">收到社区的整改情况后，登记反馈，再安排现场复查。</p>
                <UButton data-testid="feedback-open" icon="i-lucide-message-square-text" size="xl" block class="min-h-12" @click="beginFeedback">记录社区反馈</UButton>
              </template>
              <template v-else-if="issue.status === 'feedback'">
                <p class="text-sm leading-6 text-muted">社区已反馈整改情况，请核实整改效果并记录复查结论。</p>
                <UButton data-testid="review-open" icon="i-lucide-circle-check" size="xl" block class="min-h-12" @click="beginReview">复查闭环</UButton>
              </template>
              <p v-else class="text-sm leading-6 text-muted">需要继续整改时，可以退回待整改并说明原因。</p>
              <UButton v-if="issue.status !== 'pending'" data-testid="return-open" icon="i-lucide-rotate-ccw" color="neutral" variant="outline" size="xl" block class="min-h-12" @click="beginReturn">退回待整改</UButton>
            </div>
          </UCard>
          <UCard>
            <template #header><h2 class="font-semibold text-highlighted">本次排查</h2></template>
            <dl class="space-y-3 text-sm">
              <div class="flex justify-between gap-3"><dt class="shrink-0 text-muted">排查人</dt><dd>{{ detail.inspection.inspector }}</dd></div>
              <div class="flex justify-between gap-3"><dt class="shrink-0 text-muted">点位类型</dt><dd>{{ detail.inspection.typeName }}</dd></div>
              <div><dt class="mb-1 text-muted">详细地址</dt><dd class="leading-6">{{ detail.inspection.address }}</dd></div>
              <div v-if="detail.inspection.contact || detail.inspection.phone"><dt class="mb-1 text-muted">联系人</dt><dd>{{ detail.inspection.contact }} {{ detail.inspection.phone }}</dd></div>
            </dl>
            <UButton :to="`/inspections/${issue.inspectionId}`" color="neutral" variant="soft" trailing-icon="i-lucide-arrow-right" block class="mt-5 min-h-12">查看排查与导出</UButton>
          </UCard>
        </div>
      </div>

      <UCard>
        <template #header><div><h2 class="font-semibold text-highlighted">整改前后照片</h2><p class="mt-1 text-sm text-muted">点击照片可查看完整画面，历次整改照片均保留。</p></div></template>
        <div class="grid grid-cols-2 gap-3 sm:gap-6">
          <section class="min-w-0"><h3 class="mb-3 flex items-center gap-2 text-sm font-semibold"><span class="size-2 rounded-full bg-amber-500" />现场</h3><IssuePhotos :photos="beforePhotos" empty-text="未添加现场照片" /></section>
          <section class="min-w-0"><h3 class="mb-3 flex items-center gap-2 text-sm font-semibold"><span class="size-2 rounded-full bg-primary" />整改后</h3><IssuePhotos :photos="afterPhotos" empty-text="反馈时可添加整改后照片" /></section>
        </div>
        <section v-if="reviewImages.length" class="mt-6 border-t border-default pt-5"><h3 class="mb-3 text-sm font-semibold">复查照片</h3><div class="max-w-2xl"><IssuePhotos :photos="reviewImages" /></div></section>
      </UCard>

      <UCard>
        <template #header><div><h2 class="font-semibold text-highlighted">办理记录</h2><p class="mt-1 text-sm text-muted">反馈、复查和退回情况按登记顺序留存。</p></div></template>
        <div class="space-y-6">
          <article class="relative border-l-2 border-primary/20 pl-5">
            <span class="absolute -left-[5px] top-1.5 size-2 rounded-full bg-primary" />
            <div class="flex flex-wrap items-baseline justify-between gap-2"><h3 class="text-sm font-semibold">排查发现问题</h3><time class="text-xs text-muted">{{ issue.inspectionDate }}</time></div>
            <p class="mt-2 text-sm leading-6 text-muted">{{ detail.inspection.inspector }} 登记问题，要求 {{ issue.deadline }} 前完成整改。</p>
          </article>
          <article v-for="action in detail.actions" :key="action.id" class="relative border-l-2 border-default pl-5">
            <span class="absolute -left-[5px] top-1.5 size-2 rounded-full" :class="action.kind === 'return' ? 'bg-amber-500' : 'bg-primary'" />
            <div class="flex flex-wrap items-baseline justify-between gap-2"><h3 class="text-sm font-semibold">{{ actionLabels[action.kind] }}</h3><time class="text-xs text-muted">{{ action.date }}</time></div>
            <p class="mt-2 whitespace-pre-wrap text-sm leading-7">{{ action.notes }}</p>
            <div v-if="action.photos.length" class="mt-3 max-w-md"><IssuePhotos :photos="action.photos" /></div>
          </article>
        </div>
      </UCard>
    </template>

    <UModal v-model:open="feedbackOpen" title="记录社区反馈" description="记录社区反馈的整改情况，保存后进入待复查阶段。" :dismissible="!saving && !feedbackBusy" :close="!saving && !feedbackBusy" :unmount-on-hide="false" :content="actionModalContent" :ui="{ content: 'sm:max-w-2xl' }" @after:leave="restoreActionFocus">
      <template #body>
        <UForm id="issue-feedback-form" :state="feedback" :validate="validateFeedback" class="space-y-5" @submit="saveFeedback">
          <UAlert v-if="actionError" :description="actionError" color="error" variant="subtle" />
          <UFormField label="反馈日期" name="date" required><UInput v-model="feedback.date" data-testid="feedback-date" type="date" :min="issue?.inspectionDate" :max="today" size="xl" class="w-full" :ui="{ base: 'min-h-12' }" :disabled="saving" /></UFormField>
          <UFormField label="反馈说明" name="notes" required><UTextarea v-model="feedback.notes" data-testid="feedback-notes" placeholder="例如：已清理河岸垃圾，并安排专人每日巡查。" :rows="5" :maxlength="8000" size="xl" class="w-full" :disabled="saving" /></UFormField>
          <UFormField label="整改后照片" description="可拍照或从相册选择多张。"><PhotoUploader v-model="feedbackPhotos" phase="after" :quality="quality" :disabled="saving" @busy="feedbackBusy = $event" /></UFormField>
        </UForm>
      </template>
      <template #footer><div class="flex w-full justify-end gap-3"><UButton color="neutral" variant="outline" size="xl" class="min-h-12" :disabled="saving || feedbackBusy" @click="feedbackOpen = false">暂不提交</UButton><UButton form="issue-feedback-form" data-testid="feedback-save" type="submit" size="xl" class="min-h-12" :loading="saving" :disabled="feedbackBusy">保存反馈</UButton></div></template>
    </UModal>

    <UModal v-model:open="reviewOpen" title="复查闭环" description="核实整改效果后，填写现场复查结论。照片可选。" :dismissible="!saving && !reviewBusy" :close="!saving && !reviewBusy" :unmount-on-hide="false" :content="actionModalContent" :ui="{ content: 'sm:max-w-2xl' }" @after:leave="restoreActionFocus">
      <template #body>
        <UForm id="issue-review-form" :state="review" :validate="validateReview" class="space-y-5" @submit="saveReview">
          <UAlert v-if="actionError" :description="actionError" color="error" variant="subtle" />
          <UFormField label="复查日期" name="date" required><UInput v-model="review.date" data-testid="review-date" type="date" :min="issue?.feedbackDate || issue?.inspectionDate" :max="today" size="xl" class="w-full" :ui="{ base: 'min-h-12' }" :disabled="saving" /></UFormField>
          <UFormField label="复查结论" name="conclusion" required><UTextarea v-model="review.conclusion" data-testid="review-conclusion" placeholder="请写明复查发现的实际整改效果及闭环意见。" :rows="5" :maxlength="8000" size="xl" class="w-full" :disabled="saving" /></UFormField>
          <UFormField label="复查照片（可选）" description="没有照片也可以保存复查结论。"><PhotoUploader v-model="reviewPhotos" phase="review" :quality="quality" :disabled="saving" @busy="reviewBusy = $event" /></UFormField>
        </UForm>
      </template>
      <template #footer><div class="flex w-full justify-end gap-3"><UButton color="neutral" variant="outline" size="xl" class="min-h-12" :disabled="saving || reviewBusy" @click="reviewOpen = false">暂不提交</UButton><UButton form="issue-review-form" data-testid="review-save" type="submit" size="xl" class="min-h-12" :loading="saving" :disabled="reviewBusy">确认复查闭环</UButton></div></template>
    </UModal>

    <UModal v-model:open="returnOpen" title="退回待整改？" description="问题将重新进入待整改状态，原有办理记录和照片会继续保留。" :dismissible="!saving" :close="!saving" :content="actionModalContent" @after:leave="restoreActionFocus">
      <template #body><UForm id="issue-return-form" :state="returnForm" class="space-y-4" @submit="saveReturn"><UAlert v-if="actionError" :description="actionError" color="error" variant="subtle" /><UFormField label="退回原因（可选）" name="reason"><UTextarea v-model="returnForm.reason" data-testid="return-reason" :rows="4" :maxlength="8000" placeholder="说明还需整改的问题，便于下一次跟进。" size="xl" class="w-full" :disabled="saving" /></UFormField></UForm></template>
      <template #footer><div class="flex w-full justify-end gap-3"><UButton color="neutral" variant="outline" size="xl" class="min-h-12" :disabled="saving" @click="returnOpen = false">取消</UButton><UButton form="issue-return-form" data-testid="return-save" type="submit" color="warning" size="xl" class="min-h-12" :loading="saving">确认退回</UButton></div></template>
    </UModal>

    <UModal v-model:open="editOpen" title="编辑问题信息" description="更新问题描述、整改要求、期限或责任单位。" :dismissible="!saving" :close="!saving" :ui="{ content: 'sm:max-w-2xl' }">
      <template #body><UForm id="issue-edit-form" :state="edit" :validate="validateEdit" class="space-y-5" @submit="saveEdit">
        <UAlert v-if="actionError" :description="actionError" color="error" variant="subtle" />
        <UFormField label="问题描述" name="description" required><UTextarea v-model="edit.description" :rows="4" :maxlength="8000" size="xl" class="w-full" :disabled="saving" /></UFormField>
        <UFormField label="整改要求" name="requirement"><UTextarea v-model="edit.requirement" :rows="3" :maxlength="8000" size="xl" class="w-full" :disabled="saving" /></UFormField>
        <div class="grid gap-4 md:grid-cols-2"><UFormField label="整改期限" name="deadline" required><UInput v-model="edit.deadline" type="date" size="xl" class="w-full" :ui="{ base: 'min-h-12' }" :disabled="saving" /></UFormField><UFormField label="责任单位" name="responsibleUnit" required><UInput v-model="edit.responsibleUnit" :maxlength="200" size="xl" class="w-full" :ui="{ base: 'min-h-12' }" :disabled="saving" /></UFormField></div>
      </UForm></template>
      <template #footer><div class="flex w-full justify-end gap-3"><UButton color="neutral" variant="outline" size="xl" class="min-h-12" :disabled="saving" @click="editOpen = false">取消</UButton><UButton form="issue-edit-form" type="submit" size="xl" class="min-h-12" :loading="saving">保存修改</UButton></div></template>
    </UModal>
  </div>
</template>
