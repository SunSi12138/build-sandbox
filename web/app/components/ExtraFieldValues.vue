<script setup lang="ts">
import type { PurposeField } from '~/types/community'
import type { PhotoPhase, PhotoUploadDto } from '~/types/api'
const props = defineProps<{ fields: PurposeField[]; phase: PhotoPhase; disabled?: boolean; quality?: number }>()
const values = defineModel<Record<string, string | number>>({ required: true })
const photoIds = defineModel<string[]>('photoIds', { default: () => [] })
const emit = defineEmits<{ busy: [value: boolean]; failed: [value: boolean] }>()
function setValue(id: string, next: string | number) { values.value = { ...values.value, [id]: next } }
function imageModel(id: string): PhotoUploadDto[] {
  const value = values.value[id]
  return typeof value === 'string' && value ? [{ id: value, width: 1, height: 1 }] : []
}
function setImage(id: string, list: PhotoUploadDto[]) {
  const previous = values.value[id]
  const next = list[0]?.id ?? ''
  if (typeof previous === 'string' && previous) photoIds.value = photoIds.value.filter(item => item !== previous)
  if (next) photoIds.value = [...new Set([...photoIds.value, next])]
  setValue(id, next)
}
</script>
<template>
  <div v-if="fields.length" class="field-grid">
    <UFormField v-for="field in fields" :key="field.id" :label="field.label" :required="field.required" :class="{ 'field-full': field.type !== 'number' }">
      <UInput v-if="field.type==='number'" :model-value="values[field.id]" type="number" step="any" class="w-full" :disabled="disabled" @update:model-value="v=>setValue(field.id,Number(v))" />
      <UTextarea v-else-if="field.type==='text' && field.multiline" :model-value="String(values[field.id]??'')" :rows="4" class="w-full" :disabled="disabled" @update:model-value="v=>setValue(field.id,String(v))" />
      <UInput v-else-if="field.type==='text'" :model-value="String(values[field.id]??'')" class="w-full" :maxlength="4000" :disabled="disabled" @update:model-value="v=>setValue(field.id,String(v))" />
      <NameTagsInput v-else-if="field.type==='tags'" :model-value="String(values[field.id]??'')" :maxlength="4000" :disabled="disabled" @update:model-value="v=>setValue(field.id,v)" />
      <PhotoUploader v-else :model-value="imageModel(field.id)" :phase="phase" :max="1" :quality="quality" deferred-removal :disabled="disabled" @update:model-value="list=>setImage(field.id,list)" @busy="emit('busy',$event)" @failed="emit('failed',$event)" />
    </UFormField>
  </div>
</template>