<script setup lang="ts">
import type { CommunityRecord, UseData } from '~/types/community'
import { errorMessage } from '~/utils/format'
const props = defineProps<{ pointId: string; record?: CommunityRecord<UseData> | null }>()
const emit = defineEmits<{ saved: [record: CommunityRecord<UseData>]; cancel: [] }>()
const editing = ref(!props.record), name = ref(''), data = ref(emptyUse()), busy = ref(false), uploading = ref(false), failed = ref(false), error = ref('')
const { purpose } = usePurposeCatalog()
watch(() => props.record, record => { name.value = record?.name ?? ''; data.value = record ? { ...emptyUse(), ...structuredClone(toRaw(record.data)), values: structuredClone(toRaw(record.data.values ?? {})) } : emptyUse(); editing.value = !record; error.value = '' }, { immediate: true })
async function save() {
  if (busy.value || uploading.value || failed.value) return
  if (!name.value.trim() || !data.value.position.trim() || !data.value.purposeId) { error.value = '请填写用途位置、选择用途并填写使用名称'; return }
  busy.value = true; error.value = ''
  try { const result = await $fetch<CommunityRecord<UseData>>(props.record ? `/api/records/use/${props.record.id}` : '/api/records/use', { method: props.record ? 'PUT' : 'POST', body: { name: name.value, parentId: props.pointId, data: data.value, revision: props.record?.revision ?? 0, archived: props.record?.archived ?? false }, retry: 0 }); editing.value = false; emit('saved',result) }
  catch(e) { error.value = errorMessage(e) } finally { busy.value = false }
}
</script>
<template>
  <div class="space-y-4">
    <UAlert v-if="error" :description="error" color="error" />
    <template v-if="editing"><UseFields v-model="data" v-model:name="name" :existing="!!record" :disabled="busy" @busy="uploading=$event" @failed="failed=$event" /><div class="flex gap-2"><UButton label="保存使用情况" :loading="busy" :disabled="uploading || failed" @click="save" /><UButton label="取消" color="neutral" variant="outline" :disabled="busy || uploading" @click="record ? editing=false : emit('cancel')" /></div></template>
    <template v-else-if="record"><p class="text-lg font-semibold">{{ purpose(record.data.purposeId)?.data.icon }} {{ record.name }}</p><UBadge>{{ record.data.state }}</UBadge><dl class="detail-grid"><div><dt>用途位置</dt><dd>{{ record.data.position }}</dd></div><div><dt>用途</dt><dd>{{ purpose(record.data.purposeId)?.name }}</dd></div><div><dt>联系人</dt><dd>{{ record.data.contact || '未填写' }}</dd></div><div><dt>电话</dt><dd>{{ record.data.phone || '未填写' }}</dd></div><div v-for="field in (record.data.fieldSnapshot ?? purpose(record.data.purposeId)?.data.fields ?? [])" v-show="record.data.values?.[field.id]!==undefined && record.data.values?.[field.id]!==''" :key="field.id"><dt>{{ field.label }}</dt><dd>{{ record.data.values?.[field.id] }}</dd></div><div v-for="field in ([['startDate','开始日期'],['endDate','结束日期'],['notes','备注']] as const)" v-show="record.data[field[0]]" :key="field[0]"><dt>{{ field[1] }}</dt><dd>{{ record.data[field[0]] }}</dd></div></dl><div class="grid grid-cols-2 gap-2"><a v-for="id in record.data.photoIds" :key="id" :href="`/photos/${id}`" target="_blank"><img :src="`/photos/${id}?size=thumb`" alt="使用情况照片" class="w-full rounded" /></a></div><div class="flex gap-2 flex-wrap"><UButton label="修改使用情况" variant="outline" @click="editing=true" /><UButton v-if="record.data.state !== '已结束'" label="登记检查" :to="`/inspections/new?pointId=${pointId}&useId=${record.id}`" /></div></template>
  </div>
</template>
