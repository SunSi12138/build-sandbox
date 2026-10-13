"""Temporary, exact-match source edits for the verified first optimization stage.
Removed after delivery; this is not part of the application runtime.
"""
from pathlib import Path
import re


def replace(path, old, new, count=1):
    p = Path(path)
    text = p.read_text()
    assert text.count(old) == count, (path, old[:100], text.count(old))
    p.write_text(text.replace(old, new))


replace('server/Contracts.cs', 'public sealed record PointPromoteRequest', 'public sealed record PointPhotosRequest(string[] PhotoIds);\npublic sealed record PointStatusRequest(bool? Disabled);\npublic sealed record PointPromoteRequest')
replace('server/AppJsonContext.cs', '[JsonSerializable(typeof(PointPromoteRequest))]', '[JsonSerializable(typeof(PointPhotosRequest))]\n[JsonSerializable(typeof(PointStatusRequest))]\n[JsonSerializable(typeof(PointPromoteRequest))]')
replace('server/Api.cs', '        app.MapPut("/api/points/{id}", updatePoint);', '''        app.MapPut("/api/points/{id}", updatePoint);
        RequestDelegate pointPhotos = async context => await Write(context,
            store.AttachPointPhotos(Id(context), await Read(context, AppJsonContext.Default.PointPhotosRequest)), AppJsonContext.Default.PointDto);
        RequestDelegate pointStatus = async context => await Write(context,
            store.SetPointDisabled(Id(context), await Read(context, AppJsonContext.Default.PointStatusRequest)), AppJsonContext.Default.PointDto);
        app.MapPut("/api/points/{id}/photos", pointPhotos);
        app.MapPut("/api/points/{id}/status", pointStatus);''')
replace('server/Store.Points.cs', '    private static void ValidateFrequency(string frequency)', '''    // Photo completion must never replay a stale point edit. This operation is
    // additive/idempotent for this owner; removal remains an explicit DELETE.
    public PointDto AttachPointPhotos(string id, PointPhotosRequest request)
    {
        lock (_gate)
        {
            PrepareWrite();
            _ = GetPointInternal(id);
            if (request.PhotoIds is null) throw new ApiException(400, "请提供照片编号列表。");
            using (var transaction = _db.BeginTransaction())
            {
                AttachPhotos(request.PhotoIds, "point", null, null, transaction, pointId: id);
                transaction.Commit();
            }
            return GetPointInternal(id);
        }
    }

    public PointDto SetPointDisabled(string id, PointStatusRequest request)
    {
        lock (_gate)
        {
            PrepareWrite();
            _ = GetPointInternal(id);
            if (request.Disabled is null) throw new ApiException(400, "请提供停用状态。");
            using var command = Command("UPDATE points SET disabled=$disabled WHERE id=$id;", null,
                ("$id", id), ("$disabled", request.Disabled.Value ? 1 : 0));
            RequireChanged(command.ExecuteNonQuery(), "点位");
            return GetPointInternal(id);
        }
    }

    private static void ValidateFrequency(string frequency)''')
replace('server/Startup.cs', '        builder.Logging.ClearProviders();', '        builder.Logging.ClearProviders();\n        builder.Logging.SetMinimumLevel(LogLevel.Warning);')

p = Path('web/app/pages/points/[id].vue')
s = p.read_text().replace('PointDetailDto, PointWriteRequest, PhotoUploadDto', 'PointDetailDto, PhotoUploadDto')
s, count = re.subn(r'^function pointBody\(\).*\n', '', s, flags=re.M)
assert count == 1
old = next(line for line in s.splitlines() if line.startswith('async function updatePhotos('))
s = s.replace(old, '''let photoRequestVersion = 0
async function updatePhotos(value: PhotoUploadDto[]) {
  photos.value = value
  const version = ++photoRequestVersion
  const pointId = id.value
  photoError.value = ''
  try {
    await $fetch(`/api/points/${pointId}/photos`, { method: 'PUT', body: { photoIds: value.map(photo => photo.id) }, retry: 0 })
  } catch (error) {
    if (version === photoRequestVersion && pointId === id.value) photoError.value = errorMessage(error)
  }
}''')
s = s.replace('`/api/points/${id.value}`, { method: \'PUT\', body: { ...pointBody(), disabled: !detail.value.point.disabled }', '`/api/points/${id.value}/status`, { method: \'PUT\', body: { disabled: !detail.value.point.disabled }')
assert 'pointBody' not in s
p.write_text(s)

p = Path('web/app/components/PhotoUploader.vue')
s = p.read_text()
s = s.replace('  busy: [value: boolean]\n', '  busy: [value: boolean]\n  failed: [value: boolean]\n')
s = s.replace('  uploaded?: PhotoUploadDto\n', '  uploaded?: PhotoUploadDto\n  task?: Promise<void>\n  deleteFailed?: boolean\n')
s = s.replace("const failedCount = computed(() => jobs.value.filter(job => job.status === 'failed').length)", "const failedCount = computed(() => jobs.value.filter(job => job.status === 'failed').length)\nwatch(failedCount, value => emit('failed', value > 0), { immediate: true, flush: 'sync' })")
s = s.replace('  localPhotos.value = photos\n  emit', '  if (disposed) return\n  localPhotos.value = photos\n  emit')
start, end = s.index('async function runJob('), s.index('\nfunction chooseFiles(')
s = s[:start] + '''function removeJob(job: UploadJob) {
  jobs.value = jobs.value.filter(value => value.key !== job.key)
}

function startJob(job: UploadJob) {
  if (disposed || deleting.value || props.disabled) return
  job.task = markRaw(runJob(job))
}

async function runJob(job: UploadJob) {
  job.error = ''
  job.deleteFailed = false
  job.status = 'queued'
  job.progress = 0
  job.message = '等待处理'
  const controller = markRaw(new AbortController())
  job.controller = controller
  const active = () => !disposed && !controller.signal.aborted && job.controller === controller && jobs.value.some(value => value.key === job.key)
  const quality = props.quality
  try {
    if (!job.uploaded && (!job.compressed || job.quality !== quality)) {
      const compressed = await compressPhoto(job.file, {
        quality,
        signal: controller.signal,
        onProgress(percent, message) {
          if (!active()) return
          job.status = percent === 0 ? 'queued' : 'compressing'
          job.progress = Math.round(percent * 0.6)
          job.message = message
        },
      })
      if (!active()) return
      job.compressed = compressed
      job.quality = quality
    }
    if (!active() || !job.compressed) return
    job.status = 'uploading'
    job.progress = 60
    job.message = `正在上传 · ${formatBytes(job.compressed.blob.size)}`
    if (!job.uploaded) {
      // Retain the returned ID even if cancellation won this await, so the
      // cancellation path can delete a late server response before forgetting it.
      job.uploaded = await uploadPhoto(job.compressed.blob, props.phase, {
        signal: controller.signal,
        onProgress(percent) {
          if (!active()) return
          job.progress = 60 + Math.round(percent * 0.4)
          job.message = percent < 100 ? `正在上传 · ${percent}%` : '确认照片已保存'
        },
      })
    }
    if (!active()) return
    const uploaded = job.uploaded
    if (job.compressed.thumbnail && job.compressed.thumbnail.size <= 200 * 1024) {
      try {
        await $fetch(`/api/photos/${uploaded.id}/thumb`, { method: 'PUT', body: job.compressed.thumbnail, headers: { 'Content-Type': 'image/jpeg' }, signal: controller.signal, retry: 0, timeout: 10000 })
      } catch { /* An optional thumbnail failure must not discard the original. */ }
    }
    // In particular, deleting during the thumbnail await must never resurrect
    // the deleted ID in a parent form or its persisted draft.
    if (!active()) return
    compressionEngines[uploaded.id] = job.compressed.engine
    if (!localPhotos.value.some(photo => photo.id === uploaded.id)) updatePhotos([...localPhotos.value, uploaded])
    removeJob(job)
  } catch (error) {
    if (!active()) return
    job.status = 'failed'
    job.error = errorMessage(error)
    job.message = '照片未完成'
  }
}
''' + s[end:]
s = s.replace('    void runJob(job)', '    startJob(job)')
s = s.replace('async function removeUploadedPhoto(photo: PhotoUploadDto) {', 'async function deleteServerPhoto(photo: PhotoUploadDto) {')
s = s.replace("query: { confirm: true }, retry: 0 })", "query: { confirm: true }, retry: 0, timeout: 10000 })")
s = s.replace('  updatePhotos(localPhotos.value.filter(value => value.id !== photo.id))\n}', '}')
start, end = s.index('async function confirmRemove()'), s.index('\nonBeforeUnmount(')
s = s[:start] + '''async function confirmRemove() {
  const target = removal.value
  if (!target || deleting.value) return
  deleting.value = true
  deleteError.value = ''
  try {
    if (target.kind === 'uploaded') {
      await deleteServerPhoto(target.photo)
      updatePhotos(localPhotos.value.filter(value => value.id !== target.photo.id))
    } else {
      // Invalidate FIRST, then wait for the task's final ID, then remove it.
      target.job.controller.abort()
      await target.job.task
      if (target.job.uploaded) await deleteServerPhoto(target.job.uploaded)
      if (!disposed) removeJob(target.job)
    }
    if (!disposed) { removeOpen.value = false; removal.value = null }
  } catch (error) {
    if (disposed) return
    deleteError.value = errorMessage(error)
    if (target.kind === 'pending') {
      target.job.status = 'failed'
      target.job.deleteFailed = true
      target.job.message = '照片删除未完成'
      target.job.error = deleteError.value
    }
  } finally { if (!disposed) deleting.value = false }
}
''' + s[end:]
start, end = s.index('onBeforeUnmount('), s.index('\n</script>')
s = s[:start] + '''onBeforeUnmount(() => {
  disposed = true
  for (const job of jobs.value) {
    job.controller.abort()
    // Only unpublished jobs are in this queue. If best-effort cleanup fails,
    // existing server orphan maintenance retains its seven-day safety window.
    void job.task?.then(async () => { if (job.uploaded) await deleteServerPhoto(job.uploaded) }).catch(() => {})
  }
  emit('busy', false)
  emit('failed', false)
})
''' + s[end:]
s = s.replace('@click="runJob(job)"', '@click="job.deleteFailed ? askRemove({ kind: \'pending\', job }) : startJob(job)"')
s = s.replace('variant="link" label="重试" :disabled="disabled || deleting"', 'variant="link" :label="job.deleteFailed ? \'重试删除\' : \'重试\'" :disabled="disabled || deleting"')
s = s.replace('{{ failedCount }} 张未上传，请重试或移除', '{{ failedCount }} 张未完成，请重试或移除')
p.write_text(s)

# Freeze each control explicitly, including portalled Nuxt UI selectors.
def freeze_controls(text, expression, model_prefix=None):
    def edit(match):
        tag = match.group(0)
        if ':disabled=' in tag or (model_prefix and ('v-model="' + model_prefix) not in tag):
            return tag
        return tag[:-2] + f' :disabled="{expression}" />'
    return re.sub(r'<(?:UInput|USelect|UTextarea)\b[^>]*?/>', edit, text)

p = Path('web/app/components/CampaignEditor.vue')
s = p.read_text()
s = s.replace('const photosBusy = computed', 'const phaseFailed = reactive({ before: false, process: false, after: false })\nconst photosFailed = computed(() => Object.values(phaseFailed).some(Boolean))\nconst formDisabled = computed(() => loading.value || saving.value)\nconst photosBusy = computed')
s = freeze_controls(s, 'formDisabled')
s = s.replace('if (!completed && (photosBusy.value || Boolean', 'if (!completed && (saving.value || photosBusy.value || Boolean')
s = s.replace('onBeforeRouteLeave(() => {\n', 'onBeforeRouteLeave(() => {\n  if (saving.value) return false\n')
for phase in ['before','process','after']:
    s = s.replace('@busy="phaseBusy.' + phase + ' = $event"', '@busy="phaseBusy.' + phase + ' = $event" @failed="phaseFailed.' + phase + ' = $event"')
s = s.replace("{{ photosBusy ? '照片上传中'", "{{ photosFailed ? '有照片未完成，请重试或移除' : photosBusy ? '照片处理中'")
p.write_text(s)

p = Path('web/app/pages/inspections/new.vue')
s = p.read_text()
s = freeze_controls(s, 'formDisabled', 'draft.newPoint.')
s = s.replace('v-model="draft.noticeNumber" :maxlength="200"', 'v-model="draft.noticeNumber" :maxlength="100"')
s = s.replace('const uploadsBusy = computed', 'const overallPhotosFailed = ref(false)\nconst issuePhotosFailed = reactive<Record<string, boolean>>({})\nconst photosFailed = computed(() => overallPhotosFailed.value || draft.issues.some(issue => issuePhotosFailed[issue.key]))\nconst uploadsBusy = computed')
s = s.replace('@busy="issuePhotosBusy[issue.key] = $event"', '@busy="issuePhotosBusy[issue.key] = $event" @failed="issuePhotosFailed[issue.key] = $event"')
s = s.replace('@busy="overallPhotosBusy = $event"', '@busy="overallPhotosBusy = $event" @failed="overallPhotosFailed = $event"')
s = s.replace("{{ uploadsBusy ? '照片上传中'", "{{ photosFailed ? '有照片未完成，请重试或移除' : uploadsBusy ? '照片处理中'")
p.write_text(s)

p = Path('web/app/pages/inspections/[id].vue')
s = p.read_text()
for prefix, expression in [('editState.', 'editing'), ('issueState.', 'issueEditing'), ('returnReason', 'returning')]:
    s = freeze_controls(s, expression, prefix)
s = s.replace('v-model="editState.noticeNumber" :maxlength="200"', 'v-model="editState.noticeNumber" :maxlength="100"')
s = s.replace('if (validateEdit().length) return;', 'if (editing.value || validateEdit().length) return;')
s = s.replace('if (!issueToEdit.value || validateIssue().length) return;', 'if (issueEditing.value || !issueToEdit.value || validateIssue().length) return;')
s = s.replace('if (!issueToReturn.value) return;', 'if (returning.value || !issueToReturn.value) return;')
s = s.replace('description="删除后，问题、办理记录和照片无法恢复"', ':description="detail?.inspection.pointTemporary ? \'删除后，问题、办理记录和照片无法恢复；若这是该临时点位最后一条排查，该临时点位及其点位照片也将删除。\' : \'删除后，问题、办理记录和照片无法恢复\'"')
s = s.replace('if (batchBusy.value || editing.value || issueEditing.value)', 'if (batchBusy.value || editing.value || issueEditing.value || returning.value || deleting.value)')
s = s.replace('onBeforeRouteLeave(() => !batchBusy.value && !editing.value && !issueEditing.value)', 'onBeforeRouteLeave(() => !batchBusy.value && !editing.value && !issueEditing.value && !returning.value)')
p.write_text(s)

p = Path('web/app/components/InspectionBatch.vue')
s = p.read_text()
s = s.replace('busy: boolean }', 'busy: boolean; failed: boolean }')
s = s.replace("const saving = ref(false), error = ref('')", "const saving = ref(false), error = ref(''), discardOpen = ref(false)\nlet initialState = ''\nlet leaveResolve: ((value: boolean) => void) | undefined\nfunction fingerprint() { return JSON.stringify({ date: state.date, items: state.items.map(item => ({ id: item.issueId, selected: item.selected, text: item.text, photos: item.photos.map(photo => photo.id) })) }) }\nconst dirty = computed(() => fingerprint() !== initialState)\nconst photosFailed = computed(() => state.items.some(item => item.failed))\nfunction requestClose() { if (busy.value) return; if (dirty.value) discardOpen.value = true; else open.value = false }\nconst modalOpen = computed({ get: () => open.value, set: value => { if (value) open.value = true; else requestClose() } })\nfunction discard(confirmed: boolean) { discardOpen.value = false; if (confirmed) open.value = false; leaveResolve?.(confirmed); leaveResolve = undefined }\nonBeforeRouteLeave(() => {\n  if (!open.value) return true\n  if (busy.value) return false\n  if (!dirty.value) return true\n  discardOpen.value = true\n  return new Promise<boolean>(resolve => { leaveResolve = resolve })\n})")
s = s.replace("text: '', photos: [], busy: false }))", "text: '', photos: [], busy: false, failed: false }))\n  initialState = fingerprint()")
s = s.replace("onBeforeUnmount(() => emit('busy', false))", "onBeforeUnmount(() => { emit('busy', false); leaveResolve?.(false) })")
s = s.replace('<UModal v-model:open="open"', '<UModal v-model:open="modalOpen"')
s = s.replace('@click="open = false"', '@click="requestClose"')
s = s.replace('@busy="item.busy = $event"', '@busy="item.busy = $event" @failed="item.failed = $event"')
s = s.replace('<span v-if="uploading" class="mr-auto text-xs text-muted">照片上传中</span>', '<span v-if="uploading" class="mr-auto text-xs text-muted">{{ photosFailed ? \'有照片未完成，请重试或移除\' : \'照片处理中\' }}</span>')
s = s.replace('\n</template>', '''
  <UModal v-model:open="discardOpen" title="放弃未提交内容？" :description="`未提交的${mode === 'feedback' ? '反馈' : '复查'}内容将丢失`" :dismissible="false" :close="false">
    <template #footer><UButton label="继续编辑" color="neutral" variant="outline" @click="discard(false)" /><UButton label="放弃" color="error" @click="discard(true)" /></template>
  </UModal>
</template>''')
p.write_text(s)

Path('server/StreetEcoTool.Tests/PointMutationTests.cs').write_text('''using Xunit;

namespace StreetEcoTool.Tests;

public sealed class PointMutationTests
{
    [Fact]
    public void PhotoAndStatusWritesCannotReplayAnOldPointSnapshot()
    {
        using var environment = new V2Environment();
        using var store = environment.Open();
        var point = environment.CreatePoint(store);
        var newer = store.SavePoint(point.Id, new("新名称", point.CommunityId, point.TypeId, "新地址",
            Contact: "新联系人", Phone: "新电话", Frequency: "monthly", Notes: "新备注", Disabled: true,
            Longitude: 88.893426, Latitude: 29.274331));
        var photo = store.UploadPhoto(WordExporterTests.Jpeg(120, 80), "point");
        var associated = store.AttachPointPhotos(point.Id, new([photo.Id]));
        Assert.Equal(newer with { PhotoCount = 1, CoverPhotoId = photo.Id }, associated);
        Assert.Equal(associated with { Disabled = false }, store.SetPointDisabled(point.Id, new(false)));
        Assert.Equal(associated with { Disabled = false }, store.AttachPointPhotos(point.Id, new([photo.Id])));
        Assert.Throws<ApiException>(() => store.SetPointDisabled(point.Id, new(null)));
        V2Environment.AssertForeignKeys(store.DatabasePath);
    }

    [Fact]
    public async Task ConcurrentPhotoCompletionsKeepBothAssociations()
    {
        using var environment = new V2Environment();
        using var store = environment.Open();
        var point = environment.CreatePoint(store);
        var a = store.UploadPhoto(WordExporterTests.Jpeg(120, 80), "point");
        var b = store.UploadPhoto(WordExporterTests.Jpeg(80, 120), "point");
        await Task.WhenAll(Task.Run(() => store.AttachPointPhotos(point.Id, new([a.Id]))),
            Task.Run(() => store.AttachPointPhotos(point.Id, new([a.Id, b.Id]))));
        Assert.Equal(2, store.GetPoint(point.Id).PhotoCount);
        Assert.Equal(a.Id, store.GetPoint(point.Id).CoverPhotoId);
        Assert.Equal(new[] { a.Id, b.Id }, store.GetPointDetail(point.Id).Photos!.Select(photo => photo.Id));
    }

    [Fact]
    public void InvalidPhotoListsRollBackWithoutChangingPointMetadataOrOwnership()
    {
        using var environment = new V2Environment();
        using var store = environment.Open();
        var point = environment.CreatePoint(store);
        var community = store.SaveCommunity(null, new("另一社区", "另一社区居委会"));
        var other = store.SavePoint(null, new("另一点位", community.Id, point.TypeId, "另一地址"));
        var photos = Enumerable.Range(0, 10).Select(_ => store.UploadPhoto(WordExporterTests.Jpeg(120, 80), "point")).ToArray();
        var wrongPhase = store.UploadPhoto(WordExporterTests.Jpeg(120, 80), "site");
        Assert.Throws<ApiException>(() => store.AttachPointPhotos(point.Id, new([photos[0].Id, photos[0].Id])));
        Assert.Throws<ApiException>(() => store.AttachPointPhotos(point.Id, new([photos[0].Id, wrongPhase.Id])));
        Assert.Throws<ApiException>(() => store.AttachPointPhotos(point.Id, new(photos.Select(photo => photo.Id).ToArray())));
        Assert.Equal(point, store.GetPoint(point.Id));
        store.AttachPointPhotos(other.Id, new([photos[9].Id]));
        Assert.Throws<ApiException>(() => store.AttachPointPhotos(point.Id, new([photos[0].Id, photos[9].Id])));
        Assert.Equal(point, store.GetPoint(point.Id));
        Assert.Equal(1, store.GetPoint(other.Id).PhotoCount);
        store.AttachPointPhotos(point.Id, new(photos.Take(9).Select(photo => photo.Id).ToArray()));
        Assert.Equal(9, store.GetPoint(point.Id).PhotoCount);
        Assert.Throws<ApiException>(() => store.AttachPointPhotos("missing", new([])));
        V2Environment.AssertForeignKeys(store.DatabasePath);
    }
}
''')

Path('docs/optimization').mkdir(parents=True, exist_ok=True)
Path('docs/optimization/DECISIONS.md').write_text('''# 优化实施边界

依据：2026-10-13 项目实施任务书、DOCX 专项提示词及审计报告。业务基线：05eba126e7b9f3de13e5a2b055624ac6f97ee76d。

D01 整治台账继续摘要，全文见记录单；D02 通知单正文期限仍取该次最晚期限，表内逐项期限不变；D03 退回后补录日期规则不变；D04 台账仍独立选择范围，不自动继承未支持的列表筛选。四项业务决策待用户确认，本轮仅修明确问题。

默认 ILogger 级别改为 Warning（含 Error/Critical），保留必要的启动地址及配置/故障控制台信息。用户提到额外两项小修改，但仅给出这一项，第二项未推定。

所有验证使用隔离目录和合成数据，未访问生产部署目录。移除历史兼容不等于删除旧数据；当前备份、事务、外键、照片归属和中断恢复保护必须保留。
''')
Path('/tmp/hbtool-stage-message.txt').write_text('fix: F01-F05/F08 protect point metadata and asynchronous form operations; default warning logs\n')
print('Stage 1 edits applied; compilation, regression and AOT follow before commit.')
