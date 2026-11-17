<script setup lang="ts">
import { errorMessage } from '~/utils/format'
import { downloadWord } from '~/utils/wordDownload'
interface Report { title: string; scope: string; headers: string[]; rows: string[][] }
const { bootstrap, load } = useAppData()
const route=useRoute(),router=useRouter()
const kind = ref(['places','uses','inspections','issues'].includes(String(route.query.kind))?String(route.query.kind):'places'), from = ref(''), to = ref(''), communityId = ref(''), status = ref(''), q = ref(''), error = ref(''), busy = ref(false), exporting = ref(false), report = ref<Report | null>(null)
const kinds = [{label:'场所台账',value:'places'},{label:'使用情况',value:'uses'},{label:'检查台账',value:'inspections'},{label:'问题整改',value:'issues'}]
const grids = computed(() => [{label:'全部网格',value:'all'},...(bootstrap.value?.communities ?? []).map(x => ({label:x.name,value:x.id}))])
const statuses = computed(() => [{label:'全部状态',value:'all'},...(kind.value === 'issues' ? [{label:'待整改',value:'pending'},{label:'待复查',value:'feedback'},{label:'已完成',value:'closed'},{label:'已超期',value:'overdue'}] : kind.value === 'inspections' ? [{label:'未发现问题',value:'normal'},{label:'发现问题',value:'issues'},{label:'未完成检查',value:'incomplete'}] : kind.value === 'places' ? [{label:'使用中',value:'false'},{label:'已停用',value:'true'}] : ['使用中','暂停使用','已结束','待核实'].map(x => ({label:x,value:x})))])
const dated=computed(()=>kind.value==='issues'||kind.value==='inspections')
const filters=computed(()=>({from:dated.value?from.value:'',to:dated.value?to.value:'',communityId:communityId.value,status:status.value,q:q.value.trim()}))
const signature=computed(()=>JSON.stringify({kind:kind.value,...filters.value}))
const queried=ref('')
const changed=computed(()=>queried.value!==signature.value)
const searchHint=computed(()=>(({places:'场所、地址、联系人',uses:'场所、使用名称',inspections:'场所、使用名称、检查人',issues:'场所、问题、责任单位'} as Record<string,string>)[kind.value]))
const selected = ref<string[]>([])
function toggle(id:string){selected.value=selected.value.includes(id)?selected.value.filter(x=>x!==id):[...selected.value,id]}
let sequence=0,ready=false
watch(signature,()=>{selected.value=[]})
watch(kind,()=>{status.value='';report.value=null;void router.replace({query:{kind:kind.value}});if(ready)void preview()})
async function preview(){
  const request=++sequence,key=signature.value,reportKind=kind.value,query={...filters.value}
  error.value='';selected.value=[]
  if(query.from&&query.to&&query.from>query.to){busy.value=false;error.value='开始日期不能晚于结束日期';return}
  busy.value=true
  try{const result=await $fetch<Report>(`/api/reports/${reportKind}`,{query});if(request===sequence){report.value=result;queried.value=key}}
  catch(e){if(request===sequence){queried.value='';error.value=errorMessage(e)}}finally{if(request===sequence)busy.value=false}
}
function clearFilters(){from.value='';to.value='';communityId.value='';status.value='';q.value='';void preview()}
async function download(format:string){
  if(busy.value||exporting.value||changed.value||!report.value?.rows.length)return
  exporting.value=true;error.value=''
  const params=new URLSearchParams(filters.value),reportKind=kind.value
  if(selected.value.length)params.set('ids',selected.value.join(','))
  try{await downloadWord(`/api/reports/${reportKind}/${format}?${params}`,`${kinds.find(x=>x.value===reportKind)?.label}.${format}`)}catch(e){error.value=errorMessage(e)}finally{exporting.value=false}
}
onMounted(async()=>{try{await load();ready=true;await preview()}catch(e){error.value=errorMessage(e)}})
onBeforeUnmount(()=>{sequence++})
const communityIdOption=useAllOption(communityId)
const statusOption=useAllOption(status)
</script>
<template>
  <div class="page-stack"><PageHeader title="统计导出"><UButton to="/statistics" label="工作统计" variant="outline" /></PageHeader>
    <div class="panel panel-body space-y-3">
      <div class="flex gap-2 flex-wrap"><UButton v-for="item in kinds" :key="item.value" :label="item.label" :variant="kind===item.value?'solid':'outline'" :disabled="exporting" @click="kind=item.value" /></div>
      <div class="field-grid"><UFormField label="网格"><USelect v-model="communityIdOption" :items="grids" class="w-full" /></UFormField><UFormField label="状态"><USelect v-model="statusOption" :items="statuses" class="w-full" /></UFormField>
        <template v-if="kind==='issues'||kind==='inspections'"><UFormField :label="kind==='issues'?'发现日期从':'检查日期从'"><UInput v-model="from" type="date" class="w-full" /></UFormField><UFormField label="到"><UInput v-model="to" type="date" class="w-full" /></UFormField></template>
        <UFormField label="搜索" class="field-full"><UInput v-model="q" :placeholder="searchHint" class="w-full" @keydown.enter="preview" /></UFormField>
      </div>
      <div class="flex gap-2 flex-wrap"><UButton label="查询" icon="i-lucide-search" :disabled="exporting" :loading="busy" @click="preview" /><UButton v-if="from||to||communityId||status||q" label="清空筛选" color="neutral" variant="ghost" :disabled="exporting" @click="clearFilters" /><UButton :label="selected.length ? `导出所选 ${selected.length} 条 Excel` : '导出全部 Excel'" icon="i-lucide-file-down" variant="outline" :disabled="busy||exporting||changed||!report?.rows.length" @click="download('xlsx')" /><UButton :label="selected.length ? `导出所选 ${selected.length} 条 Word` : '导出全部 Word'" variant="outline" :disabled="busy||exporting||changed||!report?.rows.length" @click="download('docx')" /></div>
    </div>
    <UAlert v-if="error" :description="error" color="error" />
    <section v-if="report" class="panel"><div class="panel-head"><strong>{{report.title}}</strong><span>{{report.rows.length}} 条<span v-if="selected.length"> · 已选 {{selected.length}} 条</span></span></div><div class="panel-body flex items-center justify-between gap-2 text-sm text-muted"><div>{{report.scope}}<span v-if="changed"> · 条件已改变，请查询</span></div><UButton v-if="selected.length" label="取消勾选" color="neutral" variant="link" :disabled="exporting" @click="selected=[]" /></div><div class="overflow-auto"><table class="report-table"><thead><tr><th><input type="checkbox" aria-label="全选查询结果" :disabled="changed||report.rows.length>200||busy||exporting||!report.rows.length" :title="report.rows.length>200?'一次最多勾选 200 条，请缩小范围':undefined" :indeterminate="selected.length>0&&selected.length<report.rows.length" :checked="!!report.rows.length&&selected.length===report.rows.length" @change="selected=selected.length===report.rows.length?[]:report.rows.map(x=>x[0]!)" /></th><th v-for="header in report.headers.slice(1)" :key="header">{{header}}</th></tr></thead><tbody><tr v-for="(row,index) in report.rows" :key="index"><td><input type="checkbox" :aria-label="`选择第 ${index+1} 条`" :checked="selected.includes(row[0]!)" :disabled="changed||busy||exporting||(!selected.includes(row[0]!)&&selected.length>=200)" @change="toggle(row[0]!)" /></td><td v-for="(cell,i) in row.slice(1)" :key="i">{{cell}}</td></tr></tbody></table></div><EmptyState v-if="!report.rows.length" title="没有符合条件的记录" /></section>
  </div>
</template>
<style scoped>.report-table{border-collapse:collapse;width:100%;font-size:13px}.report-table th,.report-table td{text-align:left;padding:12px;min-width:110px;border-bottom:1px solid var(--border);vertical-align:top}.report-table th{background:var(--brand-50);white-space:nowrap}.report-table th:first-child,.report-table td:first-child{min-width:48px;width:48px}.report-table input[type=checkbox]{width:20px;height:20px;cursor:pointer}</style>
