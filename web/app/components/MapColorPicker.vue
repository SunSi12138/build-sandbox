<script setup lang="ts">
const color=defineModel<string>({required:true})
defineProps<{disabled?:boolean}>()
const colors=['#087f70','#2563eb','#7c3aed','#db2777','#dc2626','#ea580c','#ca8a04','#64748b']
</script>
<template><div class="flex flex-wrap items-center gap-2"><input v-model="color" type="color" aria-label="自选地图颜色" :disabled="disabled" class="h-9 w-12 rounded border border-default cursor-pointer" /><button v-for="item in colors" :key="item" type="button" :aria-label="`选择颜色 ${item}`" :aria-pressed="color===item" :disabled="disabled" :style="{backgroundColor:item,outline:color===item?'2px solid '+item:'none',outlineOffset:'2px'}" class="size-6 rounded-full border-2 border-white" @click="color=item" /><span class="text-xs text-muted">{{ color }}</span></div></template>
