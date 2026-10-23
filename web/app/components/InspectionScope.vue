<script setup lang="ts">
import type { CommunityRecord, UseData, InspectionScopeValue } from '~/types/community'
import { errorMessage } from '~/utils/format'
const props = defineProps<{ pointId: string; disabled?: boolean }>()
const value = defineModel<InspectionScopeValue>({ required: true })
const types = ref<CommunityRecord<{ items: string[] }>[]>([]), uses = ref<CommunityRecord<UseData>[]>([]), error = ref('')
const typeOptions = computed(() => types.value.map(x => ({ label: x.name, value: x.id })))
const useOptions = computed(() => [{ label: '整个场所', value: '' }, ...uses.value.map(x => ({ label: x.name + (x.data.state === '已结束' ? '（已结束）' : ''), value: x.id }))])
const results = [{label:'正常',value:'normal'},{label:'有问题',value:'issues'},{label:'不适用',value:'na'},{label:'未检查',value:'unchecked'}]
async function loadUses() { try { uses.value = props.pointId ? await $fetch('/api/records/use', { query: { parentId: props.pointId } }) : []; if (value.value.useId && !uses.value.some(x => x.id === value.value.useId)) value.value.useId = '' } catch(e) { error.value = errorMessage(e) } }
function selectType() { const selected = types.value.find(x => x.id === value.value.inspectionTypeId); value.value.checklist = (selected?.data.items ?? []).map(name => ({ name, result: 'unchecked', notes: '' })) }
watch(() => props.pointId, loadUses, { immediate: true })
watch([types, () => value.value.inspectionTypeId], () => { if (value.value.inspectionTypeId && !value.value.checklist.length) selectType() })
onMounted(async () => { try { types.value = await $fetch('/api/records/inspection-type'); if (value.value.inspectionTypeId && !value.value.checklist.length) selectType() } catch(e) { error.value = errorMessage(e) } })
</script>
<template><section class="form-section"><h2>检查内容</h2><UAlert v-if="error" :description="error" color="error" /><div class="field-grid"><UFormField label="检查类型" required><USelect v-model="value.inspectionTypeId" :items="typeOptions" placeholder="请选择" class="w-full" :disabled="disabled" @update:model-value="selectType" /></UFormField><UFormField v-if="pointId && (uses.length || value.useId)" label="检查对象"><USelect v-model="value.useId" :items="useOptions" class="w-full" :disabled="disabled" /></UFormField></div><div v-for="(item,index) in value.checklist" :key="index" class="check-item"><span>{{ item.name }}</span><USelect v-model="item.result" :items="results" :disabled="disabled" /><UInput v-if="item.result === 'issues' || item.result === 'unchecked'" v-model="item.notes" placeholder="情况说明" class="check-note" :disabled="disabled" /></div></section></template>
<style scoped>.check-item{display:grid;grid-template-columns:1fr 120px;gap:8px;align-items:center;margin-top:12px}.check-note{grid-column:1/-1}</style>
