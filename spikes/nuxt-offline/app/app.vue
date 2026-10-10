<script setup lang="ts">
import { zh_cn } from '@nuxt/ui/locale'
</script>

<template>
  <UApp :locale="zh_cn">
    <div class="mx-auto max-w-4xl p-4 sm:p-8">
      <header class="mb-6 flex items-center justify-between gap-3">
        <div class="flex items-center gap-3">
          <UIcon name="i-lucide-leaf" class="size-8 text-primary" />
          <div>
            <h1 class="text-xl font-bold">街道生态环保</h1>
            <p class="text-sm text-muted">离线静态构建验证</p>
          </div>
        </div>
        <UBadge color="success" variant="subtle">内网可用</UBadge>
      </header>
      <nav class="mb-6 flex gap-2" aria-label="主导航">
        <UButton to="/" icon="i-lucide-house" variant="soft">首页</UButton>
        <UButton to="/settings" icon="i-lucide-settings" variant="soft">设置</UButton>
      </nav>
      <NuxtPage />
    </div>
  </UApp>
</template>
