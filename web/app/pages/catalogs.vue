<script setup lang="ts">
import type { CommunityRecord, PurposeField } from '~/types/community'
import { errorMessage } from '~/utils/format'
const kind = ref('inspection-type'), items = ref<CommunityRecord[]>([]), error = ref(''), busy = ref(false), open = ref(false)
const current = ref<CommunityRecord | null>(null), name = ref(''), lines = ref(''), phone = ref('')
const editingKind=ref('inspection-type')
let loadVersion=0
const fields=ref<PurposeField[]>([])
const {bootstrap,load:loadApp}=useAppData()
const frequency=ref('irregular'),communityId=ref(''),pointTypeId=ref('')
const frequencies=[{label:'不定期',value:'irregular'},{label:'每周',value:'weekly'},{label:'每月',value:'monthly'},{label:'每季度',value:'quarterly'}]
const grids=computed(()=>[{label:'全部网格',value:'all'},...(bootstrap.value?.communities??[]).map(x=>({label:x.name,value:x.id}))])
const pointTypes=computed(()=>[{label:'全部场所类型',value:'all'},...(bootstrap.value?.pointTypes??[]).map(x=>({label:x.name,value:x.id}))])
onMounted(()=>loadApp())
async function load() {
  const version=++loadVersion
  const requestedKind=kind.value
  error.value=''
  try {
    const loaded=await $fetch<CommunityRecord[]>(`/api/records/${requestedKind}`,{query:{all:true}})
    if(version===loadVersion&&requestedKind===kind.value)items.value=loaded
  }catch(e){if(version===loadVersion)error.value=errorMessage(e)}
}
function edit(item?: CommunityRecord) { editingKind.value=kind.value;fields.value=structuredClone(toRaw((item?.data.fields as PurposeField[] | undefined)??[]));current.value = item ?? null; name.value = item?.name ?? ''; lines.value = Array.isArray(item?.data.items) ? item.data.items.join('\n') : ''; phone.value = String(item?.data.phone ?? ''); frequency.value=String(item?.data.frequency??'irregular');communityId.value=String(item?.data.communityId??'');pointTypeId.value=String(item?.data.pointTypeId??'');open.value = true }
async function save(archive = current.value?.archived ?? false) { if (!name.value.trim()) return; busy.value = true; error.value = ''; try { await $fetch(current.value ? `/api/records/${editingKind.value}/${current.value.id}` : `/api/records/${editingKind.value}`, { method: current.value ? 'PUT' : 'POST', body: { name: name.value, parentId: '', revision: current.value?.revision ?? 0, archived: archive, data: editingKind.value === 'staff' ? { phone: phone.value } : { items: lines.value.split('\n').map(x => x.trim()).filter(Boolean),fields:fields.value,frequency:frequency.value,communityId:communityId.value,pointTypeId:pointTypeId.value } }, retry: 0 }); open.value = false; await load() } catch(e) { error.value = errorMessage(e) } finally { busy.value = false } }
watch(kind, load, { immediate: true })
const communityIdOption=useAllOption(communityId)
const pointTypeIdOption=useAllOption(pointTypeId)
</script>
<template><div class="page-stack"><PageHeader title="人员与检查类型" back="/settings"><UButton label="新增" icon="i-lucide-plus" @click="edit()" /></PageHeader><div class="flex gap-2"><UButton label="检查类型" :variant="kind === 'inspection-type' ? 'solid' : 'outline'" @click="kind = 'inspection-type'" /><UButton label="工作人员" :variant="kind === 'staff' ? 'solid' : 'outline'" @click="kind = 'staff'" /></div><UAlert v-if="error" :description="error" color="error" /><div class="panel"><p v-if="!items.length" class="p-4 text-muted">暂无{{ kind==='staff'?'工作人员':'检查类型' }}</p><button v-for="item in items" :key="item.id" class="list-row w-full text-left" @click="edit(item)"><div class="row-body"><strong>{{ item.name }}</strong><p v-if="kind === 'inspection-type'" class="text-sm text-muted">{{ Array.isArray(item.data.items) ? item.data.items.join('、') : '' }}</p><p v-else class="text-sm text-muted">{{ item.data.phone }}</p></div><UBadge v-if="item.archived" color="neutral" label="已停用" /></button></div><UModal v-model:open="open" :title="(current ? '编辑' : '新增')+(editingKind==='staff'?'工作人员':'检查类型')" :dismissible="!busy" :close="!busy"><template #body><div class="space-y-3"><UFormField label="名称" required><UInput v-model="name" class="w-full" /></UFormField><UFormField v-if="editingKind === 'inspection-type'" label="检查项目（每行一项）"><UTextarea v-model="lines" :rows="6" class="w-full" /></UFormField><UFormField v-else label="电话"><UInput v-model="phone" type="tel" class="w-full" /></UFormField><template v-if="editingKind==='inspection-type'"><UFormField label="检查周期"><USelect v-model="frequency" :items="frequencies" class="w-full" /></UFormField><template v-if="frequency!=='irregular'"><UFormField label="适用网格"><USelect v-model="communityIdOption" :items="grids" class="w-full" /></UFormField><UFormField label="适用场所类型"><USelect v-model="pointTypeIdOption" :items="pointTypes" class="w-full" /></UFormField></template><FieldDefinitionEditor v-model="fields" :disabled="busy" /></template><UAlert v-if="error" :description="error" color="error" /></div></template><template #footer><UButton label="取消" color="neutral" variant="outline" :disabled="busy" @click="open=false" /><UButton label="保存" :disabled="!name.trim()" :loading="busy" @click="save()" /><UButton v-if="current" :label="current.archived?'启用':'停用'" color="neutral" variant="outline" :disabled="busy" @click="save(!current!.archived)" /></template></UModal></div></template>
