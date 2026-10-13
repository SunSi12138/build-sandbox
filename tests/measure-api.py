#!/usr/bin/env python3
"""Non-gating observations on an isolated NativeAOT application, not a benchmark guarantee."""
import argparse, hashlib, importlib.util, json, platform, re, shutil, statistics, time
from pathlib import Path
spec=importlib.util.spec_from_file_location('smoke',Path(__file__).with_name('api-smoke.py'))
smoke=importlib.util.module_from_spec(spec);spec.loader.exec_module(smoke)

def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--executable',required=True,type=Path);p.add_argument('--report',required=True,type=Path)
    args=p.parse_args(); app=smoke.ApiSmoke(args.executable.resolve(),None,30)
    report={'executableSha256':hashlib.sha256(args.executable.read_bytes()).hexdigest(),'platform':platform.platform(),'observations':[],
            'sqlExecutionCount':'not-instrumented; no runtime instrumentation added solely for measurement',
            'limitations':'Loopback desktop sample, no real phone timing or production concurrency. No performance thresholds or architectural changes.'}
    try:
        app.start();app.community=app.json('POST','/api/communities',{'name':'性能观察社区','title':'性能观察社区居委会'},expected=201)
        app.point_type=app.bootstrap['pointTypes'][0]
        point=app.create_point('性能观察点位')
        for i in range(1,1001):
            body=app.inspection_body(point['id'],issues=[app.issue_body('性能观察问题 '+str(i),app.today)] if i%3==0 else [])
            app.json('POST','/api/inspections',body,expected=201)
            if i not in (100,1000):continue
            entry={'recordCount':i,'queries':[]}
            for route in ('/api/inspections/counts','/api/inspections?page=1&pageSize=20','/api/dashboard','/api/exports/inspection-ledger?from=1900-01-01&to='+str(app.today)):
                values=[];size=0
                for _ in range(7):
                    began=time.perf_counter();_,data=app.request('GET',route);values.append((time.perf_counter()-began)*1000);size=len(data)
                entry['queries'].append({'route':route,'milliseconds':values,'medianMs':statistics.median(values),'maxMs':max(values),'responseBytes':size})
            status=Path('/proc')/str(app.process.pid)/'status'
            if status.exists():
                match=re.search(r'^VmHWM:\s+(\d+) kB',status.read_text(),re.M)
                if match:entry['processHighWaterRssKiB']=int(match.group(1))
            report['observations'].append(entry)
        report['passed']=True
    finally:
        app.stop();app.log.close();shutil.rmtree(app.temp)
        args.report.parent.mkdir(parents=True,exist_ok=True)
        args.report.write_text(json.dumps(report,ensure_ascii=False,indent=2))
    print(json.dumps(report,ensure_ascii=False))
if __name__=='__main__':main()
