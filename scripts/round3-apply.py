from pathlib import Path
import base64, hashlib, lzma, subprocess
parts = ''.join(p.read_text().strip() for p in sorted(Path('scripts').glob('round3-part-*.b64')))
patch = lzma.decompress(base64.b64decode(parts))
assert hashlib.sha256(patch).hexdigest() == '577b704e16706d96d137378d82bd1c316b2cdd6156867e476397a67a2ab3b8d6'
subprocess.run(['git', 'apply', '--check', '-'], input=patch, check=True)
subprocess.run(['git', 'apply', '-'], input=patch, check=True)
Path('/tmp/hbtool-round3-message.txt').write_text('feat: bind campaigns to points and filter existing ledgers by point\n')
