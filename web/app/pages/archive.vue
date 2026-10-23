<script setup lang="ts">
import { errorMessage } from '~/utils/format'
interface Archived { id: string; kind: 'inspection' | 'issue'; name: string; date: string; reason: string; revision: number; inspectionId: string }
const rows = ref<Archived[]>([]), error = ref(''), search = ref('')
const shown = computed(() => rows.value.filter(x => `${x.name} ${x.reason}`.includes(search.value)))
async function load() { error.value = ''; try { rows.value = await $fetch('/api/archive') } catch (e) { error.value = errorMessage(e) } }
onMounted(load)
</script>
<template><div class="page-stack"><PageHeader title="作废记录" back="/settings" /><UInput v-model="search" placeholder="搜索名称、原因" class="w-full" /><UAlert v-if="error" :description="error" color="error" /><section v-for="row in shown" :key="row.id" class="panel panel-body space-y-3"><div class="flex justify-between gap-3"><strong>{{ row.name }}</strong><UBadge :label="row.kind === 'inspection' ? '检查' : '问题'" color="neutral" /></div><p class="text-sm text-muted">{{ row.date }} · {{ row.reason }}</p><div class="flex gap-2"><UButton :to="`/inspections/${row.kind === 'inspection' ? row.id : row.inspectionId}${row.kind === 'issue' ? '?issue=' + row.id : ''}`" label="查看原记录" variant="outline" /><ArchiveAction :kind="row.kind" :record-id="row.id" :revision="row.revision" :voided="true" @saved="load" /></div></section><EmptyState v-if="!shown.length && !error" title="暂无作废记录" /></div></template>
