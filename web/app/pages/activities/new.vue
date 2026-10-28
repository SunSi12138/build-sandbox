<script setup lang="ts">
import type { ActivityDetailDto } from '~/types/api'
import { errorMessage, queryText } from '~/utils/format'
definePageMeta({ formPage: true })
const route = useRoute()
const id = computed(() => queryText(route.query.edit))
const detail = ref<ActivityDetailDto | null>(null)
const loading = ref(false)
const error = ref('')
const title = computed(() => id.value ? '编辑活动' : '新增活动')
useHead({ title: () => `${title.value} · 街道生态环保` })
const detailRequest = useLatestRequest()
async function refresh() {
  const request = detailRequest.start(), recordId = id.value
  detail.value = null; loading.value = true; error.value = ''
  try {
    const result = recordId ? await $fetch<ActivityDetailDto>(`/api/activities/${recordId}`, { signal: request.signal }) : null
    if (!request.isCurrent()) return
    detail.value = result
  } catch (cause) { if (request.isCurrent()) error.value = errorMessage(cause) }
  finally { if (request.isCurrent()) loading.value = false }
}
watch(id, refresh, { immediate: true })
async function saved(value: ActivityDetailDto) { await navigateTo(`/activities/${value.activity.id}`) }
</script>
<template>
  <div class="page-stack">
    <PageHeader :title="title" :back="id ? `/activities/${id}` : '/activities'" />
    <PageState :loading="loading" :error="error" @retry="refresh">
      <ActivityEditor :key="id || 'new'" :initial="detail" @saved="saved" />
    </PageState>
  </div>
</template>
