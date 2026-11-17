<script setup lang="ts">
const route = useRoute()
await navigateTo({ path: '/map', query: { ...route.query, view: 'list' } }, { replace: true })
</script>
<template><div /></template>
