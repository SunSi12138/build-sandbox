<script setup lang="ts">
import {errorMessage} from '~/utils/format'
interface Reminder {pointId:string;pointName:string;communityId:string;communityName:string;typeId:string;typeName:string;periodFrom:string;periodTo:string;lastDate:string|null}
const items=ref<Reminder[]>([]),error=ref(''),busy=ref(true),type=ref(''),search=ref('')
const types=computed(()=>[{label:'全部类型',value:'all'},...Array.from(new Map(items.value.map(x=>[x.typeId,{label:x.typeName,value:x.typeId}])).values())])
const filtered=computed(()=>items.value.filter(x=>(!type.value||x.typeId===type.value)&&(!search.value||(x.pointName+x.communityName).includes(search.value))))
async function load(){busy.value=true;try{items.value=await $fetch('/api/type-reminders')}catch(e){error.value=errorMessage(e)}finally{busy.value=false}}
onMounted(load)
const typeOption=useAllOption(type)
</script>
<template><div class="page-stack"><PageHeader title="专项检查到期" back="/"><UButton to="/catalogs" label="设置周期" variant="outline" /></PageHeader><div class="flex gap-2 flex-wrap"><USelect v-model="typeOption" :items="types" /><UInput v-model="search" placeholder="搜索场所、网格" /></div><PageState :loading="busy" :error="error" @retry="load"><div class="panel"><div v-for="item in filtered" :key="item.pointId+item.typeId" class="list-row"><div class="row-body"><NuxtLink :to="`/points/${item.pointId}`" class="row-title">{{item.pointName}}</NuxtLink><p class="text-sm">{{item.typeName}} · {{item.communityName}}</p><p class="text-sm text-muted">本期 {{item.periodFrom}} 至 {{item.periodTo}} · 上次 {{item.lastDate||'未检查'}}</p></div><UButton label="检查" :to="{path:'/inspections/new',query:{pointId:item.pointId,typeId:item.typeId,returnTo:'/reminders'}}" /></div><EmptyState v-if="!filtered.length" title="暂无待检查场所" /></div></PageState></div></template>
