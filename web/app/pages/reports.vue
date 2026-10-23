<script setup lang="ts">
import { errorMessage } from '~/utils/format'
import { downloadWord } from '~/utils/wordDownload'
interface Report { title: string; scope: string; headers: string[]; rows: string[][] }
const { bootstrap, load } = useAppData()
const route=useRoute()
const kind = ref(['places','uses','inspections','issues'].includes(String(route.query.kind))?String(route.query.kind):'places'), from = ref(''), to = ref(''), communityId = ref(''), status = ref(''), q = ref(''), error = ref(''), busy = ref(false), report = ref<Report | null>(null)
const kinds = [{label:'场所台账',value:'places'},{label:'经营使用',value:'uses'},{label:'检查台账',value:'inspections'},{label:'问题整改',value:'issues'}]
const grids = computed(() => [{label:'全部网格',value:''},...(bootstrap.value?.communities ?? []).map(x => ({label:x.name,value:x.id}))])
const statuses = computed(() => [{label:'全部状态',value:''},...(kind.value === 'issues' ? [{label:'待整改',value:'pending'},{label:'待复查',value:'feedback'},{label:'已完成',value:'closed'},{label:'已超期',value:'overdue'}] : kind.value === 'inspections' ? [{label:'未发现问题',value:'normal'},{label:'发现问题',value:'issues'},{label:'未完成检查',value:'incomplete'}] : kind.value === 'places' ? [{label:'使用中',value:'false'},{label:'已停用',value:'true'}] : ['使用中','暂停使用','已结束','待核实'].map(x => ({label:x,value:x})))])
const filters = computed(() => ({from:from.value,to:to.value,communityId:communityId.value,status:status.value,q:q.value}))
const selected = ref<string[]>([])
function toggle(id:string){selected.value=selected.value.includes(id)?selected.value.filter(x=>x!==id):[...selected.value,id]}
const changed = ref(true)
watch([kind,from,to,communityId,status,q],()=>{changed.value=true;selected.value=[]})
watch(kind,()=>{status.value='';report.value=null})
async function preview(){busy.value=true;error.value='';try{report.value=await $fetch<Report>(`/api/reports/${kind.value}`,{query:filters.value});changed.value=false;selected.value=[]}catch(e){error.value=errorMessage(e)}finally{busy.value=false}}
async function download(format:string){busy.value=true;error.value='';try{const params=new URLSearchParams(filters.value);if(selected.value.length)params.set('ids',selected.value.join(','));await downloadWord(`/api/reports/${kind.value}/${format}?${params}`,`${kinds.find(x=>x.value===kind.value)?.label}.${format}`)}catch(e){error.value=errorMessage(e)}finally{busy.value=false}}
onMounted(async()=>{await load();await preview()})
</script>
<template>
  <div class="page-stack"><PageHeader title="统计导出"><UButton to="/statistics" label="工作统计" variant="outline" /></PageHeader>
    <div class="panel panel-body space-y-3">
      <div class="flex gap-2 flex-wrap"><UButton v-for="item in kinds" :key="item.value" :label="item.label" :variant="kind===item.value?'solid':'outline'" @click="kind=item.value" /></div>
      <div class="field-grid"><UFormField label="网格"><USelect v-model="communityId" :items="grids" class="w-full" /></UFormField><UFormField label="状态"><USelect v-model="status" :items="statuses" class="w-full" /></UFormField>
        <template v-if="kind==='issues'||kind==='inspections'"><UFormField :label="kind==='issues'?'发现日期从':'检查日期从'"><UInput v-model="from" type="date" class="w-full" /></UFormField><UFormField label="到"><UInput v-model="to" type="date" class="w-full" /></UFormField></template>
        <UFormField label="搜索" class="field-full"><UInput v-model="q" placeholder="名称、问题或人员" class="w-full" @keydown.enter="preview" /></UFormField>
      </div>
      <div class="flex gap-2 flex-wrap"><UButton label="查询" icon="i-lucide-search" :loading="busy" @click="preview" /><UButton :label="selected.length ? `导出所选 ${selected.length} 条 Excel` : '导出全部 Excel'" icon="i-lucide-file-down" variant="outline" :disabled="busy||changed" @click="download('xlsx')" /><UButton :label="selected.length ? `导出所选 ${selected.length} 条 Word` : '导出全部 Word'" variant="outline" :disabled="busy||changed" @click="download('docx')" /></div>
    </div>
    <UAlert v-if="error" :description="error" color="error" />
    <section v-if="report" class="panel"><div class="panel-head"><strong>{{report.title}}</strong><span>{{report.rows.length}}条 · {{selected.length ? `导出勾选的 ${selected.length} 条` : '导出全部查询结果'}}</span></div><div class="panel-body text-sm text-muted">{{report.scope}}<span v-if="changed"> · 条件已改变，请查询</span></div><div class="overflow-auto"><table class="report-table"><thead><tr><th><input type="checkbox" aria-label="全选查询结果" :disabled="changed||report.rows.length>200||busy" :checked="!!report.rows.length&&selected.length===report.rows.length" @change="selected=selected.length===report.rows.length?[]:report.rows.map(x=>x[0]!)" /></th><th v-for="header in report.headers.slice(1)" :key="header">{{header}}</th></tr></thead><tbody><tr v-for="(row,index) in report.rows" :key="index"><td><input type="checkbox" :aria-label="`选择第 ${index+1} 条`" :checked="selected.includes(row[0]!)" :disabled="changed||busy||(!selected.includes(row[0]!)&&selected.length>=200)" @change="toggle(row[0]!)" /></td><td v-for="(cell,i) in row.slice(1)" :key="i">{{cell}}</td></tr></tbody></table></div><EmptyState v-if="!report.rows.length" title="没有符合条件的记录" /></section>
  </div>
</template>
<style scoped>.report-table{border-collapse:collapse;width:100%;font-size:13px}.report-table th,.report-table td{text-align:left;padding:12px;min-width:110px;border-bottom:1px solid var(--border);vertical-align:top}.report-table th{background:var(--brand-50);white-space:nowrap}.report-table th:first-child,.report-table td:first-child{min-width:48px;width:48px}.report-table input[type=checkbox]{width:20px;height:20px;cursor:pointer}</style>
