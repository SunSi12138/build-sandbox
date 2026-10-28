<script setup lang="ts">
import type { MatterDto, MatterWriteRequest } from '~/types/api'
import type { FormError } from '@nuxt/ui'
import { errorMessage, todayLocal, queryText } from '~/utils/format'
import { matterSources } from '~/utils/matters'
const props = defineProps<{ initial?: MatterDto | null }>()
const emit = defineEmits<{ saved: [matter: MatterDto] }>()
const route = useRoute()
const { bootstrap, load } = useAppData()
const state = reactive({ title: '', source: 'public', receivedDate: todayLocal(), content: '', pointId: '', location: '', deadline: '', handler: '', referenceNumber: '', contact: '', phone: '', tagIds: [] as string[] })
const saving = ref(false), loading = ref(true), error = ref(''), completed = ref(false), leaveOpen = ref(false)
let original = '', resolveLeave: ((value: boolean) => void) | undefined
const dirty = computed(() => !loading.value && JSON.stringify(state) !== original)
watch(() => props.initial, value => {
  if (value) Object.assign(state, {
    title: value.title, source: value.source, receivedDate: value.receivedDate, content: value.content,
    pointId: value.pointId ?? '', location: value.location, deadline: value.deadline ?? '', handler: value.handler,
    referenceNumber: value.referenceNumber, contact: value.contact, phone: value.phone, tagIds: value.tags.map(tag => tag.id)
  })
}, { immediate: true })
onMounted(async () => { window.addEventListener('beforeunload', beforeUnload); try { await load(); if (!props.initial) { state.handler = bootstrap.value?.settings.defaultInspector ?? ''; state.pointId = queryText(route.query.pointId) } original = JSON.stringify(state) } catch (cause) { error.value = errorMessage(cause) } finally { loading.value = false } })
function validate(): FormError[] {
  const errors: FormError[] = []
  if (!state.title.trim()) errors.push({ name: 'title', message: '请填写事项标题' })
  if (!state.content.trim()) errors.push({ name: 'content', message: '请填写事项内容' })
  if (!state.receivedDate || state.receivedDate > todayLocal()) errors.push({ name: 'receivedDate', message: '接收日期不能晚于今天' })
  if (state.deadline && state.deadline < state.receivedDate) errors.push({ name: 'deadline', message: '办理期限不能早于接收日期' })
  return errors
}
async function save() {
  if (saving.value || loading.value || validate().length) return
  saving.value = true; error.value = ''
  const body: MatterWriteRequest = { ...state, pointId: state.pointId || null, deadline: state.deadline || null }
  try { const matter = await $fetch<MatterDto>(props.initial ? `/api/matters/${props.initial.id}` : '/api/matters', { method: props.initial ? 'PUT' : 'POST', body, retry: 0 }); completed.value = true; emit('saved', matter) }
  catch (cause) { error.value = errorMessage(cause) } finally { saving.value = false }
}
function finishLeave(value: boolean) { leaveOpen.value = false; resolveLeave?.(value); resolveLeave = undefined }
function guardLeave() { if (saving.value) return false; if (completed.value || !dirty.value) return true; leaveOpen.value = true; return new Promise<boolean>(resolve => { resolveLeave = resolve }) }
function beforeUnload(event: BeforeUnloadEvent) { if (!completed.value && (dirty.value || saving.value)) { event.preventDefault(); event.returnValue = '' } }
onBeforeRouteLeave(guardLeave); onBeforeRouteUpdate(guardLeave)
onBeforeUnmount(() => { window.removeEventListener('beforeunload', beforeUnload); resolveLeave?.(false) })
</script>
<template><UForm id="matter-form" :state="state" :validate="validate" class="space-y-3" @submit="save"><UAlert role="alert" v-if="error" :description="error" color="error" />
<div class="form-sections"><section class="form-section"><h2>事项信息</h2><div class="field-grid">
<UFormField label="事项标题" name="title" required class="field-full"><UInput v-model="state.title" :maxlength="200" class="w-full" :disabled="saving || loading" data-testid="matter-title" /></UFormField>
<UFormField label="来源" name="source" required><USelect v-model="state.source" :items="matterSources" class="w-full" :disabled="saving || loading" data-testid="matter-source" /></UFormField>
<UFormField label="接收日期" name="receivedDate" required><UInput v-model="state.receivedDate" type="date" :max="todayLocal()" class="w-full" :disabled="saving || loading" /></UFormField>
<UFormField label="事项内容" name="content" required class="field-full"><UTextarea v-model="state.content" :rows="5" :maxlength="12000" class="w-full" :disabled="saving || loading" data-testid="matter-content" /></UFormField>
<UFormField label="关联点位" class="field-full"><PointSelect v-model="state.pointId" :disabled="saving || loading" /></UFormField>
<UFormField label="涉及地点" class="field-full"><UInput v-model="state.location" :maxlength="1000" class="w-full" :disabled="saving || loading" /></UFormField>
<UFormField label="办理期限" name="deadline"><UInput v-model="state.deadline" type="date" :min="state.receivedDate" class="w-full" :disabled="saving || loading" /></UFormField>
<UFormField label="经办人"><NameTagsInput v-model="state.handler" :maxlength="200" :disabled="saving || loading" /></UFormField>
<UFormField label="交办编号" class="field-full"><UInput v-model="state.referenceNumber" :maxlength="200" class="w-full" :disabled="saving || loading" /></UFormField>
<UFormField label="联系人"><UInput v-model="state.contact" :maxlength="100" class="w-full" :disabled="saving || loading" /></UFormField>
<UFormField label="联系方式"><UInput v-model="state.phone" :maxlength="100" type="tel" class="w-full" :disabled="saving || loading" /></UFormField>
<UFormField label="标签" class="field-full"><TagPicker v-model="state.tagIds" :disabled="saving || loading" /></UFormField>
</div></section></div><div class="form-actions"><UButton :to="initial ? `/matters/${initial.id}` : '/matters'" label="取消" color="neutral" variant="outline" :disabled="saving" /><UButton type="submit" label="保存事项" :loading="saving" :disabled="loading" data-testid="matter-save" /></div></UForm>
<UModal v-model:open="leaveOpen" title="放弃未保存的事项？" description="本次输入不会保存" :dismissible="false" :close="false"><template #footer><UButton label="继续编辑" color="neutral" variant="outline" @click="finishLeave(false)" /><UButton label="离开" color="error" @click="finishLeave(true)" /></template></UModal>
</template>
