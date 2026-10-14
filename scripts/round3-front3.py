from pathlib import Path
p=Path('.')
def rep(s,a,b):
 assert a in s,a[:160];return s.replace(a,b)
f=p/'web/app/pages/points/index.vue';s=f.read_text()
s=rep(s,"editorOpen = ref(false)","editorOpen = ref(false), exportOpen = ref(false)")
s=rep(s,'<template #actions>', '<template #actions><UButton label="导出台账" color="neutral" variant="outline" class="desktop-only" data-testid="point-ledger-desktop" @click="exportOpen = true" />')
s=rep(s,'    <PointEditor v-model:open=', '    <LedgerExport v-model:open="exportOpen" kind="point" />\n    <PointEditor v-model:open=')
s=rep(s,'<div class="filter-tool-row">', '<div class="filter-tool-row"><UButton label="导出台账" color="neutral" variant="link" class="mobile-only" data-testid="point-ledger-mobile" @click="exportOpen = true" />')
f.write_text(s)
f=p/'web/app/pages/campaigns/index.vue';s=f.read_text();s=rep(s,"const q = field('q'),", "const q = field('q'),\n  pointId = field('pointId'),");s=rep(s,'      query: {\n        q:', '      query: {\n        pointId: pointId.value || undefined,\n        q:');f.write_text(s)
f=p/'web/app/pages/campaigns/[id].vue';s=f.read_text()
s=rep(s,'<div><dt>整治类型</dt>', '<div><dt>点位</dt><dd><NuxtLink :to="`/points/${detail.campaign.pointId}`" class="text-primary">{{ detail.campaign.pointName }}</NuxtLink><StatusBadge v-if="detail.campaign.pointTemporary" tone="outline">临时</StatusBadge></dd></div><div><dt>整治类型</dt>')
s=rep(s,'description="整治记录和照片将被删除，无法恢复"', ':description="detail?.campaign.pointTemporary ? \'若这是该临时点位最后一条排查或整治，点位及其点位照片也将删除。删除后无法恢复\' : \'整治记录和照片将被删除，无法恢复\'"')
f.write_text(s)
f=p/'web/app/pages/inspections/[id].vue';s=f.read_text();s=s.replace('最后一条排查，', '最后一条排查或整治，');f.write_text(s)
f=p/'web/app/components/LedgerExport.vue';s=f.read_text()
s=rep(s,"import { downloadWord }", "import type { PageDto, PointDto } from '~/types/api'\nimport { downloadWord }")
s=rep(s,"kind: 'inspection' | 'campaign';", "kind: 'inspection' | 'campaign' | 'point';")
s=rep(s,"const kind = ref<'inspection' | 'rectification'>('inspection')", "const kind = ref<'inspection' | 'rectification' | 'campaign'>('inspection')\nconst ledgerKind = computed(() => props.kind === 'campaign' ? 'campaign' : kind.value)\nconst kindOptions = computed(() => [\n  { label: '排查台账', value: 'inspection' as const }, { label: '整改台账', value: 'rectification' as const },\n  ...(props.kind === 'point' ? [{ label: '整治台账', value: 'campaign' as const }] : []),\n])\nconst pointSearch = ref('')\nconst pointOptions = ref<PointDto[]>([])\nconst selectedPoints = ref<PointDto[]>([])\nconst pointsBusy = ref(false)\nconst pointPage = ref(1)\nconst pointTotal = ref(0)\nlet pointSequence = 0\nlet pointTimer: ReturnType<typeof setTimeout> | undefined")
s=rep(s,"props.kind === 'campaign' || kind.value === 'rectification'", "ledgerKind.value !== 'inspection'")
s=rep(s,"  error.value = ''\n  community.value", "  error.value = ''\n  selectedPoints.value = []\n  pointSearch.value = ''\n  kind.value = 'inspection'\n  community.value")
s=rep(s,"try { await load(); selectRange('month') }", "try { await load(); selectRange('month'); if (props.kind === 'point') await listPoints() }")
s=rep(s,"  busy.value = true\n  error.value = ''", "  if (props.kind === 'point' && !selectedPoints.value.length) { error.value = '请选择点位'; return }\n  busy.value = true\n  error.value = ''")
s=rep(s,"const name = props.kind === 'campaign' ? '整治台账'", "const name = ledgerKind.value === 'campaign' ? '整治台账'")
s=rep(s,"const route = props.kind === 'campaign' ? 'campaign-ledger' : `${kind.value}-ledger`", "const route = `${ledgerKind.value}-ledger`")
s=rep(s,"if (props.kind === 'campaign' && campaignType.value !== 'all')", "if (ledgerKind.value === 'campaign' && campaignType.value !== 'all')")
s=rep(s,"  if (canIncludePhotos.value) query.set", "  if (props.kind === 'point') for (const point of selectedPoints.value) query.append('pointId', point.id)\n  if (canIncludePhotos.value) query.set")
s=rep(s,'</script>', '''async function listPoints(reset = true) {
  const sequence = ++pointSequence
  pointsBusy.value = true
  try {
    const result = await $fetch<PageDto<PointDto>>('/api/points', { query: { temporary: 'all', disabled: 'all', q: pointSearch.value.trim() || undefined, page: reset ? 1 : pointPage.value + 1, pageSize: 10 } })
    if (sequence !== pointSequence) return
    pointOptions.value = reset ? result.items : [...pointOptions.value, ...result.items]
    pointPage.value = result.page
    pointTotal.value = result.total
  } catch (cause) { if (sequence === pointSequence) error.value = errorMessage(cause) }
  finally { if (sequence === pointSequence) pointsBusy.value = false }
}
function selectPoint(point: PointDto, checked: boolean | 'indeterminate') {
  if (disabled.value) return
  if (!checked) selectedPoints.value = selectedPoints.value.filter(p => p.id !== point.id)
  else if (!selectedPoints.value.some(p => p.id === point.id)) {
    if (selectedPoints.value.length >= 200) { error.value = '最多选择 200 个点位'; return }
    selectedPoints.value.push(point)
  }
}
watch(pointSearch, () => { clearTimeout(pointTimer); if (open.value && props.kind === 'point') pointTimer = setTimeout(() => { void listPoints() }, 250) })
onBeforeUnmount(() => { clearTimeout(pointTimer); pointSequence++ })
</script>''')
s=rep(s,'<p class="text-xs text-muted" data-testid="ledger-scope-help">', '<p v-if="props.kind !== \'point\'" class="text-xs text-muted" data-testid="ledger-scope-help">')
s=rep(s,"v-if=\"props.kind === 'inspection'\" label=\"台账类型\"", "v-if=\"props.kind !== 'campaign'\" label=\"台账类型\"")
s=rep(s,"[{ label: '排查台账', value: 'inspection' as const }, { label: '整改台账', value: 'rectification' as const }]", 'kindOptions')
s=rep(s,'        <UFormField label="时间范围"', '''        <UFormField v-if="props.kind === 'point'" label="点位" required>
          <UInput v-model="pointSearch" type="search" placeholder="搜索点位" class="w-full" :disabled="disabled" data-testid="ledger-point-search" />
          <div v-if="selectedPoints.length" class="mt-2 flex flex-wrap gap-1"><UButton v-for="point in selectedPoints" :key="point.id" :label="point.name" trailing-icon="i-lucide-x" color="neutral" variant="soft" :disabled="disabled" :aria-label="`移除点位 ${point.name}`" @click="selectPoint(point, false)" /></div>
          <div class="mt-2 max-h-48 overflow-y-auto" data-testid="ledger-point-options">
            <UCheckbox v-for="point in pointOptions" :key="point.id" :label="point.name" :model-value="selectedPoints.some(p => p.id === point.id)" :disabled="disabled" class="check-row" @update:model-value="selectPoint(point, $event)" />
            <p v-if="!pointsBusy && !pointOptions.length" class="text-xs text-muted">暂无点位</p>
            <UButton v-if="pointOptions.length < pointTotal" label="加载更多" color="neutral" variant="link" :loading="pointsBusy" :disabled="disabled" @click="listPoints(false)" />
          </div>
        </UFormField>
        <UFormField label="时间范围"''')
s=rep(s,"v-if=\"props.kind === 'campaign'\" label=\"整治类型\"", "v-if=\"ledgerKind === 'campaign'\" label=\"整治类型\"")
f.write_text(s)
f=p/'web/app/pages/settings.vue';s=f.read_text()
s=rep(s,"const placeholders =", "const templateHost = ref<HTMLElement | null>(null)\nconst templateError = ref('')\nconst placeholders =")
s=rep(s,'async function fetchSettings', '''async function insertVariable(variable: string) {
  if (saving.value) return
  const editor = templateHost.value?.querySelector('textarea')
  if (!editor) return
  const start = editor.selectionStart ?? form.noticeTemplate.length
  const end = editor.selectionEnd ?? start
  const value = form.noticeTemplate.slice(0, start) + variable + form.noticeTemplate.slice(end)
  if (value.length > 12000) { templateError.value = '正文最多 12000 字'; return }
  templateError.value = ''
  form.noticeTemplate = value
  await nextTick()
  editor.focus({ preventScroll: true })
  editor.setSelectionRange(start + variable.length, start + variable.length)
}
async function fetchSettings''')
s=rep(s,'<UFormField label="正文内容" name="noticeTemplate" required><UTextarea v-model="form.noticeTemplate" :rows="10" :maxlength="12000" class="w-full" :disabled="saving" /></UFormField>', '<div ref="templateHost"><UFormField label="正文内容" name="noticeTemplate" required><UTextarea v-model="form.noticeTemplate" :rows="10" :maxlength="12000" class="w-full" :disabled="saving" data-testid="notice-template" /></UFormField></div><p v-if="templateError" class="text-xs text-error" role="alert">{{ templateError }}</p>')
s=rep(s,'<UBadge v-for="placeholder in placeholders" :key="placeholder" color="neutral" variant="outline">{{ placeholder }}</UBadge>', '<UButton v-for="placeholder in placeholders" :key="placeholder" type="button" color="neutral" variant="outline" :disabled="saving" :aria-label="`插入${placeholder}`" @pointerdown.prevent @mousedown.prevent @click="insertVariable(placeholder)">{{ placeholder }}</UButton>')
s=s.replace('<NameTagsInput v-model="form.defaultInspector"', '<NameTagsInput data-testid="default-inspector" v-model="form.defaultInspector"')
f.write_text(s)
