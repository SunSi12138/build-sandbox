#!/usr/bin/env python3
"""Generate real API layout samples; Pillow is test-only, never a runtime dependency."""
from __future__ import annotations
import argparse, datetime as dt, hashlib, importlib.util, io, json, re, shutil, traceback, zipfile
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont

spec = importlib.util.spec_from_file_location('word_smoke', Path(__file__).with_name('word-smoke.py'))
word = importlib.util.module_from_spec(spec)
spec.loader.exec_module(word)

def sha(data):
    return hashlib.sha256(data).hexdigest()

def normalized(value):
    return re.sub(r'\s+', '', value)

class Samples(word.WordSmoke):
    def __init__(self, *args, optimized=False, **kwargs):
        super().__init__(*args, **kwargs)
        self.optimized = optimized
        self.baseline_contract = not optimized
        self.images = {}
        self.sample_manifest = []
        self.sequence = 0

    def json(self, method, path, body=None, **kwargs):
        if getattr(self, 'baseline_contract', False) and method in ('POST', 'PUT') and path.split('?')[0] == '/api/campaigns' and body:
            body = dict(body)
            point_id = body.pop('pointId', None)
            if point_id:
                point = super().json('GET', '/api/points/' + point_id)['point']
                body['communityId'], body['location'] = point['communityId'], point['address']
        return super().json(method, path, body, **kwargs)

    def picture(self, phase, width=960, height=640):
        self.sequence += 1
        # Textured, numbered synthetic scene: not a user photo or a flat swatch.
        image = Image.new('RGB', (width, height))
        pixels = image.load()
        for y in range(height):
            for x in range(width):
                n = (x * 13 + y * 7 + self.sequence * 29) % 29
                pixels[x,y] = (min(255, 80 + x * 110 // width + n), min(255, 80 + y * 100 // height + n), 110 + n)
        draw = ImageDraw.Draw(image)
        font = ImageFont.truetype('DejaVuSans.ttf', max(14, min(width, height)//9))
        draw.rectangle((8,8,width-9,height-9), outline='white', width=4)
        for i in range(1,8):
            x = width*i//8
            draw.line((x,height//2,x,height-20), fill='black', width=2)
            draw.text((x, height//2), str(i), fill='white', font=font)
        draw.text((16,18), f'TEST {self.sequence:02d}\n{phase}\n{width} x {height}', fill='white', stroke_width=1, stroke_fill='black', font=font)
        output = io.BytesIO(); image.save(output, format='JPEG', quality=85)
        payload=output.getvalue()
        value=self.json('PUT', '/api/photos?phase='+phase, raw=payload, content_type='image/jpeg', expected=(200,201))
        assert (value['width'],value['height'])==(width,height)
        assert self.request('GET','/photos/'+value['id'])[1]==payload
        self.images[value['id']]={'sha256':sha(payload),'phase':phase,'width':width,'height':height,'syntheticNumber':self.sequence}
        return value['id']

    def check_sample(self, route, name, fields=(), images=(), optimized_terms=()):
        text, count, xml = self.download(route,name+'.docx')
        # Logical long-table columns are reconstructed separately by Word tests.
        for field in fields:
            assert normalized(str(field)) in normalized(text), (name, str(field)[:90])
        expected={self.images[i]['sha256'] for i in images}
        with zipfile.ZipFile(self.output/(name+'.docx')) as z:
            actual={sha(z.read(n)) for n in z.namelist() if n.startswith('word/media/')}
        assert expected==actual, (name,'photo bytes/ownership mismatch',len(expected),len(actual))
        if self.optimized:
            for term in optimized_terms: assert term in text, (name,term)
        self.sample_manifest.append({'name':name,'expectedFields':list(fields),'expectedPhotoHashes':sorted(expected),'expectedOptimizedTerms':list(optimized_terms),'fieldAndPhotoCheck':'passed'})
        return text

    def extra(self):
        self.community=self.json('POST','/api/communities',{'name':'专项样本社区','title':'专项样本社区居民委员会'},expected=201)
        self.point=self.create_point('专项河岸点位 A&B（合成样本）')
        pid=self.point['id']; date=self.today-dt.timedelta(days=4); iso=self.today.isoformat()
        note='请妥善保管，让每一项现场记录原样上传。A&B <现场> 全角，换行\n第二行原文。'
        body=self.inspection_body(pid);body['notes']=note
        normal=self.json('POST','/api/inspections',body,expected=201)
        self.check_sample(f"/api/inspections/{normal['inspection']['id']}/export/record",'normal-prose', [note],optimized_terms=['二、备注'])
        site=[[self.picture('site',960,640),self.picture('site',640,960),self.picture('site',1600,200)],[self.picture('site',200,1600)]]
        descriptions=['第一问题：河岸垃圾待清运，说明 A&B <原文>。','第二问题：围挡破损，检查 ABC123 全角标点。']
        body=self.inspection_body(pid,date=date,issues=[self.issue_body(descriptions[i],self.today,site[i]) for i in range(2)])
        record=self.json('POST','/api/inspections',body,expected=201);rid=record['inspection']['id'];issues=record['issues']
        self.check_sample(f'/api/inspections/{rid}/export/record','record-odd-photos',descriptions,[p for group in site for p in group])
        self.request('GET',f'/api/inspections/{rid}/export/rectification',expected=400)
        allphotos=[p for group in site for p in group];history=[]
        for round_no in (1,2):
            feedback_date=(self.today-dt.timedelta(days=1 if round_no==1 else 2)).isoformat()
            items=[]
            for i,issue in enumerate(issues):
                photos=[self.picture('after',640,960)]
                if i==0: photos.append(self.picture('after',960,640))
                allphotos+=photos
                text=f'问题{i+1}第{round_no}次反馈：清理完成，保留原文。'; history.append(text)
                items.append({'issueId':issue['id'],'notes':text,'photoIds':photos})
            self.json('POST',f'/api/inspections/{rid}/feedback',{'date':feedback_date,'items':items})
            items=[]
            for i,issue in enumerate(issues):
                photos=[self.picture('review',960,640)] if round_no==2 else []
                allphotos+=photos
                text=f'问题{i+1}第{round_no}次复查：逐项现场核实。'; history.append(text)
                items.append({'issueId':issue['id'],'conclusion':text,'photoIds':photos})
            self.json('POST',f'/api/inspections/{rid}/review',{'date':iso,'items':items})
            if round_no==1:
                for issue in issues: self.json('POST',f"/api/issues/{issue['id']}/return",{'reason':'现场重新堆放，退回后允许补录业务日期。'})
        self.check_sample(f'/api/inspections/{rid}/export/rectification','rectification-rounds',descriptions+history,allphotos,
                          ['第 1 次反馈','第 2 次反馈','第 2 次复查','问题 1','问题 2'])
        types=self.json('GET','/api/campaign-types')
        photos={k:[self.picture(phase)] for k,phase in [('beforePhotoIds','before'),('processPhotoIds','process'),('afterPhotoIds','after')]}
        campaign={'name':'整治短备注与三阶段照片','typeId':types[0]['id'],'pointId':pid,'date':iso,'content':'清理沿岸垃圾，检查排水口。','outcome':'','notes':'现场设置垃圾收集点。',**photos}
        detail=self.json('POST','/api/campaigns',campaign,expected=201)
        self.check_sample(f"/api/campaigns/{detail['campaign']['id']}/export/record",'campaign-short-note',[campaign['notes'],campaign['content']],[p for group in photos.values() for p in group],['二、备注','三、整治照片'])
        campaign.update(name='长备注、零数量与未填写数量',notes=note+'\n'+('沿河逐段检查，清理垃圾并记录整改结果。'*50),participantCount=0,vehicleCount=None,garbageTons=0,areaSqm=None)
        campaign['beforePhotoIds']=[self.picture('before',200,1600),self.picture('before',1600,200),self.picture('before')]
        campaign['processPhotoIds']=[];campaign['afterPhotoIds']=[self.picture('after')]
        detail=self.json('POST','/api/campaigns',campaign,expected=201)
        self.check_sample(f"/api/campaigns/{detail['campaign']['id']}/export/record",'campaign-long-note',[campaign['notes'],'参加人数（人）','清运垃圾（吨）'],campaign['beforePhotoIds']+campaign['afterPhotoIds'])
        query='?from='+date.isoformat()+'&to='+iso+'&communityId='+self.community['id']
        self.check_sample('/api/exports/inspection-ledger'+query,'inspection-mixed',['共 2 次排查，正常 1，发现问题 1；问题共 2 项，已闭环 2'])
        self.check_sample('/api/exports/rectification-ledger'+query+'&photos=true','ledger-rounds',descriptions,allphotos,['第 2 次反馈'])
        self.check_sample('/api/exports/campaign-ledger'+query,'campaign-quantities',['共 2 次整治','0.00'],optimized_terms=['整治内容及成效（摘要）'])
        for kind in ('inspection','rectification','campaign'):
            self.check_sample('/api/exports/'+kind+'-ledger?from=1900-01-01&to=1900-01-02','empty-'+kind,['共 0'])
        settings={**self.settings,'noticeTemplate':'经{街道全称}于{日期}检查{社区}辖区{点位}。\n{问题}\n'+('请妥善保管现场记录并逐项落实整改。'*40)+'\n请于{期限}前反馈。'}
        self.json('PUT','/api/settings',settings)
        self.json('PUT','/api/inspections/'+rid,{'date':date.isoformat(),'inspector':'专项人员','noticeNumber':'编号'+('甲'*98),'notes':note})
        longtitle='社区联合环保排查工作组'+('专项'*45)+'居民委员会'
        self.json('PUT','/api/communities/'+self.community['id'],{'name':self.community['name'],'title':longtitle})
        self.check_sample(f'/api/inspections/{rid}/export/notice','notice-long',descriptions+['编号'+('甲'*98),'专项样本社区居民委员会'],[p for group in site for p in group])
        self.json('PUT','/api/settings',{**settings,'noticeTemplate':'无问题块：请保留正文。{日期} {点位} {期限} {街道全称} {社区}'})
        self.check_sample(f'/api/inspections/{rid}/export/notice','notice-no-block',descriptions+['无问题块：请保留正文。'],[p for group in site for p in group])
        # Capture the newly configured long title in a NEW inspection, not the old snapshot.
        longname = "长名称" + "甲" * 197
        longpoint = self.create_point(longname)
        longrecord = self.json('POST', '/api/inspections', self.inspection_body(longpoint['id'], issues=[self.issue_body('长名称和抬头样本', self.today)]), expected=201)
        self.check_sample(f"/api/inspections/{longrecord['inspection']['id']}/export/notice", 'notice-long-title', [longname, longtitle, '长名称和抬头样本'])
        if self.optimized:
            before=self.json('GET','/api/settings')
            self.request('PUT','/api/settings',body={**settings,'noticeTemplate':'{问题}\n{问题}'},expected=400)
            assert self.json('GET','/api/settings')==before


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--executable',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--source-ref',required=True)
    parser.add_argument('--expect-optimized',action='store_true')
    args=parser.parse_args();root=args.output.resolve();root.mkdir(parents=True,exist_ok=True)
    suite=Samples(args.executable.resolve(),None,30,output=root/'docx',optimized=args.expect_optimized)
    success=False
    try:
        suite.run();suite.case('Synthetic layout matrix and exact field/photo preservation',suite.extra)
        success=True
    except Exception:
        suite.report['failure']=traceback.format_exc();raise
    finally:
        suite.stop();suite.log.close();shutil.copyfile(suite.log_path,root/'server.log')
        suite.report.update(passed=success,sourceRef=args.source_ref,executableSha256=sha(args.executable.read_bytes()),seed='hbtool-layout-v1',businessDate=str(getattr(suite,'today','')),images=suite.images,samples=suite.sample_manifest)
        suite.report['docx']=[{'name':p.name,'sha256':sha(p.read_bytes())} for p in sorted((root/'docx').glob('*.docx'))]
        (root/'samples.json').write_text(json.dumps(suite.report,ensure_ascii=False,indent=2));shutil.rmtree(suite.temp)
    print(json.dumps({'passed':success,'samples':len(suite.report['docx']),'requests':len(suite.report['requests'])}))

if __name__=='__main__': main()
