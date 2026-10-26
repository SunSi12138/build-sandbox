<script setup lang="ts">
import type { MapLayer } from '~/types/community'
import { errorMessage } from '~/utils/format'
const layers = defineModel<MapLayer[]>({ required:true })
const props = defineProps<{ getBounds?: () => {west:number;south:number;east:number;north:number;zoom:number} | undefined }>()
const open = ref(false), error = ref(''), saving = ref('')
const bounds = useState('map-cache-bounds', () => ({west:88.88,south:29.26,east:88.91,north:29.29,zoom:16}))
async function toggle(layer:MapLayer,value:boolean){ saving.value=layer.id;error.value='';try {await $fetch('/api/map/layers',{method:'POST',body:{...layer,enabled:value},retry:0});layers.value=layers.value.map(x=>x.id===layer.id?{...x,enabled:value}:x)}catch(e){error.value=errorMessage(e)}finally{saving.value=''} }
function remember(){const current=props.getBounds?.();if(current)bounds.value=current}
</script>
<template><div class="layer-switch"><div v-if="open" class="layer-popup"><div class="flex justify-between items-center mb-3"><strong>地图图层</strong><UButton icon="i-lucide-x" aria-label="关闭图层" color="neutral" variant="ghost" size="sm" @click="open=false" /></div><UAlert v-if="error" :description="error" color="error" /><label v-for="layer in layers" :key="layer.id" class="layer-option"><UCheckbox :model-value="layer.enabled" :disabled="!!saving" @update:model-value="value=>toggle(layer,!!value)" /><span>{{ layer.name }}</span><small>{{ layer.sourceMode==='offline'?'离线':layer.sourceMode==='hybrid'?'缓存优先':'联网' }}</small></label><UButton to="/layers" label="图层详细设置与缓存" icon="i-lucide-settings-2" variant="link" class="mt-3" @click="remember" /></div><button class="layer-trigger" :aria-expanded="open" @click="open=!open"><span class="text-2xl">🗺️</span><span>图层</span></button></div></template>
<style scoped>.layer-switch{position:absolute;right:14px;bottom:34px;z-index:8}.layer-trigger{display:flex;align-items:center;gap:7px;border:1px solid var(--border);padding:10px 15px;border-radius:10px;background:white;box-shadow:0 3px 18px #17211e24}.layer-popup{position:absolute;right:0;bottom:60px;width:280px;padding:15px;background:white;border-radius:12px;box-shadow:0 4px 25px #17211e33}.layer-option{display:flex;align-items:center;gap:9px;min-height:40px}.layer-option small{margin-left:auto;color:var(--text-3)}</style>
