<script setup lang="ts">
import { errorMessage, todayLocal as today } from '~/utils/format'
import { downloadWord } from '~/utils/wordDownload'
interface Row { id:string; name:string; places:number; checkedPlaces:number; inspections:number; incomplete:number; issues:number; closed:number; overdue:number }
interface Summary { from:string; to:string; total:Row; grids:Row[]; types:Row[] }
const {bootstrap,load}=useAppData()
const from=ref(today().slice(0,8)+'01'),to=ref(today()),communityId=ref(''),busy=ref(false),error=ref(''),data=ref<Summary|null>(null)
const grids=computed(()=>[{label:'全部网格',value:'all'},...(bootstrap.value?.communities??[]).map(x=>({label:x.name,value:x.id}))])
const filters=computed(()=>({from:from.value,to:to.value,communityId:communityId.value}))
const signature=computed(()=>JSON.stringify(filters.value)),queried=ref('')
const changed=computed(()=>signature.value!==queried.value)
let sequence=0
async function query(){
 const request=++sequence,key=signature.value,params={...filters.value};error.value=''
 if(params.from&&params.to&&params.from>params.to){error.value='开始日期不能晚于结束日期';busy.value=false;return}
 busy.value=true
 try{const result=await $fetch<Summary>('/api/statistics',{query:params});if(request===sequence){data.value=result;queried.value=key}}
 catch(e){if(request===sequence){queried.value='';error.value=errorMessage(e)}}finally{if(request===sequence)busy.value=false}
}
async function download(format:string){if(busy.value||changed.value||!data.value)return;busy.value=true;error.value='';try{await downloadWord(`/api/reports/statistics/${format}?${new URLSearchParams({from:from.value,to:to.value,communityId:communityId.value})}`,`工作统计.${format}`)}catch(e){error.value=errorMessage(e)}finally{busy.value=false}}
onMounted(async()=>{try{await load();await query()}catch(e){error.value=errorMessage(e)}})
onBeforeUnmount(()=>{sequence++})
const communityIdOption=useAllOption(communityId)
</script>
<template><div class="page-stack"><PageHeader title="工作统计" back="/reports" /><section class="panel panel-body space-y-3"><div class="field-grid"><UFormField label="开始日期"><UInput v-model="from" type="date" class="w-full" /></UFormField><UFormField label="结束日期"><UInput v-model="to" type="date" class="w-full" /></UFormField><UFormField label="网格"><USelect v-model="communityIdOption" :items="grids" class="w-full" /></UFormField></div><div class="flex gap-2 flex-wrap"><UButton label="查询" :loading="busy" @click="query" /><UButton label="导出 Excel" variant="outline" :disabled="changed||busy" @click="download('xlsx')" /><UButton label="导出 Word" variant="outline" :disabled="changed||busy" @click="download('docx')" /></div></section><UAlert v-if="error" :description="error" color="error" /><template v-if="data"><div class="stats-grid"><div v-for="card in [{label:'检查次数',value:data.total.inspections},{label:'发现问题',value:data.total.issues},{label:'已完成整改',value:data.total.closed},{label:'尚未完成且超期',value:data.total.overdue}]" :key="card.label" class="stat-block"><span>{{card.label}}</span><strong class="stat-number">{{card.value}}</strong></div></div><p class="text-sm text-muted">按检查日期统计。整改数量为这些检查发现问题的当前状态。已查场所含临时地点，未完成检查不计入。<span v-if="changed">条件已改变，请查询。</span></p><section v-for="group in [{name:'按网格',rows:data.grids},{name:'按检查类型',rows:data.types}]" :key="group.name" class="panel"><div class="panel-head"><strong>{{group.name}}</strong></div><div class="overflow-auto"><table class="w-full text-sm"><thead><tr><th>名称</th><th v-if="group.name==='按网格'">在用场所</th><th>已查场所</th><th>检查次数</th><th>未完成检查</th><th>问题数</th><th>已完成整改</th><th>超期</th></tr></thead><tbody><tr v-for="row in group.rows" :key="row.id"><td>{{row.name}}</td><td v-if="group.name==='按网格'">{{row.places}}</td><td>{{row.checkedPlaces}}</td><td>{{row.inspections}}</td><td>{{row.incomplete}}</td><td>{{row.issues}}</td><td>{{row.closed}}</td><td>{{row.overdue}}</td></tr></tbody></table></div></section></template></div></template>
<style scoped>th,td{padding:12px;text-align:left;white-space:nowrap;border-bottom:1px solid var(--border)}</style>
