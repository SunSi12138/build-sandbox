<script setup lang="ts">
import type { UseData } from '~/types/community'
import type { PhotoUploadDto } from '~/types/api'
import { todayLocal } from '~/utils/format'
const value = defineModel<UseData>({ required: true })
const name = defineModel<string>('name', { required: true })
defineProps<{ disabled?: boolean; existing?: boolean }>()
const emit = defineEmits<{ busy: [value: boolean]; failed: [value: boolean] }>()
const { purposes, purpose, loadPurposes } = usePurposeCatalog()
const { bootstrap } = useAppData()
const fields = computed(() => purpose(value.value.purposeId)?.data.fields ?? [])
function purposeChanged() { value.value.values = {}; value.value.fieldSnapshot = undefined }
const options = computed(() => purposes.value.filter(x => !x.archived || x.id === value.value.purposeId).map(x => ({ label: `${x.data.icon} ${x.name}${x.archived ? '（已停用）' : ''}`, value: x.id })))
const photos = computed<PhotoUploadDto[]>({ get: () => value.value.photoIds.map(id => ({ id, width: 1, height: 1 })), set: list => { value.value.photoIds = list.map(x => x.id) } })
onMounted(() => { void loadPurposes() })
</script>
<template>
  <div class="space-y-3">
    <div class="field-grid"><UFormField label="用途位置" required><UInput v-model="value.position" placeholder="例如：一楼东侧、后院" class="w-full" :disabled="disabled" /></UFormField><UFormField label="用途分类" required><USelect v-model="value.purposeId" :items="options" placeholder="选择用途" class="w-full" :disabled="disabled" @update:model-value="purposeChanged" /></UFormField></div>
    <UFormField label="店名 / 住户 / 使用名称" required><UInput v-model="name" placeholder="例如：便民商店、张某租住" class="w-full" :disabled="disabled" /></UFormField>
    <div class="field-grid"><UFormField label="联系人"><UInput v-model="value.contact" class="w-full" :disabled="disabled" /></UFormField><UFormField label="联系电话"><UInput v-model="value.phone" type="tel" class="w-full" :disabled="disabled" /></UFormField></div>
    <ExtraFieldValues v-model="value.values" v-model:photo-ids="value.photoIds" :fields="fields" phase="point" :quality="bootstrap?.settings.jpegQuality" :disabled="disabled" @busy="emit('busy',$event)" @failed="emit('failed',$event)" />
    <div class="field-grid"><UFormField label="开始日期"><UInput v-model="value.startDate" type="date" class="w-full" :disabled="disabled" /></UFormField><UFormField label="使用状态"><USelect v-model="value.state" :items="existing ? ['使用中','暂停使用','待核实','已结束'] : ['使用中','暂停使用','待核实']" class="w-full" :disabled="disabled" @update:model-value="v => { value.endDate = v === '已结束' ? (value.endDate || todayLocal()) : '' }" /></UFormField><UFormField v-if="value.state === '已结束'" label="结束日期" required><UInput v-model="value.endDate" type="date" class="w-full" :disabled="disabled" /></UFormField></div>
    <UFormField label="照片"><PhotoUploader v-model="photos" phase="point" :max="20" deferred-removal :disabled="disabled" :quality="bootstrap?.settings.jpegQuality" @busy="emit('busy',$event)" @failed="emit('failed',$event)" /></UFormField>
    <UFormField label="备注"><UTextarea v-model="value.notes" :rows="2" class="w-full" :disabled="disabled" /></UFormField>
  </div>
</template>
