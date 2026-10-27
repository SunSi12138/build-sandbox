<script setup lang="ts">
import type { CommunityDto } from '~/types/api'
defineProps<{disabled?:boolean}>()
const emit=defineEmits<{created:[grid:CommunityDto]}>()
const open=ref(false)
</script>
<template><UButton label="添加网格" variant="link" :disabled="disabled" @click="open=true" /><GridEditor v-model:open="open" @saved="emit('created',$event)" /></template>
