<script setup lang="ts">
import type { MatterDetailDto, MatterDto } from '~/types/api'
import { errorMessage, queryText } from '~/utils/format'
definePageMeta({ formPage: true })
const route = useRoute(), id = computed(() => queryText(route.query.edit)), detail = ref<MatterDto | null>(null), loading = ref(false), error = ref('')
useHead({ title: () => id.value ? '编辑事项' : '登记事项' })
async function refresh() { loading.value = true; error.value = ''; try { detail.value = id.value ? (await $fetch<MatterDetailDto>(`/api/matters/${id.value}`)).matter : null } catch (cause) { error.value = errorMessage(cause) } finally { loading.value = false } }
watch(id, refresh, { immediate: true })
</script>
<template><div class="page-stack"><PageHeader :title="id ? '编辑事项' : '登记事项'" :back="id ? `/matters/${id}` : '/matters'" /><PageState :loading="loading" :error="error" @retry="refresh"><MatterEditor :key="id || 'new'" :initial="detail" @saved="matter => navigateTo(`/matters/${matter.id}`)" /></PageState></div></template>
