from pathlib import Path


def edit(path, old, new, count=1):
    p = Path(path); text = p.read_text()
    assert text.count(old) == count, (path, old[:90], text.count(old))
    p.write_text(text.replace(old, new))


# F07: an independent, fully visible query; do not invent inheritance of filters.
Path('web/app/components/LedgerExport.vue').write_text('''<script setup lang="ts">
import { errorMessage, todayLocal } from '~/utils/format'
import { downloadWord } from '~/utils/wordDownload'

const open = defineModel<boolean>('open', { default: false })
const props = defineProps<{ kind: 'inspection' | 'campaign'; communityId?: string; typeId?: string }>()
const { bootstrap, load } = useAppData()
const kind = ref<'inspection' | 'rectification'>('inspection')
const range = ref('month')
const from = ref('')
const to = ref('')
const community = ref('all')
const campaignType = ref('all')
const photos = ref(false)
const busy = ref(false)
const loading = ref(false)
const error = ref('')
const disabled = computed(() => busy.value || loading.value)
const communities = computed(() => [{ label: '全部', value: 'all' }, ...(bootstrap.value?.communities ?? []).map(item => ({ label: item.name, value: item.id }))])
const campaignTypes = computed(() => [{ label: '全部', value: 'all' }, ...(bootstrap.value?.campaignTypes ?? []).map(item => ({ label: item.name, value: item.id }))])
const ranges = [{ label: '本月', value: 'month' }, { label: '本季', value: 'quarter' }, { label: '本年', value: 'year' }, { label: '自定义', value: 'custom' }]
const canIncludePhotos = computed(() => props.kind === 'campaign' || kind.value === 'rectification')
function selectRange(value: string) {
  range.value = value
  if (value === 'custom') return
  const today = bootstrap.value?.today ?? todayLocal()
  const [year, month] = today.split('-').map(Number)
  const firstMonth = value === 'year' ? 1 : value === 'quarter' ? Math.floor(((month ?? 1) - 1) / 3) * 3 + 1 : month ?? 1
  const lastMonth = value === 'year' ? 12 : value === 'quarter' ? firstMonth + 2 : firstMonth
  const lastDay = new Date(year ?? 2026, lastMonth, 0).getDate()
  from.value = `${year}-${String(firstMonth).padStart(2, '0')}-01`
  to.value = `${year}-${String(lastMonth).padStart(2, '0')}-${String(lastDay).padStart(2, '0')}`
}
watch(open, async value => {
  if (!value) return
  error.value = ''
  community.value = props.communityId || 'all'
  campaignType.value = props.typeId || 'all'
  photos.value = false
  loading.value = true
  try { await load(); selectRange('month') }
  catch (cause) { error.value = errorMessage(cause) }
  finally { loading.value = false }
})
async function exportLedger() {
  if (disabled.value) return
  if (!from.value || !to.value || from.value > to.value) { error.value = '请选择正确的时间范围'; return }
  busy.value = true
  error.value = ''
  const name = props.kind === 'campaign' ? '整治台账' : kind.value === 'inspection' ? '排查台账' : '整改台账'
  const route = props.kind === 'campaign' ? 'campaign-ledger' : `${kind.value}-ledger`
  const query = new URLSearchParams({ from: from.value, to: to.value })
  if (community.value !== 'all') query.set('communityId', community.value)
  if (props.kind === 'campaign' && campaignType.value !== 'all') query.set('typeId', campaignType.value)
  if (canIncludePhotos.value) query.set('photos', String(photos.value))
  try { await downloadWord(`/api/exports/${route}?${query}`, `${name}.docx`); open.value = false }
  catch (cause) { error.value = errorMessage(cause) }
  finally { busy.value = false }
}
</script>

<template>
  <UModal v-model:open="open" title="导出台账" :dismissible="!disabled" :close="!disabled" :ui="{ content: 'mobile-dialog max-w-[560px]' }">
    <template #body>
      <div class="space-y-3">
        <UAlert v-if="error" :description="error" color="error" variant="subtle" />
        <p class="text-xs text-muted" data-testid="ledger-scope-help">{{ props.kind === 'campaign' ? '导出使用下方日期、社区及整治类型，列表搜索词不参与。' : '导出使用下方日期和社区，列表搜索、点位、类型及状态筛选不参与。' }}</p>
        <UFormField v-if="props.kind === 'inspection'" label="台账类型">
          <div class="chip-row">
            <UButton v-for="option in [{ label: '排查台账', value: 'inspection' as const }, { label: '整改台账', value: 'rectification' as const }]" :key="option.value" color="neutral" variant="ghost" class="filter-chip" :aria-pressed="kind === option.value" :data-testid="`ledger-kind-${option.value}`" :disabled="disabled" @click="kind = option.value">{{ option.label }}</UButton>
          </div>
        </UFormField>
        <UFormField label="时间范围" help="日期单独选择，默认本月">
          <div class="chip-row"><UButton v-for="option in ranges" :key="option.value" color="neutral" variant="ghost" class="filter-chip" :aria-pressed="range === option.value" :disabled="disabled" @click="selectRange(option.value)">{{ option.label }}</UButton></div>
        </UFormField>
        <p v-if="range !== 'custom'" class="text-sm" data-testid="ledger-dates">{{ from }} 至 {{ to }}</p>
        <div v-else class="field-grid">
          <UFormField label="开始日期"><UInput v-model="from" type="date" class="w-full" :max="to || undefined" :disabled="disabled" data-testid="ledger-from" /></UFormField>
          <UFormField label="结束日期"><UInput v-model="to" type="date" class="w-full" :min="from || undefined" :disabled="disabled" data-testid="ledger-to" /></UFormField>
        </div>
        <UFormField label="所属社区"><USelect v-model="community" :items="communities" class="w-full" :disabled="disabled" data-testid="ledger-community" /></UFormField>
        <UFormField v-if="props.kind === 'campaign'" label="整治类型"><USelect v-model="campaignType" :items="campaignTypes" class="w-full" :disabled="disabled" data-testid="ledger-campaign-type" /></UFormField>
        <div v-if="canIncludePhotos" class="check-row"><UCheckbox v-model="photos" label="附照片" :disabled="disabled" data-testid="ledger-photos" /></div>
      </div>
    </template>
    <template #footer>
      <UButton label="取消" color="neutral" variant="outline" :disabled="disabled" @click="open = false" />
      <UButton label="导出 Word" :loading="busy" :disabled="loading" data-testid="export-ledger" @click="exportLedger" />
    </template>
  </UModal>
</template>
''')
edit('web/app/types/api.ts', 'inspectionCount: number; issueCount: number; expandedBytes:', 'inspectionCount: number; issueCount: number; campaignCount: number; expandedBytes:')
edit('web/app/components/SettingsBackups.vue', '<div><dt>问题</dt><dd>{{ preview.issueCount }}</dd></div>', '<div><dt>问题</dt><dd>{{ preview.issueCount }}</dd></div>\n          <div><dt>整治</dt><dd data-testid="restore-campaign-count">{{ preview.campaignCount }}</dd></div>')
edit('web/app/pages/settings.vue', "const placeholders = ['{日期}', '{点位}', '{问题}', '{期限}']", "const placeholders = ['{日期}', '{点位}', '{问题}', '{期限}', '{街道全称}', '{社区}']")
edit('web/app/pages/settings.vue', "  if (!state.noticeTemplate.trim())", "  if (state.noticeTemplate.split('{问题}').length > 2) errors.push({ name: 'noticeTemplate', message: '{问题} 最多只能出现一次' })\n  if (!state.noticeTemplate.trim())")
edit('web/app/pages/settings.vue', '<div class="mt-3 flex flex-wrap', '<p class="mt-3 text-xs text-muted">{问题} 为问题表格，最多一次；不填写时在正文后补表。</p>\n          <p class="mt-2 text-xs text-muted">示例：经{街道全称}于{日期}对{社区}辖区{点位}排查，发现以下问题：</p>\n          <div class="mt-3 flex flex-wrap')
# F09: validate the original template and substitute scalar values in one pass.
# A point's legitimate name containing literal braces is not a second template.
p = Path('server/WordExporter.cs'); s = p.read_text()
a = s.index('        var template = settings.NoticeTemplate')
b = s.index('        for (var index = 0; index < parts.Length; index++)', a)
s = s[:a] + '        var parts = settings.NoticeTemplate.Split("{问题}", StringSplitOptions.None);\n' + s[b:]
s = s.replace('        using var document = new Document(destination, false, "整改通知单", settings.StreetName);', '        ValidateNoticeTemplate(settings.NoticeTemplate);\n        using var document = new Document(destination, false, "整改通知单", settings.StreetName);')
s = s.replace('parts[index].Replace("\\r\\n", "\\n", StringComparison.Ordinal)', 'ExpandNoticeText(parts[index], inspection, settings, deadline).Replace("\\r\\n", "\\n", StringComparison.Ordinal)')
s = s.replace('document.Paragraph(paragraph.Trim());', 'document.Paragraph(paragraph);')
a = s.index('    public static void WriteNotice(')
s = s[:a] + '''    internal static void ValidateNoticeTemplate(string template)
    {
        var first = template.IndexOf("{问题}", StringComparison.Ordinal);
        if (first >= 0 && template.IndexOf("{问题}", first + "{问题}".Length, StringComparison.Ordinal) >= 0)
            throw new ApiException(400, "通知单模板中的 {问题} 最多只能出现一次，请修改模板。");
    }

    private static string ExpandNoticeText(string template, InspectionDto inspection, SettingsDto settings, string deadline)
    {
        var result = new StringBuilder(template.Length);
        var position = 0;
        while (position < template.Length)
        {
            var start = template.IndexOf('{', position);
            var end = start < 0 ? -1 : template.IndexOf('}', start + 1);
            if (start < 0 || end < 0) { result.Append(template, position, template.Length - position); break; }
            result.Append(template, position, start - position);
            var token = template[start..(end + 1)];
            result.Append(token switch
            {
                "{日期}" => ChineseDate(inspection.Date), "{点位}" => inspection.PointName,
                "{期限}" => ChineseDate(deadline), "{街道全称}" => settings.StreetName,
                "{社区}" => inspection.CommunityName, _ => token
            });
            position = end + 1;
        }
        return result.ToString();
    }

''' + s[a:]
s = s.replace('"整治内容及成效"', '"整治内容及成效（摘要）"')
p.write_text(s)
edit('server/Store.Settings.cs', '            if (value.JpegQuality is < 1', '            WordExporter.ValidateNoticeTemplate(value.NoticeTemplate);\n            if (value.JpegQuality is < 1')
p = Path('server/StreetEcoTool.Tests/WordExporterTests.cs'); s = p.read_text()
s = '\n'.join(line for line in s.split('\n') if not ('[InlineData(' in line and '再次说明：{问题}' in line))
a = s.index('    [Fact]')
s = s[:a] + '''    [Fact]
    public void DuplicateProblemBlockIsRejectedWithoutReplacingStoredTemplate()
    {
        using var environment = new V2Environment();
        using var store = environment.Open();
        var original = store.GetSettings();
        var invalid = original with { NoticeTemplate = "正文{问题}再次{问题}" };
        Assert.Equal(400, Assert.Throws<ApiException>(() => store.SaveSettings(invalid)).StatusCode);
        Assert.Equal(original, store.GetSettings());
        using var destination = new MemoryStream();
        Assert.Throws<ApiException>(() => WordExporter.WriteNotice(destination, Inspection(), invalid, []));
        Assert.Equal(0, destination.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SixVariablesAndLegitimateBusinessTextArePreserved(bool explicitBlock)
    {
        var detail = Inspection();
        detail = detail with { Inspection = detail.Inspection with { PointName = "让居民了解{问题}的现场点位", Notes = "请妥善保管上传的现场原文。" } };
        var settings = Settings() with { NoticeTemplate = "{街道全称}：{社区}\\r\\n经{日期}对{点位}排查，A&B <原文>。\\n请于{期限}前妥善办理。" + (explicitBlock ? "\\n{问题}" : "") };
        using var notice = new Package(Render(stream => WordExporter.WriteNotice(stream, detail, settings, [])));
        ValidatePackage(notice);
        var text = Text(notice.Xml("word/document.xml"));
        Assert.Contains(settings.StreetName, text);
        Assert.Contains(detail.Inspection.CommunityName, text);
        Assert.Contains(detail.Inspection.PointName, text);
        Assert.Contains("A&B <原文>。", text);
        Assert.Contains("妥善办理。", text);
        foreach (var issue in detail.Issues) Assert.Single(Regex.Matches(text, Regex.Escape(issue.Description)));
        using var record = new Package(Render(stream => WordExporter.WriteRecord(stream, detail, settings, [])));
        Assert.Contains(detail.Inspection.Notes, Text(record.Xml("word/document.xml")));
    }

''' + s[a:]
p.write_text(s)
p = Path('tests/word-smoke.py'); s = p.read_text()
a = s.index('        visible_text = '); b = s.index('        relationships = ', a)
s = s[:a] + '        # Business prose is data: no global ordinary-word blacklist.\n' + s[b:]
p.write_text(s)
# Ensure the previous save's success navigation has settled before the next test.
p = Path('web/optimization-browser-test.mjs'); s = p.read_text()
s = s.replace('  async function beginInspection() {\n', "  async function beginInspection() {\n    await goto('/points')\n")
s = s.replace("      assert.equal(record.inspection.result, 'normal')", "      assert.equal(record.inspection.result, 'normal')\n      await page.waitForURL('**/#/inspections/' + record.inspection.id)")
p.write_text(s)
Path('docs/optimization/DECISIONS.md').write_text(Path('docs/optimization/DECISIONS.md').read_text() + '\nF07/F19：台账范围全部显示，日期保持独立默认本月；整治类型可见且可调整，界面说明未继承列表条件。恢复摘要增加整治数量。\nF09/F10：六变量完整提示；原模板重复问题块在保存及导出均报错，既有模板不重置；无问题块自动补表不变，普通业务用词及花括号按原文保留。\n')
Path('/tmp/hbtool-stage-message.txt').write_text('fix: F07/F09/F10/F19 explicit export scope, safe template blocks and restore counts\n')
