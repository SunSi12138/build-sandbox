from pathlib import Path
p=Path('web/optimization-browser-test.mjs');s=p.read_text();old="page.getByRole('button', { name: '返回', exact: true }).first().click()";assert s.count(old)==1;p.write_text(s.replace(old,"page.getByRole('link', { name: '返回', exact: true }).first().click()"))
p=Path('tests/word-layout-samples.py');s=p.read_text();needle='        if self.optimized:\n            before=self.json'
extra='''        # Capture the newly configured long title in a NEW inspection, not the old snapshot.
        longname = "长名称" + "甲" * 197
        longpoint = self.create_point(longname)
        longrecord = self.json('POST', '/api/inspections', self.inspection_body(longpoint['id'], issues=[self.issue_body('长名称和抬头样本', self.today)]), expected=201)
        self.check_sample(f"/api/inspections/{longrecord['inspection']['id']}/export/notice", 'notice-long-title', [longname, longtitle, '长名称和抬头样本'])
'''
assert s.count(needle)==1;p.write_text(s.replace(needle,extra+needle))
Path('/tmp/hbtool-stage-message.txt').write_text('test: use actual navigation link and cover a newly captured long community title\n')
