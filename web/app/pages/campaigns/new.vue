<script setup lang="ts">
import type { CampaignDetailDto } from '~/types/api'
import { errorMessage, queryText } from '~/utils/format'
definePageMeta({ formPage: true })
const route = useRoute()
const id = computed(() => queryText(route.query.edit))
const detail = ref<CampaignDetailDto | null>(null), loading = ref(false), error = ref('')
const title = computed(() => id.value ? '编辑整治' : '新增整治')
useHead({ title: () => `${title.value} · 街道生态环保` })
async function load() {
  loading.value = true; error.value = ''
  try { detail.value = id.value ? await $fetch<CampaignDetailDto>(`/api/campaigns/${id.value}`) : null }
  catch (value) { error.value = errorMessage(value) }
  finally { loading.value = false }
}
watch(id, load, { immediate: true })
async function saved(value: CampaignDetailDto) { await navigateTo(`/campaigns/${value.campaign.id}`) }
</script>
<template><div class="page-stack"><PageHeader :title="title" :back="id ? `/campaigns/${id}` : '/campaigns'" /><PageState :loading="loading" :error="error" @retry="load"><CampaignEditor :key="id || 'new'" :initial="detail" @saved="saved" /></PageState></div></template>
