<script setup lang="ts">
import { zh_cn } from '@nuxt/ui/locale'
</script>
<template><UApp :locale="zh_cn" :toaster="{ position: 'top-right', duration: 2000, ui: { viewport: 'top-[calc(52px+env(safe-area-inset-top))] md:top-4' } }"><NuxtLayout><NuxtPage /></NuxtLayout></UApp></template>
