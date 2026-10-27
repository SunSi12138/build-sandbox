<script setup lang="ts">
import type { CommunityDto } from '~/types/api'
import { gridDescendants } from '~/utils/gridTree'
const model=defineModel<string>({required:true})
const props=withDefaults(defineProps<{grids?:CommunityDto[];allowAll?:boolean;allowRoot?:boolean;excludeId?:string;placeholder?:string;disabled?:boolean}>(),{placeholder:'选择网格'})
const {bootstrap}=useAppData()
const open=ref(false),search=ref(''),collapsed=ref(new Set<string>())
const items=computed(()=>props.grids??bootstrap.value?.communities??[])
const label=computed(()=>model.value==='all'&&props.allowAll?'全部网格':!model.value&&props.allowRoot?'无父网格':items.value.find(x=>x.id===model.value)?.name??props.placeholder)
const rows=computed(()=>{
 const excluded=props.excludeId?gridDescendants(items.value,props.excludeId):new Set<string>()
 const valid=items.value.filter(x=>!excluded.has(x.id))
 const matches=new Set(valid.filter(x=>x.name.includes(search.value.trim())).map(x=>x.id))
 if(search.value.trim())for(const id of [...matches]){let p=valid.find(x=>x.id===id)?.parentId;const seen=new Set<string>();while(p&&!seen.has(p)){seen.add(p);matches.add(p);p=valid.find(x=>x.id===p)?.parentId}}
 const result:{grid:CommunityDto;depth:number;children:boolean}[]=[]
 function visit(parent:string,depth:number){for(const grid of valid.filter(x=>(x.parentId||'')===parent)){if(search.value&&!matches.has(grid.id))continue;const children=valid.some(x=>x.parentId===grid.id);result.push({grid,depth,children});if(search.value||!collapsed.value.has(grid.id))visit(grid.id,depth+1)}}
 visit('',0);return result
})
function choose(id:string){model.value=id;open.value=false;search.value=''}
function toggle(id:string){const next=new Set(collapsed.value);next.has(id)?next.delete(id):next.add(id);collapsed.value=next}
</script>
<template><UPopover v-model:open="open" :content="{side:'bottom',align:'start'}"><UButton :label="label" trailing-icon="i-lucide-chevron-down" color="neutral" variant="outline" :disabled="disabled" class="w-full justify-between" :aria-label="placeholder" /><template #content><div class="grid-tree-select"><UInput v-model="search" placeholder="搜索网格" icon="i-lucide-search" class="w-full" autofocus /><div class="tree-scroll" role="tree" aria-label="网格树"><button v-if="allowAll&&!search" class="tree-choice" :class="{selected:model==='all'}" @click="choose('all')">🗺️ 全部网格</button><button v-if="allowRoot&&!search" class="tree-choice" :class="{selected:!model}" @click="choose('')">无父网格</button><div v-for="row in rows" :key="row.grid.id" class="tree-row" :style="{paddingLeft:`${row.depth*18}px`}" role="treeitem" :aria-level="row.depth+1" :aria-selected="model===row.grid.id" :aria-expanded="row.children?!collapsed.has(row.grid.id):undefined"><button v-if="row.children" class="tree-expand" :aria-label="`${collapsed.has(row.grid.id)?'展开':'收起'}${row.grid.name}`" @click="toggle(row.grid.id)"><UIcon :name="collapsed.has(row.grid.id)?'i-lucide-chevron-right':'i-lucide-chevron-down'" /></button><span v-else class="tree-expand">·</span><button class="tree-choice" :class="{selected:model===row.grid.id}" @click="choose(row.grid.id)">{{ row.grid.name }}</button></div><p v-if="!rows.length" class="p-3 text-sm text-muted">没有匹配的网格</p></div></div></template></UPopover></template>
<style scoped>.grid-tree-select{width:300px;max-width:85vw;padding:10px;background:white}.tree-scroll{max-height:340px;overflow:auto;margin-top:8px}.tree-row{display:flex;align-items:center;min-width:0}.tree-choice{display:block;text-align:left;padding:8px;border-radius:5px;flex:1;min-width:0;font-size:14px}.tree-choice.selected{color:var(--brand-700);background:var(--brand-50)}.tree-expand{width:25px;min-width:25px;display:flex;align-items:center;justify-content:center;height:34px;color:var(--text-3)}</style>
