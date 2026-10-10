<script setup lang="ts">
import type { FormError } from '@nuxt/ui'
import type { PointDto, PointWriteRequest } from '~/types/api'
import { errorMessage, frequencyOptions } from '~/utils/format'

const open = defineModel<boolean>('open', { default: false })
const props = defineProps<{ point?: PointDto | null }>()
const emit = defineEmits<{ saved: [point: PointDto] }>()
const { bootstrap, load } = useAppData()
const saving = ref(false)
const loading = ref(false)
const error = ref('')
type PointFormState = Omit<PointWriteRequest, 'contact' | 'phone' | 'notes'> & { contact: string; phone: string; notes: string }
const state = reactive<PointFormState>({ name: '', communityId: '', typeId: '', address: '', contact: '', phone: '', frequency: 'irregular', notes: '', disabled: false })
const communities = computed(() => (bootstrap.value?.communities ?? []).map(item => ({ label: item.name, value: item.id })))
const types = computed(() => (bootstrap.value?.pointTypes ?? []).map(item => ({ label: item.name, value: item.id })))
watch(open, async (value) => {
  if (!value) return
  error.value = ''
  Object.assign(state, props.point ? {
    name: props.point.name, communityId: props.point.communityId, typeId: props.point.typeId, address: props.point.address,
    contact: props.point.contact, phone: props.point.phone, frequency: props.point.frequency, notes: props.point.notes, disabled: props.point.disabled
  } : { name: '', communityId: '', typeId: '', address: '', contact: '', phone: '', frequency: 'irregular', notes: '', disabled: false })
  loading.value = true
  try { await load() } catch (value) { error.value = errorMessage(value) }
  finally { loading.value = false }
})
function validate(value: PointWriteRequest): FormError[] {
  const errors: FormError[] = []
  if (!value.name.trim()) errors.push({ name: 'name', message: '请填写点位名称' })
  if (!value.communityId) errors.push({ name: 'communityId', message: '请选择所属社区' })
  if (!value.typeId) errors.push({ name: 'typeId', message: '请选择点位类型' })
  if (!value.address.trim()) errors.push({ name: 'address', message: '请填写详细地址' })
  return errors
}
async function save() {
  saving.value = true
  error.value = ''
  try {
    const point = await $fetch<PointDto>(props.point ? `/api/points/${props.point.id}` : '/api/points', { method: props.point ? 'PUT' : 'POST', body: state })
    emit('saved', point)
    open.value = false
  } catch (value) { error.value = errorMessage(value) }
  finally { saving.value = false }
}
</script>

<template>
  <UModal v-model:open="open" :title="point ? '编辑点位' : '新增点位'" description="完善点位信息，后续排查时可以直接选择。" :dismissible="!saving" :close="!saving" :ui="{ content: 'sm:max-w-2xl' }">
    <template #body>
      <UAlert v-if="error" :description="error" color="error" variant="subtle" icon="i-lucide-circle-alert" class="mb-5" />
      <UAlert v-if="!loading && (!communities.length || !types.length)" title="请先维护社区和点位类型" description="基础字典为空，完善后就可以新增点位。" color="warning" variant="subtle" class="mb-5"><template #actions><UButton to="/settings" label="前往设置" color="warning" variant="soft" @click="open = false" /></template></UAlert>
      <UForm id="point-editor-form" :state="state" :validate="validate" class="field-grid" @submit="save">
        <UFormField label="点位名称" name="name" required class="md:col-span-2"><UInput v-model="state.name" placeholder="例如：建设路便民餐饮店" maxlength="200" class="w-full" autofocus /></UFormField>
        <UFormField label="所属社区" name="communityId" required><USelect v-model="state.communityId" :items="communities" placeholder="请选择所属社区" class="w-full" :loading="loading" /></UFormField>
        <UFormField label="点位类型" name="typeId" required><USelect v-model="state.typeId" :items="types" placeholder="请选择点位类型" class="w-full" :loading="loading" /></UFormField>
        <UFormField label="详细地址" name="address" required class="md:col-span-2"><UInput v-model="state.address" placeholder="填写道路、门牌号或具体位置" maxlength="1000" class="w-full" /></UFormField>
        <UFormField label="联系人" name="contact"><UInput v-model="state.contact" maxlength="100" placeholder="选填" class="w-full" /></UFormField>
        <UFormField label="联系电话" name="phone"><UInput v-model="state.phone" type="tel" maxlength="100" placeholder="选填" class="w-full" /></UFormField>
        <UFormField label="排查频次" name="frequency" description="按自然周、月、季度提醒"><USelect v-model="state.frequency" :items="frequencyOptions" class="w-full" /></UFormField>
        <UFormField v-if="point" label="点位状态" name="disabled"><UCheckbox v-model="state.disabled" label="停用此点位" description="停用后不再提醒，历史资料保留" class="pt-3" /></UFormField>
        <UFormField label="备注" name="notes" class="md:col-span-2"><UTextarea v-model="state.notes" :rows="3" placeholder="其他需要记下的信息（选填）" maxlength="8000" class="w-full" /></UFormField>
      </UForm>
    </template>
    <template #footer><div class="flex w-full justify-end gap-3"><UButton label="取消" color="neutral" variant="outline" :disabled="saving" @click="open = false" /><UButton type="submit" form="point-editor-form" label="保存点位" icon="i-lucide-check" :loading="saving" :disabled="loading || !communities.length || !types.length" data-testid="point-save" /></div></template>
  </UModal>
</template>
