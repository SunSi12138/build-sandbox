from pathlib import Path
import re,json
source=Path('E:/Map/wwwroot/index.html').read_text(encoding='utf-8-sig')
def value(key):
 m=re.search(r'\b'+key+r':(["\x27])([^"\x27]*)\1',source)
 if not m or not m.group(2):raise SystemExit('Missing provider configuration '+key)
 return m.group(2)
mk=value('jlyhApiMk');tk=value('jlyhApiTk');tdt=value('tdtApiSecret')
def layer(id,name,scheme,lo,hi,order,url,key,annotation=False,mapkey=''):
 return dict(id=id,name=name,folder=id,scheme=scheme,enabled=id=='jlyh',isDefault=id=='jlyh',opacity=1,minZoom=lo,maxZoom=hi,sortOrder=order,urlTemplate=url,apiKey=key,mapKey=mapkey,subdomains='0,1,2,3,4,5,6,7',updateCache=False,cacheVersion=0,annotation=annotation)
providers=[layer('jlyh','吉林一号','tms',0,18,0,'https://api.jl1mall.com/getMap/{z}/{x}/{y}?mk={mk}&tk={key}',tk,mapkey=mk),layer('tdt-cva','地名标注','xyz',0,18,10000,'https://t{s}.tianditu.gov.cn/cva_w/wmts?SERVICE=WMTS&REQUEST=GetTile&VERSION=1.0.0&LAYER=cva&STYLE=default&TILEMATRIXSET=w&FORMAT=tiles&TILEMATRIX={z}&TILEROW={y}&TILECOL={x}&tk={key}',tdt,True)]
Path('map-providers.local.json').write_text(json.dumps(providers,ensure_ascii=False,indent=2),encoding='utf-8')
print('Imported Jilin imagery and annotation configuration into local provider file; credential values omitted.')
p=Path('.gitignore');s=p.read_text(encoding='utf-8-sig');s+='\nmap-providers.local.json\n';p.write_text(s,encoding='utf-8')
p=Path('scripts/build-community.ps1');s=p.read_text(encoding='utf-8-sig');s=s.replace('$taskWeb=Join-Path',"if(Test-Path -LiteralPath (Join-Path $taskRoot 'map-providers.local.json')){Copy-Item -LiteralPath (Join-Path $taskRoot 'map-providers.local.json') -Destination $taskApp -Force}\n$taskWeb=Join-Path");p.write_text(s,encoding='utf-8')
p=Path('scripts/package-community.py');s=p.read_text(encoding='utf-8-sig').replace('import hashlib\n','');s=s.replace("for folder in ['wwwroot','tiles']:","if (app/'map-providers.local.json').is_file(): files.append(app/'map-providers.local.json')\nfor folder in ['wwwroot','tiles']:");s=re.sub(r'checksum=hashlib\.sha256.*?encoding=\x27utf-8\x27\)\n','',s,flags=re.S);p.write_text(s,encoding='utf-8')
