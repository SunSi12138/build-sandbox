from pathlib import Path
p=Path('web/optimization-browser-test.mjs');s=p.read_text();assert s.count('street-eco.campaign-draft.v2')==1
s=s.replace('street-eco.campaign-draft.v2','street-eco-campaign-draft-v2')
needle="    assert.deepEqual(errors, [], 'Optimization cases caused unhandled browser errors')"
assert s.count(needle)==1
s=s.replace(needle,Path('scripts/draft-measure-stage.txt').read_text()+'\n'+needle)
p.write_text(s)
p=Path('web/app/pages/inspections/new.vue');s=p.read_text();old="    localStorage.removeItem('street-eco.inspection-draft.v1')\n";assert s.count(old)==1;p.write_text(s.replace(old,''))
Path('/tmp/hbtool-stage-message.txt').write_text('test: measure 50-issue draft persistence and correct campaign draft contract\n')
