import { chromium } from 'playwright'
import fs from 'node:fs/promises'
import path from 'node:path'
const base = 'http://127.0.0.1:5199'
const output = path.resolve('../work/checks')
await fs.mkdir(output,{recursive:true})
async function api(url,body,method='POST'){const r=await fetch(base+url,{method:body===undefined?'GET':method,headers:{'Content-Type':'application/json'},body:body===undefined?undefined:JSON.stringify(body)});if(!r.ok)throw Error(`${url} ${r.status} ${await r.text()}`);return r.json()}
const boot=await api('/api/bootstrap')
const grid=await api('/api/communities',{name:'功能检查网格',title:'功能检查网格'})
const place=await api('/api/points',{name:'幸福路18号',communityId:grid.id,typeId:boot.pointTypes[0].id,address:'幸福路18号',longitude:88.893426,latitude:29.274331,frequency:'monthly',disabled:false})
await api(`/api/map/shapes/point/${place.id}`,{shape:{type:'Polygon',coordinates:[[88.892,29.274],[88.894,29.274],[88.894,29.276],[88.892,29.276],[88.892,29.274]]},revision:0},'PUT')
const use=await api('/api/records/use',{name:'便民商店',parentId:place.id,data:{unitId:'',purposes:['商店','仓库'],contact:'张某',phone:'',startDate:'2026-01-01',endDate:'',state:'使用中',notes:'',photoIds:[]}})
const types=await api('/api/records/inspection-type')
const inspection=await api('/api/inspections',{pointId:place.id,date:boot.today,inspector:'王某',result:'issues',inspectionTypeId:types[0].id,useId:use.id,submissionKey:'smoke-inspection',issues:[{description:'通道堆物',requirement:'清理通道',deadline:boot.today,responsibleUnit:'店主'}]})
await api(`/api/issues/${inspection.issues[0].id}/feedback`,{date:boot.today,notes:'已清理'})
await api(`/api/issues/${inspection.issues[0].id}/review`,{date:boot.today,conclusion:'现场确认已清理'})
const incomplete=await api('/api/inspections',{pointId:place.id,date:boot.today,inspector:'王某',result:'incomplete',inspectionTypeId:types[0].id,incompleteReason:'二楼关门'})
if(incomplete.inspection.rectifyStatus!=='incomplete')throw Error('Incomplete counted incorrectly')
await api(`/api/records/use/${use.id}`,{name:'便民商店',parentId:place.id,data:{...use.data,state:'已结束',endDate:boot.today},revision:use.revision},'PUT')
const saved=await api(`/api/inspections/${inspection.inspection.id}`)
if(saved.inspection.useName!=='便民商店'||saved.issues[0].status!=='closed')throw Error('History or review failed')
for(const format of ['xlsx','docx']){const r=await fetch(`${base}/api/reports/inspections/${format}`);if(!r.ok)throw Error(await r.text());await fs.writeFile(path.join(output,`检查台账.${format}`),Buffer.from(await r.arrayBuffer()))}
const browser=await chromium.launch({channel:'msedge',headless:true})
const context=await browser.newContext({viewport:{width:1440,height:1000}})
const external=[],errors=[]
await context.route('**/*',route=>{const url=new URL(route.request().url());if(!['127.0.0.1','localhost'].includes(url.hostname)&&['http:','https:'].includes(url.protocol)){external.push(url.href);return route.abort()}return route.continue()})
const page=await context.newPage();page.on('pageerror',e=>errors.push(e.message))
await page.goto(base+'/#/map');await page.getByRole('heading',{name:'社区地图',exact:true}).waitFor();await page.waitForTimeout(1500)
await page.screenshot({path:path.join(output,'map-desktop.png'),fullPage:true})
await page.goto(base+`/#/points/${place.id}`);await page.getByText('经营和使用情况',{exact:true}).waitFor();await page.screenshot({path:path.join(output,'place-desktop.png'),fullPage:true})
await page.setViewportSize({width:390,height:844});await page.goto(base+'/#/inspections/new?pointId='+place.id);await page.getByRole('heading',{name:'检查内容',exact:true}).waitFor();await page.screenshot({path:path.join(output,'inspection-mobile.png'),fullPage:true})
await browser.close()
const result={api:'passed',word:true,excel:true,externalRequests:external,pageErrors:errors,placeId:place.id,inspectionId:inspection.inspection.id}
await fs.writeFile(path.join(output,'result.json'),JSON.stringify(result,null,2));console.log(JSON.stringify(result));if(errors.length||external.length)process.exitCode=1
