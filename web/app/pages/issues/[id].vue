<script setup lang="ts">
import type { IssueDetailDto } from '~/types/api'
import { errorMessage, queryText } from '~/utils/format'
const route = useRoute(), error = ref(''), loading = ref(true)
async function redirect() { loading.value = true; error.value = ''; try { const detail = await $fetch<IssueDetailDto>(`/api/issues/${queryText(route.params.id)}`); await navigateTo({ path: `/inspections/${detail.issue.inspectionId}`, query: { issue: detail.issue.id } }, { replace: true }) } catch (value) { error.value = errorMessage(value) } finally { loading.value = false } }
onMounted(redirect)
</script>
<template><div class="page-stack"><PageHeader title="排查记录" back="/inspections" /><PageState :loading="loading" :error="error" @retry="redirect" /></div></template>
