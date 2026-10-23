<script setup lang="ts">
import type { FormError } from '@nuxt/ui'
import type { PointDetailDto, PointDto, PointWriteRequest, PhotoUploadDto, Frequency } from '~/types/api'
import { coordinatesText, errorMessage, frequencyOptions, parseCoordinates } from '~/utils/format'
const open = defineModel<boolean>('open', { default: false })
const props = defineProps<{ point?: PointDto | null }>()
const emit = defineEmits<{ saved: [point: PointDto] }>()
const { bootstrap, load } = useAppData()
const overlayForm = useState<boolean>('street-eco-overlay-form', () => false)
const saving = ref(false), loading = ref(false), error = ref(''), photosBusy = ref(false)
interface PointState { name: string; communityId: string; typeId: string; address: string; coordinates: string; contact: string; phone: string; frequency: Frequency; notes: string; disabled: boolean; photos: PhotoUploadDto[]; tagIds: string[] }
const defaults = (): PointState => ({ name: '', communityId: '', typeId: '', address: '', coordinates: '', contact: '', phone: '', frequency: 'irregular', notes: '', disabled: false, photos: [], tagIds: [] })
const state = reactive<PointState>(defaults())
const communities = computed(() => (bootstrap.value?.communities ?? []).map(item => ({ label: item.name, value: item.id })))
const types = computed(() => (bootstrap.value?.pointTypes ?? []).map(item => ({ label: item.name, value: item.id })))
watch(open, async value => {
  overlayForm.value = value
  if (!value) return
  error.value = ''; loading.value = true
  const point = props.point
  Object.assign(state, point ? { ...point, coordinates: coordinatesText(point.longitude, point.latitude), photos: [], tagIds: point.tags.map(tag => tag.id) } : defaults())
  try {
    await load()
    if (point) state.photos = (await $fetch<PointDetailDto>(`/api/points/${point.id}`)).photos?.map(photo => ({ id: photo.id, width: photo.width, height: photo.height })) ?? []
  } catch (value) { error.value = errorMessage(value) }
  finally { loading.value = false }
})
function validate(value: PointState): FormError[] {
  const errors: FormError[] = []
  if (!value.name.trim()) errors.push({ name: 'name', message: '请填写点位名称' })
  if (!value.communityId) errors.push({ name: 'communityId', message: '请选择所属社区' })
  if (!value.typeId) errors.push({ name: 'typeId', message: '请选择点位类型' })
  if (!value.address.trim()) errors.push({ name: 'address', message: '请填写详细地址' })
  const coordinates = parseCoordinates(value.coordinates)
  if (coordinates.error) errors.push({ name: 'coordinates', message: coordinates.error })
  return errors
}
async function save() {
  if (loading.value || saving.value || photosBusy.value || validate(state).length) return
  saving.value = true; error.value = ''
  const { longitude, latitude } = parseCoordinates(state.coordinates)
  const body: PointWriteRequest = { name: state.name, communityId: state.communityId, typeId: state.typeId, address: state.address, longitude, latitude, contact: state.contact, phone: state.phone, frequency: props.point?.temporary ? 'irregular' : state.frequency, notes: state.notes, disabled: state.disabled, temporary: props.point?.temporary ?? false, photoIds: state.photos.map(photo => photo.id), tagIds: state.tagIds }
  try { const point = await $fetch<PointDto>(props.point ? `/api/points/${props.point.id}` : '/api/points', { method: props.point ? 'PUT' : 'POST', body, retry: 0 }); emit('saved', point); open.value = false }
  catch (value) { error.value = errorMessage(value) }
  finally { saving.value = false }
}
onBeforeUnmount(() => { overlayForm.value = false })
</script>
<template>
  <USlideover v-model:open="open" :title="point ? '编辑点位' : '新增点位'" :dismissible="!saving && !photosBusy" :close="!saving && !photosBusy" :ui="{ content: 'w-full max-w-[480px] max-md:max-w-none' }">
    <template #body>
      <UAlert v-if="error" :description="error" color="error" variant="subtle" class="mb-3" />
      <p v-if="!loading && (!communities.length || !types.length)" class="mb-3 text-sm text-warning">请先维护社区和点位类型</p>
      <UForm id="point-editor-form" :state="state" :disabled="loading || saving" :validate="validate" class="field-grid" @submit="save">
        <UFormField label="名称" name="name" required class="field-full"><UInput v-model="state.name" maxlength="200" class="w-full" data-testid="point-name" /></UFormField>
        <UFormField label="社区" name="communityId" required><USelect v-model="state.communityId" :items="communities" placeholder="请选择" class="w-full" /></UFormField>
        <UFormField label="类型" name="typeId" required><USelect v-model="state.typeId" :items="types" placeholder="请选择" class="w-full" /></UFormField>
        <UFormField label="详细地址" name="address" required class="field-full"><UInput v-model="state.address" maxlength="1000" class="w-full" /></UFormField>
        <UFormField label="经纬度" name="coordinates" class="field-full"><template #label><CoordinateLabel /></template><UInput v-model="state.coordinates" aria-label="经纬度" placeholder="88.893426,29.274331" class="w-full" /></UFormField>
        <UFormField label="联系人" name="contact"><NameTagsInput :disabled="loading || saving" v-model="state.contact" maxlength="100" class="w-full" /></UFormField>
        <UFormField label="电话" name="phone"><UInput v-model="state.phone" type="tel" maxlength="100" class="w-full" /></UFormField>
        <UFormField label="排查频次" name="frequency" class="field-full"><USelect v-model="state.frequency" :items="frequencyOptions" :disabled="point?.temporary" class="w-full" /></UFormField>
        <UFormField label="标签" class="field-full"><TagPicker v-model="state.tagIds" :disabled="loading || saving" /></UFormField>
        <UFormField label="备注" name="notes" class="field-full"><UTextarea v-model="state.notes" :rows="3" maxlength="8000" class="w-full" /></UFormField>
        <UFormField label="现场图片" class="field-full"><PhotoUploader v-model="state.photos" phase="point" :max="9" :quality="bootstrap?.settings.jpegQuality" :disabled="saving || loading" @busy="photosBusy = $event" /></UFormField>
        <UFormField v-if="point" label="状态" name="disabled" class="field-full"><div class="check-row"><UCheckbox v-model="state.disabled" label="停用" /></div></UFormField>
      </UForm>
    </template>
    <template #footer><span v-if="photosBusy" class="mr-auto text-xs text-muted">照片上传中</span><UButton label="取消" color="neutral" variant="outline" :disabled="saving || photosBusy" @click="open = false" /><UButton type="submit" form="point-editor-form" label="保存" :loading="saving" :disabled="loading || photosBusy || !communities.length || !types.length" data-testid="point-save" /></template>
  </USlideover>
</template>
