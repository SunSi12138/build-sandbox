<script setup lang="ts">
const route = useRoute()
const status = typeof route.query.status === 'string' ? route.query.status : undefined
await navigateTo({ path: '/inspections', query: { ...route.query, status: undefined, rectify: status === 'all' ? undefined : status } }, { replace: true })
</script>
<template><div /></template>
