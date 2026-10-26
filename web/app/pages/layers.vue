<script setup lang="ts">
import type { MapLayer } from '~/types/community'
import { errorMessage } from '~/utils/format'
const layers=ref<MapLayer[]>([]),error=ref(''),busy=ref(false),adding=ref(false),expanded=ref('')
const fresh=():MapLayer=>({id:'layer-'+Date.now().toString(36),name:'',folder:'layer-'+Date.now().toString(36),scheme:'xyz',enabled:true,isDefault:false,opacity:1,minZoom:0,maxZoom:18,sortOrder:3,urlTemplate:'',apiKey:'',subdomains:'0,1,2,3',sourceMode:'offline',cacheVersion:0})
const draft=reactive(fresh())
const modes=[{label:'仅用本地缓存（断网可用）',value:'offline'},{label:'优先缓存，缺失时联网并缓存',value:'hybrid'},{label:'联网更新，失败时使用缓存',value:'online'}]
async function load(){try{layers.value=await $fetch('/api/map/layers')}catch(e){error.value=errorMessage(e)}}
async function save(layer:MapLayer){busy.value=true;error.value='';try{await $fetch('/api/map/layers',{method:'POST',body:layer,retry:0});adding.value=false;await load()}catch(e){error.value=errorMessage(e)}finally{busy.value=false}}
async function add(){await save(draft);expanded.value=draft.id}
onMounted(load)
</script>
<template>
<div class="page-stack">
<PageHeader title="地图图层设置" back="/map">
<UButton label="新增图层" icon="i-lucide-plus" @click="Object.assign(draft,fresh());adding=true" />
</PageHeader>
<UAlert v-if="error" :description="error" color="error" />
<p class="text-sm text-muted">常用图层开关在地图右下角。这里配置地图来源、链接、密钥和缓存。</p>
<CacheImport @saved="load" />
<section v-for="layer in layers" :key="layer.id" class="panel panel-body space-y-4">
<div class="flex gap-3 items-center">
<UInput v-model="layer.name" aria-label="图层名称" class="flex-1" />
<UCheckbox v-model="layer.enabled" label="显示" />
<UButton label="保存" :loading="busy" variant="outline" @click="save(layer)" />
<UButton :label="expanded===layer.id?'收起':'详细设置'" variant="outline" color="neutral" @click="expanded=expanded===layer.id?'':layer.id" />
</div>
<template v-if="expanded===layer.id">
<div class="field-grid">
<UFormField label="加载方式" class="field-full">
<USelect v-model="layer.sourceMode" :items="modes" class="w-full" />
</UFormField>
<UFormField label="瓦片链接模板" class="field-full" help="填完整链接，可在路径或查询参数中拼接变量">
<UTextarea v-model="layer.urlTemplate" placeholder="https://tiles.example.com/{z}/{x}/{y}.png?key={key}" :rows="3" class="w-full" />
<p class="text-xs text-muted mt-2" v-text="'支持 {z} 级别、{x} 列、{y} 行、{-y} 反向行号、{s} 子域、{key} 密钥、{quadkey} 四叉树编码。WMTS 可直接拼接 TileMatrix={z}&TileCol={x}&TileRow={y}。'" />
</UFormField>
<UFormField label="密钥">
<UInput v-model="layer.apiKey" type="password" autocomplete="off" class="w-full" />
</UFormField>
<UFormField label="子域（逗号分隔）">
<UInput v-model="layer.subdomains" placeholder="0,1,2,3 或 a,b,c" class="w-full" />
</UFormField>
<UFormField label="瓦片行号规则">
<USelect v-model="layer.scheme" :items="[{label:'XYZ（从北向南）',value:'xyz'},{label:'TMS（从南向北）',value:'tms'}]" class="w-full" />
</UFormField>
<UFormField label="缓存目录名称">
<UInput v-model="layer.folder" class="w-full" />
</UFormField>
<UFormField label="透明度">
<UInput v-model.number="layer.opacity" type="number" min="0" max="1" step="0.1" class="w-full" />
</UFormField>
<UFormField label="叠放顺序">
<UInput v-model.number="layer.sortOrder" type="number" class="w-full" />
</UFormField>
<UFormField label="最小级别">
<UInput v-model.number="layer.minZoom" type="number" min="0" max="22" class="w-full" />
</UFormField>
<UFormField label="最大级别">
<UInput v-model.number="layer.maxZoom" type="number" min="0" max="22" class="w-full" />
</UFormField>
</div>
<UButton label="保存图层设置" :loading="busy" @click="save(layer)" />
<TileCacheUpdater :layer="layer" @updated="load" />
</template>
</section>
<UModal v-model:open="adding" title="新增图层">
<template #body>
<div class="space-y-3">
<UFormField label="图层名称" required>
<UInput v-model="draft.name" class="w-full" />
</UFormField>
<UFormField label="瓦片链接（也可稍后填写）">
<UTextarea v-model="draft.urlTemplate" placeholder="https://tiles.example.com/{z}/{x}/{y}.png?key={key}" class="w-full" />
</UFormField>
<UFormField label="密钥">
<UInput v-model="draft.apiKey" type="password" class="w-full" />
</UFormField>
<p class="text-sm text-muted">添加后展开详细设置，配置加载方式和更新缓存。</p>
</div>
</template>
<template #footer>
<UButton label="添加图层" :loading="busy" @click="add" />
</template>
</UModal>
</div>
</template>
