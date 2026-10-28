<script setup lang="ts">
import type { AttachmentDto } from '~/types/api'
import { errorMessage, formatBytes } from '~/utils/format'
const props = defineProps<{ pointId: string }>()
const items = ref<AttachmentDto[]>([]), error = ref(''), busy = ref(false), uploadName = ref('')
const input = ref<HTMLInputElement | null>(null), editing = ref<AttachmentDto | null>(null), deleting = ref<AttachmentDto | null>(null)
const editOpen = ref(false), deleteOpen = ref(false), saving = ref(false), actionError = ref('')
const form = reactive({ name: '', notes: '' })
const listRequest = useLatestRequest()
let uploadController: AbortController | undefined, ownerVersion = 0
async function load() {
  const request = listRequest.start(), pointId = props.pointId
  try {
    const result = await $fetch<AttachmentDto[]>(`/api/points/${pointId}/attachments`, { signal: request.signal })
    if (request.isCurrent()) { items.value = result; error.value = '' }
  } catch (cause) { if (request.isCurrent()) error.value = errorMessage(cause) }
}
async function upload(event: Event) {
  const element = event.target as HTMLInputElement, files = [...element.files ?? []]; element.value = ''
  if (busy.value || !files.length) return
  const pointId = props.pointId, controller = new AbortController(), version = ownerVersion
  uploadController = controller; busy.value = true; error.value = ''
  const current = () => version === ownerVersion && uploadController === controller && !controller.signal.aborted
  const failures: string[] = []
  try {
    for (const file of files) {
      if (!current()) break
      uploadName.value = file.name
      if (!file.size || file.size > 25 * 1024 * 1024) { failures.push(`${file.name}：文件应大于 0 且不超过 25 MB`); continue }
      try { await $fetch(`/api/points/${pointId}/attachments`, { method: 'PUT', query: { name: file.name }, body: file, headers: { 'content-type': 'application/octet-stream' }, retry: 0, signal: controller.signal }) }
      catch (cause) { if (!current()) break; failures.push(`${file.name}：${errorMessage(cause)}`) }
    }
    if (current()) {
      await load()
      if (current() && failures.length) error.value = [error.value, ...failures].filter(Boolean).join('\n')
    }
  } finally { if (current()) { busy.value = false; uploadName.value = ''; uploadController = undefined } }
}
function edit(item: AttachmentDto) { editing.value = item; form.name = item.name; form.notes = item.notes; actionError.value = ''; editOpen.value = true }
async function save() {
  if (!editing.value || saving.value) return
  const version = ownerVersion, attachmentId = editing.value.id, body = { ...form }
  saving.value = true; actionError.value = ''
  try { await $fetch(`/api/attachments/${attachmentId}`, { method: 'PUT', body, retry: 0 }); if (version === ownerVersion) { editOpen.value = false; await load() } }
  catch (cause) { if (version === ownerVersion) actionError.value = errorMessage(cause) }
  finally { if (version === ownerVersion) saving.value = false }
}
async function remove() {
  if (!deleting.value || saving.value) return
  const version = ownerVersion, attachmentId = deleting.value.id
  saving.value = true; actionError.value = ''
  try { await $fetch(`/api/attachments/${attachmentId}`, { method: 'DELETE', query: { confirm: true }, retry: 0 }); if (version === ownerVersion) { deleteOpen.value = false; await load() } }
  catch (cause) { if (version === ownerVersion) actionError.value = errorMessage(cause) }
  finally { if (version === ownerVersion) saving.value = false }
}
function cancelOwner() { ownerVersion++; uploadController?.abort(); uploadController = undefined; listRequest.cancel() }
watch(() => props.pointId, () => {
  cancelOwner(); items.value = []; error.value = ''; busy.value = false; uploadName.value = ''
  editing.value = null; deleting.value = null; editOpen.value = false; deleteOpen.value = false; saving.value = false; actionError.value = ''
  void load()
}, { immediate: true, flush: 'sync' })
onBeforeUnmount(cancelOwner)
</script>
<template><section class="panel"><div class="panel-head"><h2 class="section-title">点位附件</h2><UButton label="添加附件" icon="i-lucide-paperclip" color="neutral" variant="outline" :loading="busy" :disabled="busy" @click="input?.click()" /><input ref="input" type="file" multiple accept=".pdf,.doc,.docx,.xls,.xlsx,.jpg,.jpeg,.png,.txt,.zip" class="sr-only" data-testid="attachment-file" :disabled="busy" @change="upload" /></div>
<div class="panel-body space-y-2"><p class="text-xs text-muted">保存原文件，单个不超过 25 MB；包含在完整备份中。</p><p v-if="busy" role="status" class="text-sm">正在上传 {{ uploadName }}</p><UAlert role="alert" v-if="error" :description="error" color="error" class="whitespace-pre-wrap" />
<ul v-if="items.length" class="divide-y divide-[var(--divider)]"><li v-for="item in items" :key="item.id" class="py-3" data-testid="attachment-row"><div class="flex min-w-0 items-start justify-between gap-2"><div class="min-w-0 flex-1"><p class="break-words font-medium">{{ item.name }}</p><p class="text-xs text-muted">{{ formatBytes(item.size) }} · {{ item.uploadedAt.slice(0, 10) }}</p><p v-if="item.notes" class="whitespace-pre-wrap text-sm text-muted">{{ item.notes }}</p></div><UDropdownMenu :items="[[{ label: '编辑名称与备注', onSelect: () => edit(item) }, { label: '删除附件', color: 'error', onSelect: () => { deleting = item; actionError = ''; deleteOpen = true } }]]"><UButton icon="i-lucide-ellipsis" color="neutral" variant="ghost" :aria-label="`${item.name}操作`" /></UDropdownMenu></div><div class="mt-1 flex gap-2"><UButton v-if="item.canPreview" :to="`/api/attachments/${item.id}?preview=true`" target="_blank" external label="预览" color="neutral" variant="link" /><UButton :to="`/api/attachments/${item.id}`" external download label="下载原文件" color="neutral" variant="link" data-testid="attachment-download" /></div></li></ul><EmptyState v-else-if="!busy" title="暂无附件" icon="i-lucide-paperclip" /></div></section>
<UModal v-model:open="editOpen" title="编辑附件" :dismissible="!saving" :close="!saving"><template #body><div class="space-y-3"><UAlert role="alert" v-if="actionError" :description="actionError" color="error" /><UFormField label="文件名称" required><UInput v-model="form.name" :maxlength="200" class="w-full" :disabled="saving" /></UFormField><UFormField label="备注"><UTextarea v-model="form.notes" :maxlength="2000" class="w-full" :disabled="saving" /></UFormField></div></template><template #footer><UButton label="取消" color="neutral" variant="outline" :disabled="saving" @click="editOpen = false" /><UButton label="保存" :loading="saving" @click="save" /></template></UModal>
<UModal v-model:open="deleteOpen" :title="`删除附件「${deleting?.name ?? ''}」？`" description="文件及附件记录将删除，无法撤销" :dismissible="!saving" :close="!saving"><template #body><UAlert role="alert" v-if="actionError" :description="actionError" color="error" /></template><template #footer><UButton label="取消" color="neutral" variant="outline" :disabled="saving" @click="deleteOpen = false" /><UButton label="删除" color="error" :loading="saving" @click="remove" /></template></UModal>
</template>
