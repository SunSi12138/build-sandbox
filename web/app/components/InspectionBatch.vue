<script setup lang="ts">
import type { FormError } from '@nuxt/ui'
import type { InspectionDetailDto, PhotoUploadDto } from '~/types/api'
import { errorMessage, todayLocal } from '~/utils/format'
const open = defineModel<boolean>('open', { default: false })
const props = defineProps<{ detail: InspectionDetailDto; mode: 'feedback' | 'review'; selectedIssue?: string }>()
const emit = defineEmits<{ saved: [detail: InspectionDetailDto]; busy: [value: boolean] }>()
const { bootstrap } = useAppData()
const focus = useDialogFocus('[data-testid="batch-feedback"]:not(:disabled), [data-testid="batch-review"]:not(:disabled)')
interface Entry { issueId: string; number: number; description: string; selected: boolean; text: string; photos: PhotoUploadDto[]; busy: boolean; failed: boolean }
const state = reactive<{ date: string; items: Entry[] }>({ date: todayLocal(), items: [] })
const saving = ref(false), error = ref(''), discardOpen = ref(false)
let initialState = ''
let leaveResolve: ((value: boolean) => void) | undefined
function fingerprint() { return JSON.stringify({ date: state.date, items: state.items.map(item => ({ id: item.issueId, selected: item.selected, text: item.text, photos: item.photos.map(photo => photo.id) })) }) }
const dirty = computed(() => fingerprint() !== initialState)
const photosFailed = computed(() => state.items.some(item => item.failed))
function requestClose() { if (busy.value) return; if (dirty.value) discardOpen.value = true; else open.value = false }
const modalOpen = computed({ get: () => open.value, set: value => { if (value) open.value = true; else requestClose() } })
function discard(confirmed: boolean) { discardOpen.value = false; if (confirmed) open.value = false; leaveResolve?.(confirmed); leaveResolve = undefined }
onBeforeRouteLeave(() => {
  if (!open.value) return true
  if (busy.value) return false
  if (!dirty.value) return true
  discardOpen.value = true
  return new Promise<boolean>(resolve => { leaveResolve = resolve })
})
const uploading = computed(() => state.items.some(item => item.busy))
const busy = computed(() => uploading.value || saving.value)
watch(busy, value => emit('busy', value), { flush: 'sync' })
const title = computed(() => props.mode === 'feedback' ? '记录反馈' : '复查闭环')
const textLabel = computed(() => props.mode === 'feedback' ? '整改情况' : '复查结论')
watch(open, value => {
  if (!value) return
  error.value = ''; state.date = bootstrap.value?.today ?? todayLocal()
  state.items = props.detail.issues.map((issue, index) => ({ issue, index })).filter(({ issue }) => issue.status === (props.mode === 'feedback' ? 'pending' : 'feedback')).map(({ issue, index }) => ({ issueId: issue.id, number: index + 1, description: issue.description, selected: !props.selectedIssue || props.selectedIssue === issue.id, text: '', photos: [], busy: false, failed: false }))
  initialState = fingerprint()
})
function validate(): FormError[] {
  const errors: FormError[] = []
  if (!state.date || state.date > todayLocal()) errors.push({ name: 'date', message: '日期不得晚于今天' })
  if (!state.items.some(item => item.selected)) errors.push({ name: 'items', message: '请选择问题' })
  state.items.forEach((item, index) => { if (item.selected && !item.text.trim()) errors.push({ name: `items.${index}.text`, message: `请填写${textLabel.value}` }) })
  return errors
}
async function save() {
  if (busy.value || validate().length) return
  saving.value = true; error.value = ''
  const items = state.items.filter(item => item.selected).map(item => ({ issueId: item.issueId, ...(props.mode === 'feedback' ? { notes: item.text.trim() } : { conclusion: item.text.trim() }), photoIds: item.photos.map(photo => photo.id) }))
  try { const result = await $fetch<InspectionDetailDto>(`/api/inspections/${props.detail.inspection.id}/${props.mode}`, { method: 'POST', body: { date: state.date, items }, retry: 0 }); open.value = false; emit('saved', result) }
  catch (value) { error.value = errorMessage(value) }
  finally { saving.value = false }
}
onBeforeUnmount(() => { emit('busy', false); leaveResolve?.(false) })
</script>
<template>
  <UModal v-model:open="modalOpen" :content="focus.content" @after:leave="focus.restoreFocus" :title="title" :dismissible="!busy" :close="!busy" :ui="{ content: 'mobile-full-dialog max-w-[560px]' }">
    <template #body><UForm id="batch-form" :state="state" :validate="validate" class="space-y-3" @submit="save"><UFormField name="date" :label="mode === 'feedback' ? '反馈日期' : '复查日期'" required><UInput v-model="state.date" data-testid="batch-date" type="date" :max="todayLocal()" :disabled="saving" class="w-full" /></UFormField><UFormField name="items"><div v-for="(item, index) in state.items" :key="item.issueId" class="border-b border-[var(--divider)] py-2"><div class="check-row"><UCheckbox v-model="item.selected" :label="`问题 ${item.number} · ${item.description}`" :disabled="busy" :data-testid="`batch-issue-${item.number}`" /></div><div v-if="item.selected" class="mt-2 space-y-3"><UFormField :name="`items.${index}.text`" :label="textLabel" required><UTextarea v-model="item.text" :rows="3" :maxlength="8000" class="w-full" :disabled="saving" :data-testid="`batch-notes-${item.number}`" /></UFormField><UFormField :label="mode === 'feedback' ? '整改后照片' : '复查照片'"><PhotoUploader v-model="item.photos" :phase="mode === 'feedback' ? 'after' : 'review'" :quality="bootstrap?.settings.jpegQuality" :disabled="saving" @busy="item.busy = $event" @failed="item.failed = $event" /></UFormField></div></div></UFormField><UAlert v-if="error" :description="error" color="error" variant="subtle" /></UForm></template>
    <template #footer><span v-if="uploading" class="mr-auto text-xs text-muted">{{ photosFailed ? '有照片未完成，请重试或移除' : '照片处理中' }}</span><UButton label="取消" color="neutral" variant="outline" :disabled="busy" @click="requestClose" /><UButton type="submit" form="batch-form" label="保存" :loading="saving" :disabled="uploading || !state.items.some(item => item.selected)" data-testid="batch-save" /></template>
  </UModal>
  <UModal v-model:open="discardOpen" title="放弃未提交内容？" :description="`未提交的${mode === 'feedback' ? '反馈' : '复查'}内容将丢失`" :dismissible="false" :close="false">
    <template #footer><UButton label="继续编辑" color="neutral" variant="outline" @click="discard(false)" /><UButton label="放弃" color="error" @click="discard(true)" /></template>
  </UModal>
</template>
