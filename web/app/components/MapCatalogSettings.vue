<script setup lang="ts">
import type { CommunityRecord, PurposeData, PurposeField } from '~/types/community'
import { errorMessage } from '~/utils/format'
const { purposes, loadPurposes }=usePurposeCatalog()
const error=ref(''),busy=ref(false),open=ref(false),current=ref<CommunityRecord<PurposeData>|null>(null)
const name=ref(''),icon=ref('📌'),fields=ref<PurposeField[]>([])
const deleting=ref<CommunityRecord<PurposeData>|null>(null)
async function load(){try{await loadPurposes()}catch(e){error.value=errorMessage(e)}}
function edit(record?:CommunityRecord<PurposeData>){current.value=record??null;name.value=record?.name??'';icon.value=record?.data.icon??'📌';fields.value=structuredClone(toRaw(record?.data.fields??[]));error.value='';open.value=true}
async function save(){if(!name.value.trim()||!icon.value.trim())return;if(fields.value.some(x=>!x.label.trim())){error.value='请填写每个字段的名称';return}busy.value=true;error.value='';try{await $fetch(current.value?`/api/records/purpose-type/${current.value.id}`:'/api/records/purpose-type',{method:current.value?'PUT':'POST',body:{name:name.value,parentId:'',data:{icon:icon.value,fields:fields.value},revision:current.value?.revision??0,archived:current.value?.archived??false},retry:0});open.value=false;await load()}catch(e){error.value=errorMessage(e)}finally{busy.value=false}}
async function remove(){if(!deleting.value)return;busy.value=true;error.value='';try{await $fetch(`/api/records/purpose-type/${deleting.value.id}`,{method:'DELETE',query:{confirm:'true'},retry:0});deleting.value=null;await load()}catch(e){error.value=errorMessage(e)}finally{busy.value=false}}
onMounted(load)
</script>
<template>
<section class="panel panel-body space-y-4"><UAlert v-if="error&&!open" :description="error" color="error" /><div class="flex justify-between items-center"><h2 class="section-title">用途分类与字段</h2><UButton label="新增用途" icon="i-lucide-plus" @click="edit()" /></div>
<div v-for="item in purposes" :key="item.id" class="flex gap-3 items-center border-b border-default pb-2"><span class="text-2xl">{{ item.data.icon }}</span><span class="flex-1">{{ item.name }}<small v-if="item.data.fields?.length||item.archived" class="block text-muted">{{ item.data.fields?.map(x=>x.label).join('、')||'' }} {{ item.archived?'· 已停用':'' }}</small></span><UButton icon="i-lucide-pencil" :aria-label="`编辑${item.name}`" color="neutral" variant="ghost" :disabled="busy" @click="edit(item)" /><UButton icon="i-lucide-trash-2" :aria-label="`删除${item.name}`" color="error" variant="ghost" :disabled="busy" @click="deleting=item" /></div>
</section>
<UModal v-model:open="open" :title="current?'编辑用途':'新增用途'" :dismissible="!busy" :ui="{content:'max-w-2xl'}"><template #body><div class="space-y-4"><UAlert v-if="error" :description="error" color="error" /><div class="field-grid"><UFormField label="用途名称" required><UInput v-model="name" class="w-full" :disabled="busy" /></UFormField><UFormField label="地图图标（Emoji）" required><UInput v-model="icon" :maxlength="16" class="w-full" :disabled="busy" /></UFormField></div><div class="flex gap-2 flex-wrap"><button v-for="emoji in ['🏪','🏠','🏭','📦','🔑','🏢','🌳','🅿️','🏞️','📌']" :key="emoji" class="text-2xl p-1" :disabled="busy" @click="icon=emoji">{{ emoji }}</button></div><FieldDefinitionEditor v-model="fields" :disabled="busy" /></div></template><template #footer><UButton label="取消" color="neutral" variant="outline" :disabled="busy" @click="open=false" /><UButton label="保存用途" :disabled="!name.trim()||!icon.trim()" :loading="busy" @click="save" /></template></UModal>
<UModal :open="!!deleting" title="删除用途" :dismissible="!busy" @update:open="v=>{if(!v)deleting=null}"><template #body><p>删除「{{ deleting?.name }}」？</p><UAlert v-if="error" :description="error" color="error" /></template><template #footer><UButton label="取消" color="neutral" variant="outline" :disabled="busy" @click="deleting=null" /><UButton label="删除" color="error" :loading="busy" @click="remove" /></template></UModal>
</template>

