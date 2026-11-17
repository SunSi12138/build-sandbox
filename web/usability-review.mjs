// UI-only checks against the generated site and in-memory fixtures; never touches user data.
import { chromium, expect } from 'playwright/test'
import { createServer } from 'node:http'
import fs from 'node:fs/promises'
import path from 'node:path'
const root=path.resolve('.output/public'),out=path.resolve('../work/usability-review')
await fs.mkdir(out,{recursive:true})
const mime={'.html':'text/html','.js':'text/javascript','.css':'text/css','.svg':'image/svg+xml','.png':'image/png','.wasm':'application/wasm'}
const server=createServer(async(req,res)=>{try{const file=path.resolve(root,'.'+decodeURIComponent(new URL(req.url,'http://local').pathname));if(!file.startsWith(root+path.sep)&&file!==root)throw Error();const target=file===root?path.join(root,'index.html'):file;res.setHeader('Content-Type',mime[path.extname(target)]??'application/octet-stream');res.end(await fs.readFile(target))}catch{res.writeHead(404);res.end()}})
await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve))
const base=`http://127.0.0.1:${server.address().port}`
const polygon={type:'Polygon',coordinates:[[88.892,29.274],[88.894,29.274],[88.894,29.276],[88.892,29.276],[88.892,29.274]]}
const point={id:'place1',name:'幸福路18号',communityId:'grid1',communityName:'第一网格',typeId:'building',typeName:'永久建筑',address:'幸福路18号',contact:'',phone:'',frequency:'irregular',notes:'',disabled:false,temporary:false,longitude:88.893,latitude:29.275,color:'#087f70',revision:1}
const use={id:'use1',kind:'use',parentId:'place1',name:'便民商店',revision:1,archived:false,updatedAt:'2026-10-28',data:{position:'一楼东侧',purposeId:'purpose-shop',state:'使用中',contact:'',phone:'',startDate:'',endDate:'',notes:'',photoIds:[],values:{business:'日用百货'},fieldSnapshot:[{id:'business',label:'经营内容',type:'text',required:false}]}}
const purpose={id:'purpose-shop',kind:'purpose-type',name:'商店',revision:1,archived:false,data:{icon:'🏪',fields:use.data.fieldSnapshot}}
const layer={id:'jlyh',name:'吉林一号',folder:'jlyh',scheme:'tms',enabled:true,isDefault:true,opacity:1,minZoom:12,maxZoom:18,sortOrder:0,urlTemplate:'',apiKey:'',subdomains:'',updateCache:false,annotation:false,mapKey:'',cacheVersion:0}
const state={layers:[layer],features:[{id:point.id,kind:'point',name:point.name,communityId:point.communityId,shape:polygon,revision:1,openIssues:0,disabled:false,typeId:'building',color:point.color,purposeIds:['purpose-shop'],uses:[{id:use.id,name:use.name,purposeName:'商店',icon:'🏪'}]}]}
const boot={today:'2026-10-28',settings:{streetName:'社区管理',issuingUnit:'社区管理',defaultInspector:'',noticeTemplate:'',jpegQuality:75},communities:[{id:'grid1',name:'第一网格',title:'第一网格',parentId:'',color:'#64748b'}],pointTypes:[{id:'building',name:'永久建筑'},{id:'temporary',name:'临时建筑'}],campaignTypes:[]}
const browser=await chromium.launch({channel:'msedge',headless:true})
const page=await browser.newPage({viewport:{width:1440,height:960}}),errors=[]
const checks=[{id:'safety',name:'安全检查',archived:false,data:{items:['消防通道','用电安全']}},{id:'hygiene',name:'环境卫生',archived:false,data:{items:['卫生情况']}}]
let releaseSlow,notifySlow
const slowStarted=new Promise(resolve=>{notifySlow=resolve})
const reportQueries=[]
const issue={id:'issue1',inspectionId:'check1',inspectionDate:'2026-10-01',description:'通道堆物',requirement:'清理通道',deadline:'2026-10-31',responsibleUnit:'店主',follower:'',status:'pending',revision:1,voided:false}
const inspection={id:'check1',pointId:point.id,pointName:point.name,date:'2026-10-01',inspector:'王某',inspectionTypeName:'安全检查',useName:use.name,result:'issues',notes:'',incompleteReason:''}
page.on('pageerror',e=>errors.push(e.message))
page.setDefaultTimeout(10000)
await page.route('**/api/**',async route=>{
 const url=new URL(route.request().url()),p=url.pathname
 if(p.startsWith('/api/reports/')){
   reportQueries.push({path:p,query:Object.fromEntries(url.searchParams)})
   if(url.searchParams.get('q')==='慢')await new Promise(resolve=>{releaseSlow=resolve;notifySlow()})
   return route.fulfill({json:{title:p.endsWith('/uses')?'使用情况台账':'场所台账',scope:`搜索：${url.searchParams.get('q')||'全部'}`,headers:['编号','名称','状态'],rows:[['place1','幸福路18号','使用中'],['place2','第二场所','使用中']]}})
 }
 if(p.startsWith('/api/map/tiles/')){const [,z,x,y]=p.match(/jlyh\/(\d+)\/(\d+)\/(\d+)/)??[];if(z){const dir=path.resolve(`../tiles/jlyh/${z}/${x}`),row=2**Number(z)-1-Number(y);for(const ext of ['jpg','png','jpeg']){try{return await route.fulfill({body:await fs.readFile(path.join(dir,`${row}.${ext}`)),contentType:ext==='png'?'image/png':'image/jpeg'})}catch{}}}return route.fulfill({status:404,body:''})}
 const round2=process.argv.includes('--round2')
 const fixtures={'/api/bootstrap':boot,'/api/map':state,'/api/map/layers':[layer],'/api/points/place1':{point,photos:[],inspections:round2?[inspection]:[],openIssues:round2?[issue]:[],campaigns:[]},'/api/inspections/check1':{inspection,issues:[issue],photos:[]},'/api/records/use':[use],'/api/records/purpose-type':[purpose],'/api/records/inspection-type':checks,'/api/records/staff':[],'/api/records/plan':[],'/api/inspections':{items:[],total:0,page:1,pageSize:100},'/api/points':{items:[point],total:1,page:1,pageSize:30}}
 if(route.request().method()!=='GET')throw Error('Unexpected mutation '+p)
 return route.fulfill({json:fixtures[p]??[]})
})
try{
 if(process.argv.includes('--round2')||process.argv.includes('--plans-only')){
 if(!process.argv.includes('--plans-only')){
 await page.goto(base+'/#/reports')
 await expect(page.getByRole('button',{name:'导出全部 Excel',exact:true})).toBeEnabled()
 await page.getByRole('textbox',{name:'搜索',exact:true}).fill('慢')
 await page.getByRole('button',{name:'查询',exact:true}).click();await slowStarted
 await page.getByRole('textbox',{name:'搜索',exact:true}).fill('新')
 const response=page.waitForResponse(r=>r.url().includes('q=%E6%85%A2'))
 releaseSlow();await response
 await expect(page.getByText('条件已改变，请查询',{exact:false})).toBeVisible()
 await expect(page.getByRole('button',{name:'导出全部 Excel',exact:true})).toBeDisabled()
 await page.getByRole('button',{name:'查询',exact:true}).click()
 await expect(page.getByRole('button',{name:'导出全部 Excel',exact:true})).toBeEnabled()
 await page.getByLabel('选择第 1 条').check()
 if(!await page.getByLabel('全选查询结果').evaluate(el=>el.indeterminate))throw Error('Partial selection not indicated')
 await page.getByRole('button',{name:'取消勾选',exact:true}).click()
 await expect(page.getByLabel('选择第 1 条')).not.toBeChecked()
 await page.getByRole('button',{name:'使用情况',exact:true}).click()
 await expect(page.locator('.panel-head strong')).toHaveText('使用情况台账')
 await page.screenshot({path:path.join(out,'round2-reports.png'),fullPage:true})
 await page.goto(base+'/#/points')
 await page.getByRole('link',{name:'地图查看',exact:true}).click()
 await expect(page).toHaveURL(/place=place1/)
 await expect(page.getByRole('heading',{name:'幸福路18号',exact:true})).toBeVisible()
 await page.locator('.pane-record').filter({hasText:'安全检查'}).click()
 await page.getByRole('button',{name:'办理进度',exact:true}).click()
 await page.getByRole('textbox',{name:'办理说明'}).fill('取消后不能残留')
 page.once('dialog',d=>d.accept())
 await page.getByRole('dialog').getByRole('button',{name:'取消',exact:true}).click()
 await expect(page.getByRole('dialog')).toHaveCount(0)
 await page.getByRole('button',{name:'办理进度',exact:true}).click()
 await expect(page.getByRole('textbox',{name:'办理说明'})).toHaveValue('')
 await page.getByRole('dialog').getByRole('button',{name:'取消',exact:true}).click()
 await page.getByRole('button',{name:'整改反馈',exact:true}).click()
 await expect(page.getByLabel('反馈日期')).toHaveValue(new Date().toLocaleDateString('sv-SE'))
 await page.getByRole('dialog').getByRole('button',{name:'取消',exact:true}).click()
 await page.goto(base+'/#/inspections/new?pointId=place1&typeId=safety')
 await expect(page.getByText('消防通道',{exact:true})).toBeVisible()
 await page.getByRole('button',{name:'未填项设为正常',exact:true}).click()
 await expect(page.getByText('已填 2 / 2 项',{exact:true})).toBeVisible()
 await page.getByRole('combobox',{name:'检查类型'}).click()
 page.once('dialog',d=>d.dismiss())
 await page.getByRole('option',{name:'环境卫生',exact:true}).click()
 await expect(page.getByRole('combobox',{name:'检查类型'})).toContainText('安全检查')
 await expect(page.getByText('已填 2 / 2 项',{exact:true})).toBeVisible()
 }
 await page.goto(base+'/#/plans')
 await page.getByRole('button',{name:'新增安排',exact:true}).click()
 await page.getByRole('button',{name:'勾选当前结果（1）',exact:true}).click()
 await page.getByRole('checkbox',{name:'只看已选',exact:true}).check()
 await expect(page.getByRole('checkbox',{name:'幸福路18号 · 第一网格',exact:true})).toBeChecked()
 await page.getByRole('button',{name:'取消全部勾选',exact:true}).click()
 await expect(page.getByRole('checkbox',{name:'幸福路18号 · 第一网格',exact:true})).not.toBeChecked()
 await page.setViewportSize({width:390,height:844})
 await page.screenshot({path:path.join(out,'round2-plan-mobile.png')})
 if(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth+1))throw Error('Mobile horizontal overflow')
 if(errors.length)throw Error(errors.join('\n'))
 console.log(process.argv.includes('--plans-only')?'PASS: plan batch selection, selected-only filter, clear and mobile layout.':'PASS round 2: stale report export blocked, partial selection, map link, operation reset/date, checklist switch protection, plan selection.')
 }else if(process.argv.includes('--drawing-only')){
 await page.goto(base+'/#/map')
 await page.getByRole('button',{name:'画区域',exact:true}).click()
 const finish=page.getByRole('button',{name:'结束当前区域',exact:true})
 await expect(finish).toBeDisabled()
 for(const [x,y] of [[650,240],[900,240],[800,430]])await page.mouse.click(x,y)
 await page.mouse.move(750,360)
 await expect(finish).toBeEnabled()
 await finish.click()
 await expect(page.getByRole('button',{name:'新建场所',exact:true})).toBeEnabled()
 await page.mouse.click(1030,240)
 await expect(page.getByRole('button',{name:'新建场所',exact:true})).toBeDisabled()
 await page.mouse.click(1200,240);await page.mouse.click(1150,430);await page.mouse.move(1100,360)
 await finish.click()
 await expect(page.getByText('已画 2 个区域',{exact:true})).toBeVisible()
 console.log('PASS: continuous multi-area drawing; incomplete sketch cannot be saved.')
 }else if(process.argv.includes('--mobile-only')){
 await page.setViewportSize({width:390,height:844})
 await page.goto(base+'/#/map?place=place1')
 await expect(page.getByRole('heading',{name:'幸福路18号',exact:true})).toBeVisible()
 await page.waitForTimeout(400);await page.screenshot({path:path.join(out,'map-mobile.png')})
 }else{
 await page.goto(base+'/#/map?place=place1')
 await expect(page.getByRole('heading',{name:'幸福路18号',exact:true})).toBeVisible()
 await expect(page.getByText('本地地图',{exact:true})).toHaveCount(0)
 await page.getByPlaceholder('搜索场所、店名或用途').fill('便民商店')
 await expect(page.locator('.map-search-results button')).toHaveText('幸福路18号')
 await page.getByPlaceholder('搜索场所、店名或用途').fill('')
 await page.getByRole('button',{name:'修改使用情况',exact:true}).click()
 const useName=page.getByRole('textbox',{name:'店名 / 住户 / 使用名称'})
 await useName.fill('不应保留的修改')
 await page.getByRole('button',{name:'取消',exact:true}).click()
 await page.getByRole('button',{name:'修改使用情况',exact:true}).click()
 await expect(useName).toHaveValue('便民商店')
 await page.getByRole('button',{name:'取消',exact:true}).click()
 await page.screenshot({path:path.join(out,'map-desktop.png')})
 await page.getByRole('button',{name:'修改位置',exact:true}).click()
 await expect(page.getByRole('button',{name:'保存场所位置',exact:true})).toBeVisible()
 await expect(page.getByRole('button',{name:'新建场所',exact:true})).toHaveCount(0)
 await page.locator('.map-save').getByRole('button',{name:'取消',exact:true}).click()
 await page.getByRole('button',{name:'修改场所',exact:true}).click()
 await page.getByRole('button',{name:'修改位置',exact:true}).last().click()
 await expect(page.getByRole('button',{name:'使用这个位置',exact:true})).toBeEnabled()
 await expect(page.getByText('拖动节点调整',{exact:true})).toBeVisible()
 await page.getByRole('dialog').last().getByRole('button',{name:'取消',exact:true}).click()
 await page.getByRole('dialog').getByRole('button',{name:'取消',exact:true}).click()
 await expect(page.getByRole('dialog')).toHaveCount(0)
 await page.setViewportSize({width:390,height:844})
 await page.waitForTimeout(400);await page.screenshot({path:path.join(out,'map-mobile.png')})
 if(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth+1))throw Error('Mobile horizontal overflow')
 await page.goto(base+'/#/settings?tab=places')
 await expect(page.getByRole('heading',{name:'用途分类与字段'})).toBeVisible()
 await expect(page.getByText('导入地图缓存',{exact:true})).toHaveCount(0)
 await page.screenshot({path:path.join(out,'settings-mobile.png'),fullPage:true})
 if(errors.length)throw Error(errors.join('\n'))
 console.log('PASS: map search, edit/cancel, geometry retention, contextual save, settings and mobile layout; UI fixtures only.')
 }
}catch(error){console.error('Checkboxes:',await page.locator('[role=checkbox]').evaluateAll(elements=>elements.map(e=>({html:e.outerHTML,labels:e.labels?[...e.labels].map(x=>x.textContent):null,hidden:e.closest('[aria-hidden=true]')?.tagName}))));console.error('Page errors:',errors);console.error((await page.locator('body').innerText()).slice(-2200));await page.screenshot({path:path.join(out,'failure.png')});throw error}finally{await browser.close();await new Promise(resolve=>server.close(resolve))}
