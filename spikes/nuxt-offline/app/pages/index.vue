<script setup lang="ts">
const checked = ref(true)
const selected = ref('东湖社区')
const date = ref('2026-10-10')
const description = ref('门前生活垃圾需及时清理')
const toast = useToast()
const rows = [
  { site: '东湖餐饮店', community: '东湖社区', status: '待整改' },
  { site: '滨河施工点', community: '滨河社区', status: '社区已反馈' }
]
</script>

<template>
  <main class="space-y-5">
    <UAlert title="本页无需访问外网" description="样式、图标和中文界面均来自本机静态资源。" icon="i-lucide-wifi-off" color="success" variant="subtle" />
    <UCard>
      <template #header><h2 class="text-lg font-semibold">排查登记验证</h2></template>
      <div class="grid gap-5 sm:grid-cols-2">
        <UFormField label="所属社区"><USelect v-model="selected" :items="['东湖社区', '滨河社区']" class="w-full" /></UFormField>
        <UFormField label="排查日期"><UInput v-model="date" type="date" class="w-full" /></UFormField>
        <UFormField label="问题描述" class="sm:col-span-2"><UTextarea v-model="description" class="w-full" /></UFormField>
        <UCheckbox v-model="checked" label="整改后进行复查" />
        <div class="flex flex-wrap gap-3">
          <UButton icon="i-lucide-save" @click="toast.add({title: '离线组件正常', description: '本次仅验证界面，没有写入数据。', color: 'success'})">保存验证</UButton>
          <UModal title="删除确认" description="验证中文弹窗、关闭图标与按钮。">
            <UButton icon="i-lucide-trash-2" color="error" variant="soft">删除验证</UButton>
            <template #body><p>这是一次离线界面验证，不会删除数据。</p></template>
          </UModal>
        </div>
      </div>
    </UCard>
    <UCard>
      <template #header><h2 class="text-lg font-semibold">整改列表验证</h2></template>
      <UTable :data="rows" :columns="[{ accessorKey: 'site', header: '点位' }, { accessorKey: 'community', header: '社区' }, { accessorKey: 'status', header: '状态' }]" />
    </UCard>
  </main>
</template>
