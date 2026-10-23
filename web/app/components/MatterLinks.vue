<script setup lang="ts">
import type { MatterLinkDto } from '~/types/api'
defineProps<{ matters: MatterLinkDto[] }>()
</script>
<template><ul v-if="matters.length" class="space-y-1"><li v-for="matter in matters" :key="matter.id"><NuxtLink :to="`/matters/${matter.id}`" class="inline-flex min-h-10 items-center text-sm text-primary">{{ matter.title }}<span v-if="matter.status === 'closed'" class="ml-2 text-xs text-muted">已办结</span></NuxtLink></li></ul><span v-else class="text-sm text-muted">未关联事项</span></template>
