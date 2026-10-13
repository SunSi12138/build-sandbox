#!/usr/bin/env python3
"""Render real API exports locally. Conversion/text success is NOT visual acceptance.

Review every generated page separately; Word/WPS GUI remain independent gates.
No font files or production dependencies are copied into output.
"""
from __future__ import annotations
import argparse, hashlib, json, os, re, subprocess, tempfile, time
from pathlib import Path


def sha(path): return hashlib.sha256(path.read_bytes()).hexdigest()
def norm(text):
    text = re.sub(r'^\s*[—–-]\s*\d+\s*[—–-]\s*$', '', text, flags=re.M)
    return re.sub(r'\s+', '', text)

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('samples',type=Path)
    parser.add_argument('--dpi',type=int,default=120)
    args=parser.parse_args(); root=args.samples.resolve(); manifest=json.loads((root/'samples.json').read_text())
    assert manifest['passed'], 'Do not render a failed fixture set as accepted evidence'
    assert 72 <= args.dpi <= 200
    pdfdir=root/'pdf'; pagesdir=root/'pages';pdfdir.mkdir(exist_ok=True);pagesdir.mkdir(exist_ok=True)
    report={'sourceRef':manifest['sourceRef'],'executableSha256':manifest['executableSha256'],'samplesManifestSha256':sha(root/'samples.json'),'dpi':args.dpi,'commands':[],'documents':[],'visualReview':'pending','wordGUI':'not-run','wpsGUI':'not-run'}
    def run(command):
        began=time.perf_counter()
        result=subprocess.run(command,text=True,stdout=subprocess.PIPE,stderr=subprocess.STDOUT,timeout=180)
        report['commands'].append({'command':command,'exitCode':result.returncode,'seconds':round(time.perf_counter()-began,3),'output':result.stdout})
        result.check_returncode();return result.stdout
    os.environ['FONTCONFIG_FILE'] = str(Path(__file__).with_name('word-fonts.conf').resolve())
    report['fontConfigSha256'] = sha(Path(os.environ['FONTCONFIG_FILE']))
    report['renderer']=run(['libreoffice','--version']).strip()
    report['poppler']=run(['pdftoppm','-v']).strip()
    fonts=run(['fc-match','-f','%{file}\n%{family}\n','仿宋']).strip().splitlines()
    report['fallbackFont']={'family':fonts[-1],'sha256':sha(Path(fonts[0]))}
    report['fontMatches']={name:run(['fc-match','-f','%{family}\n',name]).strip() for name in ['方正小标宋简体','黑体','宋体','仿宋']}
    documents=sorted((root/'docx').glob('*.docx'))
    try:
        with tempfile.TemporaryDirectory(prefix='hbtool-lo-') as profile:
            run(['libreoffice','-env:UserInstallation='+Path(profile).as_uri(),'--headless','--convert-to','pdf','--outdir',str(pdfdir),*[str(p) for p in documents]])
        expected={s['name']:s for s in manifest['samples']}
        review=['# DOCX 页面审阅清单','',f"引擎：{report['renderer']}；生产程序 SHA256：{report['executableSha256']}",'','自动检查不替代目视审阅。Word/WPS 图形界面另行验收。','','| 文档 | 页 | 图片 | 审阅状态 |','|---|---:|---|---|']
        for source in documents:
            pdf=pdfdir/(source.stem+'.pdf'); assert pdf.exists() and pdf.stat().st_size>0, source
            info=run(['pdfinfo',str(pdf)]);count=int(re.search(r'^Pages:\s+(\d+)',info,re.M).group(1))
            textpath=pdf.with_suffix('.txt');run(['pdftotext','-layout','-enc','UTF-8',str(pdf),str(textpath)])
            rawpath=pdf.with_suffix('.raw.txt');run(['pdftotext','-raw','-enc','UTF-8',str(pdf),str(rawpath)])
            text=textpath.read_text(); rawtext=rawpath.read_text(); fields=expected.get(source.stem,{}).get('expectedFields',[])
            for field in fields: assert norm(str(field)) in norm(text) or norm(str(field)) in norm(rawtext),(source.name,'rendered field missing',str(field)[:100])
            target=pagesdir/source.stem;target.mkdir(exist_ok=True)
            for previous in target.glob('page-*.png'): previous.unlink()
            run(['pdftoppm','-png','-r',str(args.dpi),str(pdf),str(target/'page')])
            images=sorted(target.glob('page-*.png'), key=lambda p:int(p.stem.split('-')[-1])); assert len(images)==count,(source,len(images),count)
            pages=[]
            for index,image in enumerate(images,1):
                pages.append({'page':index,'image':str(image.relative_to(root)),'sha256':sha(image),'review':'pending'})
                review.append(f'| {source.name} | {index} | {image.relative_to(root)} | 待目视审阅 |')
            report['documents'].append({'name':source.name,'docxSha256':sha(source),'pdfSha256':sha(pdf),'pageCount':count,'renderedFieldChecks':len(fields),'pages':pages})
        report['conversionPassed']=True
        (root/'REVIEW.md').write_text('\n'.join(review)+'\n')
    finally:
        (root/'render-report.json').write_text(json.dumps(report,ensure_ascii=False,indent=2))
    print(json.dumps({'conversionPassed':True,'documents':len(documents),'pages':sum(d['pageCount'] for d in report['documents']),'visualReview':'pending'}))

if __name__=='__main__':main()
