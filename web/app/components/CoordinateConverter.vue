<script setup lang="ts">
import type {GeoShape} from '~/types/community'
import {errorMessage} from '~/utils/format'
const value=defineModel<string>({default:''})
defineProps<{disabled?:boolean}>()
const emit=defineEmits<{converted:[shape:GeoShape]}>()
const open=ref(false),input=ref(''),source=ref('gcj02'),error=ref(''),busy=ref(false)
function start(){input.value='';error.value='';open.value=true}
async function apply(){const xy=input.value.trim().split(/[,，\s]+/).map(Number);if(xy.length!==2||!xy.every(Number.isFinite)){error.value='请填写经度,纬度';return}busy.value=true;error.value='';try{const shape=await $fetch<GeoShape>('/api/map/coordinates',{query:{lon:xy[0],lat:xy[1],system:source.value}});value.value=shape.coordinates[0]!.join(',');emit('converted',shape);open.value=false}catch(e){error.value=errorMessage(e)}finally{busy.value=false}}
</script>
<template><UButton label="从其他地图粘贴坐标" size="sm" variant="link" :disabled="disabled" @click="start" /><UModal v-model:open="open" title="导入坐标"><template #body><div class="space-y-3"><UFormField label="坐标来源"><USelect v-model="source" :items="[{label:'高德 / 腾讯（GCJ-02）',value:'gcj02'},{label:'GPS / WGS84',value:'wgs84'}]" class="w-full" /></UFormField><UFormField label="经度,纬度"><UInput v-model="input" placeholder="例如 88.893426,29.274331" class="w-full" /></UFormField><UAlert v-if="error" :description="error" color="error" /></div></template><template #footer><UButton label="使用坐标" :loading="busy" @click="apply" /></template></UModal></template>
