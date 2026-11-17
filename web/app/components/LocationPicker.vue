<script setup lang="ts">
import type {GeoShape,MapState} from '~/types/community'
import {errorMessage} from '~/utils/format'
const value=defineModel<GeoShape|null>({default:null})
const props=defineProps<{disabled?:boolean}>()
const emit=defineEmits<{picked:[shape:GeoShape]}>()
const canvas=useTemplateRef('canvas'),sketching=ref(false)
function ready(){if(draft.value){canvas.value?.edit(draft.value);canvas.value?.focus(draft.value)}else canvas.value?.start('Point')}
const open=ref(false),error=ref(''),draft=ref<GeoShape|null>(null),map=ref<MapState>({layers:[],features:[]})
async function start(){error.value='';try{map.value=await $fetch('/api/map');draft.value=value.value?structuredClone(toRaw(value.value)):null;open.value=true}catch(e){error.value=errorMessage(e)}}
function accept(){if(!draft.value||sketching.value)return;value.value=draft.value;emit('picked',draft.value);open.value=false}
</script>
<template><div><UButton :label="value?'修改位置':'地图选位置'" icon="i-lucide-map-pin" variant="outline" :disabled="props.disabled" @click="start" /><span v-if="value" class="ml-2 text-sm text-muted">{{value.type==='Point'?'已标点':'已圈范围'}}</span><UAlert v-if="error" :description="error" color="error" /><UModal v-model:open="open" title="选择位置" :ui="{content:'w-[96vw] max-w-[1100px]'}"><template #body><OfflineMap ref="canvas" v-model="draft" @ready="ready" @sketching="sketching=$event" :layers="map.layers" :features="map.features" height="60vh" /></template><template #footer><UButton label="使用这个位置" :disabled="!draft||sketching" @click="accept" /><UButton label="取消" color="neutral" variant="outline" @click="open=false" /></template></UModal></div></template>
