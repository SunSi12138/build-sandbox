<script setup lang="ts">
import type {CommunityDto} from '~/types/api'
import {errorMessage} from '~/utils/format'
defineProps<{disabled?:boolean}>()
const emit=defineEmits<{created:[grid:CommunityDto]}>()
const {refresh}=useAppData()
const open=ref(false),name=ref(''),busy=ref(false),error=ref('')
async function save(){if(busy.value)return;if(!name.value.trim()){error.value='请填写网格名称';return}busy.value=true;error.value='';try{const grid=await $fetch<CommunityDto>('/api/communities',{method:'POST',body:{name:name.value.trim(),title:name.value.trim()},retry:0});await refresh();emit('created',grid);open.value=false}catch(e){error.value=errorMessage(e)}finally{busy.value=false}}
</script>
<template><UButton label="添加网格" variant="link" :disabled="disabled" @click="name='';error='';open=true" /><UModal v-model:open="open" title="添加网格" :dismissible="!busy" :close="!busy"><template #body><form id="quick-grid-form" class="space-y-3" @submit.prevent="save"><UFormField label="网格名称" required><UInput v-model="name" placeholder="例如：第一网格" maxlength="100" class="w-full" :disabled="busy" autofocus /></UFormField><UAlert v-if="error" color="error" :description="error" /></form></template><template #footer><UButton label="取消" color="neutral" variant="outline" :disabled="busy" @click="open=false" /><UButton type="submit" form="quick-grid-form" label="保存网格" :loading="busy" /></template></UModal></template>
