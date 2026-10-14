from pathlib import Path
import subprocess, runpy
# This temporary, guarded applicator is removed from the final source commit.
expected = {
 'web/app/components/CampaignEditor.vue':'928afb786592a69ad03c369462136e329a3998b4',
 'web/app/components/LedgerExport.vue':'d2dfddaf400162c648f621063f216dec30ff6ad7',
 'web/app/pages/inspections/new.vue':'64cd0106d9919f3e4a437708eb919db9ce060614',
 'web/optimization-browser-test.mjs':'da807f8b40f4dacd8fc6bbde8cb0229584a7c5ed',
}
for path, sha in expected.items():
 actual = subprocess.check_output(['git','hash-object',path],text=True).strip()
 assert actual == sha, (path, actual, sha)
for part in range(1,5): runpy.run_path(f'scripts/round3-front{part}.py')
f=Path('web/app/components/PointEditor.vue');s=f.read_text()
s=s.replace('<NameTagsInput v-model="state.contact"', '<NameTagsInput :disabled="loading || saving" v-model="state.contact"')
f.write_text(s)
for file in Path('scripts').glob('round3-*'): file.unlink()
Path('.github/workflows/round3-apply.yml').unlink()
Path('/tmp/hbtool-round3-message.txt').write_text('feat: finish point-linked campaigns, point ledgers and compact field editing\n')
subprocess.run(['git','diff','--check'],check=True)
