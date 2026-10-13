from pathlib import Path
import subprocess

# Development-only formatter. No application dependency or lockfile is changed.
files = [
    'web/app/pages/inspections/[id].vue',
    'web/app/pages/points/[id].vue',
    'web/app/pages/campaigns/index.vue',
    'web/app/components/CampaignEditor.vue',
]
subprocess.run(['npx', '--yes', 'prettier@3.6.2', '--write', '--single-quote', '--no-semi', '--print-width', '100', *files], check=True)
Path('/tmp/hbtool-stage-message.txt').write_text('style: F18 format four Vue sources without changing business behavior\n')
