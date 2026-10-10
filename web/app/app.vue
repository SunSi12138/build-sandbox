<script setup lang="ts">
import { zh_cn } from '@nuxt/ui/locale'
</script>

<template>
  <UApp :locale="zh_cn" :toaster="{ position: 'top-right', ui: { viewport: 'top-[calc(1rem+env(safe-area-inset-top))]' } }">
    <NuxtLayout>
      <NuxtPage />
    </NuxtLayout>
  </UApp>
</template>
