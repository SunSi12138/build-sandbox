from pathlib import Path
exec(compile(Path('scripts/word-stage-core.py').read_text(), 'scripts/word-stage-core.py', 'exec'))
p = Path('server/StreetEcoTool.Tests/WordStoreTests.cs')
s = p.read_text()
old = '        var text = DocumentText(ledger.FilePath);\n        Assert.Contains("待整改", text);'
new = '''        // Reassemble logical columns: continuation context between physical rows
        // must not make a complete historical field appear to be truncated.
        using var archive = ZipFile.OpenRead(ledger.FilePath);
        using var input = archive.GetEntry("word/document.xml")!.Open();
        var xml = XDocument.Load(input);
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var table = xml.Descendants(w + "tbl").Single(t => t.Element(w + "tblGrid")?.Elements().Count() == 9);
        var rows = table.Elements(w + "tr").Skip(1).SkipLast(1).ToArray();
        var text = string.Join("\\n", Enumerable.Range(0, 9).Select(column => string.Concat(rows.SelectMany(row => row.Elements(w + "tc").ElementAt(column).Descendants(w + "t")).Select(t => t.Value))))
            + string.Concat(table.Elements(w + "tr").Last().Descendants(w + "t").Select(t => t.Value));
        Assert.Contains("待整改", text);'''
assert s.count(old) == 1
p.write_text(s.replace(old, new))
p = Path('web/app/components/PhotoUploader.vue')
s = p.read_text()
old = "onBeforeUnmount(() => {\n  disposed = true\n  for (const job of jobs.value) {"
new = '''function cancelPendingJobs() {
  // Accepted navigation can precede unmount while Nuxt loads the next page.
  // Invalidate unpublished jobs before a late thumbnail completion can publish.
  const pending = [...jobs.value]
  for (const job of pending) job.controller.abort()
  jobs.value = []
  for (const job of pending) {'''
assert s.count(old) == 1
s = s.replace(old, new)
old = "  emit('busy', false)\n  emit('failed', false)\n})"
new = '''}
const router = useRouter()
watch(() => router.currentRoute.value.path, (next, previous) => {
  if (next !== previous) cancelPendingJobs()
}, { flush: 'sync' })
onBeforeUnmount(() => {
  disposed = true
  cancelPendingJobs()
  emit('busy', false)
  emit('failed', false)
})'''
assert s.count(old) == 1
p.write_text(s.replace(old, new))
Path('/tmp/hbtool-stage-message.txt').write_text('fix: W01-W05 contextual DOCX pagination and F02 accepted-navigation cancellation\n')
