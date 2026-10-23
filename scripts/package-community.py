"""Package only runtime assets; never include local business/test data."""
import argparse
import hashlib
from pathlib import Path
import zipfile

parser=argparse.ArgumentParser()
parser.add_argument('--app',type=Path,required=True)
parser.add_argument('--output',type=Path,required=True)
args=parser.parse_args()
app=args.app.resolve()
files=[app/'CommunityGIS.exe',app/'启动社区管理.cmd',app/'开始使用.txt']
for folder in ['wwwroot','tiles']:
    files.extend(p for p in (app/folder).rglob('*') if p.is_file())
if not (app/'wwwroot/index.html').is_file() or not (app/'tiles/jlyh').is_dir():
    raise SystemExit('Missing frontend or Jilin map cache')
args.output.parent.mkdir(parents=True,exist_ok=True)
with zipfile.ZipFile(args.output,'w',zipfile.ZIP_DEFLATED,compresslevel=6) as archive:
    for file in files:
        if file.is_symlink(): raise ValueError('Unexpected symlink: '+str(file))
        archive.write(file,'CommunityGIS/'+file.relative_to(app).as_posix())
    archive.writestr('CommunityGIS/appsettings.json','{"port":5180,"listen":["127.0.0.1"]}')
checksum=hashlib.sha256(args.output.read_bytes()).hexdigest()
args.output.with_suffix('.zip.sha256').write_text(checksum+'  '+args.output.name+'\n',encoding='utf-8')
print(f'{args.output} ({args.output.stat().st_size:,} bytes; {len(files)+1} files)')
