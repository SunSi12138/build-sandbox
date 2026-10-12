<script setup lang="ts">
import type { InspectionDto } from '~/types/api'
import { overdueDays, rectifyLabel, rectifyTone } from '~/utils/format'
defineProps<{ record: InspectionDto; returnTo?: string }>()
</script>
<template><NuxtLink :to="{ path: `/inspections/${record.id}`, query: returnTo ? { returnTo } : {} }" class="list-row" data-testid="inspection-row"><div class="row-body"><div class="row-title"><span>{{ record.pointName }}</span><StatusBadge :tone="rectifyTone(record.rectifyStatus)">{{ rectifyLabel(record, true) }}</StatusBadge></div><p class="row-secondary">{{ record.communityName }} · {{ record.typeName }}</p><div class="row-footer"><span>{{ record.date }} · {{ record.inspector }}</span><span class="flex shrink-0 gap-1"><StatusBadge v-if="record.overdueCount" tone="danger">逾期 {{ overdueDays(record.nearestDeadline) }} 天</StatusBadge><StatusBadge v-if="record.pointTemporary" tone="outline">临时</StatusBadge></span></div></div></NuxtLink></template>
