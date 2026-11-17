<script setup lang="ts">
import type { CommunityDto } from '~/types/api'
import type { GeoShape } from '~/types/community'
import { errorMessage } from '~/utils/format'
const open=defineModel<boolean>('open',{default:false})
const props=defineProps<{shape?:GeoShape|null;initialParentId?:string;record?:CommunityDto|null}>(),emit=defineEmits<{saved:[grid:CommunityDto]}>()
const {refresh}=useAppData()
const name=ref(''),parentId=ref(''),color=ref('#64748b'),busy=ref(false),error=ref('')
watch(open,value=>{if(value){name.value=props.record?.name??'';parentId.value=props.record?.parentId??props.initialParentId??'';color.value=props.record?.color??'#64748b';error.value=''}})
async function save(){if(busy.value||!name.value.trim())return;busy.value=true;error.value='';try{const grid=await $fetch<CommunityDto>(props.record?`/api/communities/${props.record.id}`:'/api/communities',{method:props.record?'PUT':'POST',body:{name:name.value.trim(),title:name.value.trim(),parentId:parentId.value,color:color.value,shape:props.record?undefined:props.shape},retry:0});await refresh();emit('saved',grid);open.value=false}catch(e){error.value=errorMessage(e)}finally{busy.value=false}}
</script>
<template><UModal v-model:open="open" :title="record?'编辑网格':'新建网格'" :dismissible="!busy" :close="!busy"><template #body><form id="new-map-grid" class="space-y-4" @submit.prevent="save"><UFormField label="网格名称" required><UInput v-model="name" class="w-full" maxlength="100" :disabled="busy" autofocus /></UFormField><UFormField label="上级网格"><GridTreeSelect v-model="parentId" allow-root :exclude-id="record?.id" :disabled="busy" placeholder="选择上级网格" /></UFormField><UFormField label="网格颜色"><MapColorPicker v-model="color" :disabled="busy" /></UFormField><UAlert v-if="error" :description="error" color="error" /></form></template><template #footer><UButton label="取消" variant="outline" color="neutral" :disabled="busy" @click="open=false" /><UButton label="保存网格" type="submit" form="new-map-grid" :loading="busy" :disabled="!name.trim()" /></template></UModal></template>
