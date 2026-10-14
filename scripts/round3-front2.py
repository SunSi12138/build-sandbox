from pathlib import Path
import re
p=Path('.')
def change(name, fn):
 f=p/name; f.write_text(fn(f.read_text()))
def rep(s, old, new):
 assert old in s,old[:160];return s.replace(old,new)
f=p/'web/app/components/CampaignEditor.vue';s=f.read_text()
s=rep(s,'CampaignWriteRequest, PhotoUploadDto','CampaignWriteRequest, PhotoUploadDto, PointDto, NewPointDraft')
s=rep(s,"errorMessage, todayLocal", "errorMessage, todayLocal, parseCoordinates, queryText")
s=rep(s,"const draftKey = 'street-eco-campaign-draft-v2'", "const route = useRoute()\nconst router = useRouter()\nconst draftKey = 'street-eco-campaign-draft-v3'\nconst selectedPoint = ref<PointDto | null>(null)\nconst pointLoading = ref(false)\nconst conflictOpen = ref(false)\nconst requestedPoint = ref('')\nfunction emptyPoint(): NewPointDraft { return { name: '', communityId: '', typeId: '', address: '', contact: '', phone: '', coordinates: '' } }")
s=rep(s,'  communityId: string\n  location: string','  pointId: string\n  pointMode: \'regular\' | \'temporary\'\n  newPoint: NewPointDraft')
s=rep(s,"  communityId: '',\n  location: '',", "  pointId: '',\n  pointMode: 'regular',\n  newPoint: emptyPoint(),")
s=rep(s,"  'communityId',\n  'location',", "  'pointId',")
s=rep(s,'saved.version !== 2', 'saved.version !== 3')
s=rep(s,"    for (const field of textFields)\n      if", "    state.pointMode = saved.state.pointMode === 'temporary' ? 'temporary' : 'regular'\n    const point = saved.state.newPoint\n    if (point && typeof point === 'object') {\n      const values = point as Record<string, unknown>\n      for (const key of Object.keys(emptyPoint()) as (keyof NewPointDraft)[])\n        if (typeof values[key] === 'string') state.newPoint[key] = values[key]\n    }\n    for (const field of textFields)\n      if")
s=rep(s,'JSON.stringify({ version: 2, state, savedAt: time })','JSON.stringify({ version: 3, state, savedAt: time })')
s=rep(s,"      restoreDraft()", "      restoreDraft()\n      const requested = queryText(route.query.pointId)\n      if (requested && (state.pointMode === 'temporary' || (state.pointId && state.pointId !== requested))) {\n        requestedPoint.value = requested\n        conflictOpen.value = true\n      } else if (requested) state.pointId = requested")
s=rep(s,"    ['communityId', '所属社区'],\n    ['location', '整治地点'],\n",'')
s=rep(s,'  if (value.endDate && value.endDate < value.date)', '''  if (!props.initial) {
    if (value.pointMode === 'regular') {
      if (!value.pointId || !selectedPoint.value) errors.push({ name: 'pointId', message: '请选择点位' })
      else if (selectedPoint.value.disabled) errors.push({ name: 'pointId', message: '该点位已停用' })
    } else {
      for (const [key, label] of [['name', '名称'], ['communityId', '社区'], ['typeId', '类型'], ['address', '详细地址']] as const)
        if (!value.newPoint[key].trim()) errors.push({ name: `newPoint.${key}`, message: `请填写${label}` })
      const coordinates = parseCoordinates(value.newPoint.coordinates)
      if (coordinates.error) errors.push({ name: 'newPoint.coordinates', message: coordinates.error })
    }
  }
  if (value.endDate && value.endDate < value.date)''')
s=rep(s,'if (loading.value || saving.value || photosBusy.value || validate(state).length)', 'if (loading.value || saving.value || pointLoading.value || photosBusy.value || validate(state).length)')
s=rep(s,'    communityId: state.communityId,\n    location: state.location,', '''    pointId: props.initial ? props.initial.campaign.pointId : state.pointMode === 'regular' ? state.pointId : null,
    newPoint: !props.initial && state.pointMode === 'temporary' ? {
      name: state.newPoint.name, communityId: state.newPoint.communityId, typeId: state.newPoint.typeId,
      address: state.newPoint.address, contact: state.newPoint.contact, phone: state.newPoint.phone,
      ...parseCoordinates(state.newPoint.coordinates), frequency: 'irregular', disabled: false, temporary: true,
    } : null,''')
s=rep(s,"  if (saving.value) return false", "  persist()\n  if (saving.value) return false")
s=rep(s,"</script>", '''function keepDraft() {
  conflictOpen.value = false
  void router.replace({ query: { ...route.query, pointId: undefined } })
}
function useRequestedPoint() {
  state.pointMode = 'regular'
  state.pointId = requestedPoint.value
  state.newPoint = emptyPoint()
  conflictOpen.value = false
}
</script>''')
s=rep(s,'  <UForm :state="state" :validate="validate" class="form-sections" @submit="save">', '''  <UForm :state="state" :validate="validate" class="form-sections" @submit="save">
    <RecordPointPicker v-if="!initial" v-model:mode="state.pointMode" v-model:point-id="state.pointId" v-model:new-point="state.newPoint" :disabled="formDisabled" @selected="selectedPoint = $event" @busy="pointLoading = $event" />
    <section v-else class="form-section"><h2>点位</h2><NuxtLink :to="`/points/${initial.campaign.pointId}`" class="text-sm text-primary">{{ initial.campaign.pointName }}</NuxtLink><p class="text-xs text-muted">{{ initial.campaign.communityName }} · {{ initial.campaign.location }}</p></section>''')
start=s.index('        <UFormField label="所属社区"');end=s.index('        <UFormField label="整治日期"',start);s=s[:start]+s[end:]
s=s.replace('<UInput\n            v-model="state.organizer"','<NameTagsInput\n            v-model="state.organizer"')
s=s.replace('<UTextarea\n            v-model="state.participants"\n            :rows="2"','<NameTagsInput\n            v-model="state.participants"\n            data-testid="campaign-participants"')
s=rep(s,':disabled="loading || photosBusy || !communities.length || !types.length"', ':disabled="loading || pointLoading || photosBusy || conflictOpen || !communities.length || !types.length"')
s=rep(s,'  <UModal\n    v-model:open="leaveOpen"','''  <UModal v-model:open="conflictOpen" title="草稿点位不同" description="是否将草稿改为当前点位？" :dismissible="false" :close="false"><template #footer><UButton label="继续草稿" color="neutral" variant="outline" @click="keepDraft" /><UButton label="使用当前点位" @click="useRequestedPoint" /></template></UModal>
  <UModal
    v-model:open="leaveOpen"''')
f.write_text(s)
for name in ['web/app/components/PointEditor.vue','web/app/pages/inspections/new.vue','web/app/pages/inspections/[id].vue','web/app/pages/settings.vue']:
 f=p/name;s=f.read_text()
 s=re.sub(r'<UInput(\s+v-model="(?:state\.contact|draft\.newPoint\.contact|draft\.inspector|issue\.responsibleUnit|editState\.inspector|issueState\.responsibleUnit|form\.defaultInspector)")',r'<NameTagsInput\1',s)
 s=re.sub(r'(<UFormField[^>]*label="经纬度"[^>]*>)',r'\1<template #label><CoordinateLabel /></template>',s)
 if name.endswith('PointEditor.vue'):
  s=s.replace(':state="state" :validate="validate"', ':state="state" :disabled="loading || saving" :validate="validate"')
  s=s.replace('if (photosBusy.value || validate(state).length)', 'if (loading.value || saving.value || photosBusy.value || validate(state).length)')
 if name.endswith('inspections/new.vue'):
  s=rep(s,'label="整改期限" required description="默认排查日后 7 天">', 'label="整改期限" class="field-full"><template #label><span class="deadline-label">整改期限<span class="text-error">*</span><span class="text-xs font-normal text-muted">默认排查日后7天</span></span></template>')
 f.write_text(s)
for name in ['web/app/pages/inspections/[id].vue','web/app/pages/campaigns/[id].vue','web/app/pages/points/[id].vue']:
 f=p/name;s=f.read_text()
 s=re.sub(r'<dd(?: class="whitespace-pre-wrap")?>{{ (detail\.inspection\.(?:inspector|contact)|detail\.point\.contact|detail\.campaign\.(?:organizer|participants)|issue\.responsibleUnit)(?: \|\| \'—\')? }}</dd>',r'<dd><NameTags :value="\1" /></dd>',s)
 s=s.replace('{{ detail.inspection.date }} · {{ detail.inspection.inspector }} ·','{{ detail.inspection.date }} · <NameTags :value="detail.inspection.inspector" /> ·')
 f.write_text(s)
change('web/app/components/InspectionRow.vue',lambda s:rep(s,'{{ record.date }} · {{ record.inspector }}','{{ record.date }} · <NameTags :value="record.inspector" />'))
change('web/app/pages/inspections/index.vue',lambda s:rep(s,'<template #rectifyStatus-cell=', '<template #inspector-cell="{ row }"><NameTags :value="row.original.inspector" /></template><template #rectifyStatus-cell='))
f=p/'web/app/pages/points/[id].vue';s=f.read_text()
s=s.replace('PointDetailDto, PhotoUploadDto','PointDetailDto')
s=s.replace("const { bootstrap } = useAppData()\n",'')
s=rep(s,"const detail = ref<PointDetailDto | null>(null),\n  photos = ref<PhotoUploadDto[]>([])","const detail = ref<PointDetailDto | null>(null)\nconst photos = computed(() => detail.value?.photos ?? [])")
s=s.replace("  promoteOpen = ref(false),\n  photoError = ref('')", "  promoteOpen = ref(false)")
a=s.index('    photos.value =');b=s.index('  } catch (value)',a);s=s[:a]+s[b:]
a=s.index('let photoRequestVersion');b=s.index('async function saved()',a);s=s[:a]+s[b:]
a=s.index('            <PhotoUploader');b=s.index('          </div>',a);s=s[:a]+'            <IssuePhotos :photos="photos" label="现场" />\n'+s[b:]
s=rep(s,'><UDropdownMenu :items="menu"', '''><UButton :to="{ path: '/campaigns/new', query: { pointId: id } }" :disabled="detail.point.disabled" color="neutral" variant="outline" class="header-action" data-testid="point-new-campaign"><span class="mobile-only">整治</span><span class="desktop-only">新增整治</span></UButton><UDropdownMenu :items="menu"''')
s=rep(s,'        </section> </template', '''        </section>
        <section class="panel" data-testid="point-campaign-history">
          <div class="panel-head"><h2 class="section-title">历史整治</h2><UButton :to="`/campaigns?pointId=${id}`" label="查看全部" color="neutral" variant="link" /></div>
          <NuxtLink v-for="record in detail.campaigns ?? []" :key="record.id" :to="`/campaigns/${record.id}`" class="list-row"><div class="row-body"><p class="row-title"><span>{{ record.name }}</span></p><p class="row-secondary">{{ record.date }} · {{ record.typeName }}</p></div></NuxtLink>
          <EmptyState v-if="!detail.campaigns?.length" title="暂无整治记录" />
        </section> </template''')
f.write_text(s)
