<script setup lang="ts">
import type { MatterDetailDto, MatterDto } from '~/types/api'
import { errorMessage, queryText } from '~/utils/format'
definePageMeta({ formPage: true })
const route = useRoute()
const sourceReturn = computed(() => { const point = queryText(route.query.pointId); return /^[0-9a-f]{32}$/.test(point) ? `/points/${point}` : '' })
const id = computed(() => queryText(route.query.edit)), detail = ref<MatterDto | null>(null), loading = ref(false), error = ref('')
useHead({ title: () => id.value ? '编辑事项' : '登记事项' })
const detailRequest = useLatestRequest()
async function refresh() {
  const request = detailRequest.start(), recordId = id.value
  detail.value = null; loading.value = true; error.value = ''
  try {
    const result = recordId ? await $fetch<MatterDetailDto>(`/api/matters/${recordId}`, { signal: request.signal }) : null
    if (!request.isCurrent()) return
    if (result?.matter.status === 'closed') throw new Error('事项已办结，请先重新办理后修改。')
    detail.value = result?.matter ?? null
  } catch (cause) { if (request.isCurrent()) error.value = errorMessage(cause) }
  finally { if (request.isCurrent()) loading.value = false }
}
watch(id, refresh, { immediate: true })
</script>
<template><div class="page-stack"><PageHeader :title="id ? '编辑事项' : '登记事项'" :back="id ? `/matters/${id}` : sourceReturn || '/matters'" /><PageState :loading="loading" :error="error" @retry="refresh"><MatterEditor :key="id || 'new'" :initial="detail" @saved="matter => navigateTo({ path: `/matters/${matter.id}`, query: sourceReturn ? { returnTo: sourceReturn } : {} })" /></PageState></div></template>
