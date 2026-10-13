from pathlib import Path

p = Path('web/optimization-browser-test.mjs')
s = p.read_text()
old = "const leave = page.getByRole('dialog', { name: '离开页面？', exact: true })"
new = "const leave = page.getByRole('dialog', { name: '照片尚未上传完成', exact: true })"
assert s.count(old) == 1
p.write_text(s.replace(old, new))
Path('/tmp/hbtool-stage-message.txt').write_text('test: target the existing upload interruption confirmation in F02 regression\n')
