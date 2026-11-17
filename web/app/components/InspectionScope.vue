<script setup lang="ts">
import type { CommunityRecord, UseData, InspectionScopeValue } from '~/types/community'
import { errorMessage } from '~/utils/format'
const props = defineProps<{ pointId: string; disabled?: boolean }>()
const value = defineModel<InspectionScopeValue>({ required: true })
const emit=defineEmits<{busy:[value:boolean];failed:[value:boolean]}>()
const types = ref<CommunityRecord<{ items: string[]; fields?: import('~/types/community').PurposeField[] }>[]>([]), uses = ref<CommunityRecord<UseData>[]>([]), error = ref(''), usesLoading = ref(false)
const typeOptions = computed(() => types.value.map(x => ({ label: x.name, value: x.id })))
const extraFields=computed(()=>types.value.find(x=>x.id===value.value.inspectionTypeId)?.data.fields??[])
const useOptions = computed(() => [{ label: '整个场所', value: 'all' }, ...uses.value.filter(x=>x.data.state!=='已结束'||x.id===value.value.useId).map(x => ({ label: [x.data.position,x.name].filter(Boolean).join(' · ') + (x.data.state === '已结束' ? '（已结束）' : ''), value: x.id }))])
const useOption=useAllOption(computed({get:()=>value.value.useId,set:(id:string)=>{value.value.useId=id}}))
const results = [{label:'正常',value:'normal'},{label:'有问题',value:'issues'},{label:'不适用',value:'na'},{label:'未检查',value:'unchecked'}]
let requestSequence=0
async function loadUses() {
  const request=++requestSequence;usesLoading.value=true;error.value='';uses.value=[]
  try { const rows=props.pointId?await $fetch<CommunityRecord<UseData>[]>('/api/records/use',{query:{parentId:props.pointId}}):[];if(request!==requestSequence)return;uses.value=rows;if(value.value.useId&&!rows.some(x=>x.id===value.value.useId))value.value.useId='' }
  catch(e){if(request===requestSequence)error.value=errorMessage(e)}finally{if(request===requestSequence)usesLoading.value=false}
}
function selectType(id=value.value.inspectionTypeId) {
  if(id!==value.value.inspectionTypeId&&value.value.checklist.some(x=>x.result!=='unchecked'||x.notes.trim())&&!window.confirm('切换检查类型会清空已填的项目结果，继续吗？'))return
  if(id!==value.value.inspectionTypeId){value.value.extraValues={};value.value.extraPhotoIds=[]}
  value.value.inspectionTypeId=id
  const selected=types.value.find(x=>x.id===id)
  value.value.checklist=(selected?.data.items??[]).map(name=>({name,result:'unchecked',notes:''}))
}
function markUncheckedNormal(){for(const item of value.value.checklist)if(item.result==='unchecked'&&!item.notes.trim())item.result='normal'}
watch(() => props.pointId, loadUses, { immediate: true })
watch([types, () => value.value.inspectionTypeId], () => { if (value.value.inspectionTypeId && !value.value.checklist.length) selectType() })
onMounted(async () => { try { types.value = await $fetch('/api/records/inspection-type'); if (value.value.inspectionTypeId && !value.value.checklist.length) selectType() } catch(e) { error.value = errorMessage(e) } })
</script>
<template><section class="form-section"><h2>检查内容</h2><UAlert v-if="error" :description="error" color="error" /><div class="field-grid"><UFormField label="检查类型" name="scope.inspectionTypeId" required><USelect :model-value="value.inspectionTypeId" :items="typeOptions" placeholder="请选择" class="w-full" :disabled="disabled" @update:model-value="selectType" /></UFormField><UFormField v-if="pointId && (usesLoading || uses.length || value.useId)" label="检查对象"><USelect v-model="useOption" :items="useOptions" class="w-full" :loading="usesLoading" :disabled="disabled||usesLoading" /></UFormField></div><div v-if="value.checklist.length" class="mt-3 flex items-center justify-between gap-2"><span class="text-sm text-muted">已填 {{ value.checklist.filter(x=>x.result!=='unchecked').length }} / {{ value.checklist.length }} 项</span><UButton label="未填项设为正常" size="sm" color="neutral" variant="outline" :disabled="disabled||!value.checklist.some(x=>x.result==='unchecked'&&!x.notes.trim())" @click="markUncheckedNormal" /></div><div v-for="(item,index) in value.checklist" :key="index" class="check-item"><span>{{ item.name }}</span><USelect v-model="item.result" :items="results" :disabled="disabled" /><UInput v-if="item.result === 'issues' || item.result === 'unchecked'" v-model="item.notes" placeholder="情况说明" class="check-note" :disabled="disabled" /></div><ExtraFieldValues v-if="extraFields.length" v-model="value.extraValues" v-model:photo-ids="value.extraPhotoIds" :fields="extraFields" phase="site" :disabled="disabled" @busy="emit('busy',$event)" @failed="emit('failed',$event)" /></section></template>
<style scoped>.check-item{display:grid;grid-template-columns:1fr 120px;gap:8px;align-items:center;margin-top:12px}.check-note{grid-column:1/-1}</style>
