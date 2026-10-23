import {chromium} from 'playwright'
import fs from 'node:fs/promises'
const base='http://127.0.0.1:5199'
const result=JSON.parse(await fs.readFile('../work/checks/result.json','utf8'))
const browser=await chromium.launch({channel:'msedge',headless:true})
try{
 const page=await browser.newPage({viewport:{width:1440,height:1000}})
 await page.goto(base+'/#/map')
 await page.locator('.map-place-row').filter({hasText:'幸福路18号'}).click()
 await page.getByRole('button',{name:'画区域',exact:true}).click()
 const map=page.locator('.offline-map')
 for(const position of [{x:250,y:240},{x:400,y:240},{x:400,y:390}])await map.click({position})
 await page.getByRole('button',{name:'画好了',exact:true}).click()
 await page.getByText('拖动节点调整',{exact:true}).waitFor()
 if(!await page.getByRole('checkbox',{name:'吸附边界'}).isChecked())throw Error('Snap unavailable')
 const saved=page.waitForResponse(r=>r.url().includes('/api/map/shapes/point/')&&r.request().method()==='PUT')
 await page.getByRole('button',{name:'保存到幸福路18号',exact:true}).click()
 if(!(await saved).ok())throw Error('Draw save failed')
 await page.getByRole('button',{name:'修改位置',exact:true}).waitFor()
 await page.waitForTimeout(400)
 let state=await(await fetch(base+'/api/map')).json();const first=state.features.find(x=>x.id===result.placeId)
 if(first.shape.coordinates.length!==4)throw Error('Polygon drawing incorrect')
 await page.getByRole('button',{name:'修改位置',exact:true}).click()
 await page.getByText('拖动节点调整',{exact:true}).waitFor()
 const edited=page.waitForResponse(r=>r.url().includes('/api/map/shapes/point/')&&r.request().method()==='PUT')
 await page.getByRole('button',{name:'保存到幸福路18号',exact:true}).click()
 const response=await edited;if(!response.ok())throw Error('Edit save failed: '+await response.text())
 state=await(await fetch(base+'/api/map')).json();const second=state.features.find(x=>x.id===result.placeId)
 if(second.revision<=first.revision)throw Error('Edit save not persisted')
 const info=await(await fetch(base+'/api/system')).json();const lan=info.addresses.find(x=>x.includes('192.168.'))||info.addresses.find(x=>x.includes('10.'))
 if(lan){const health=await fetch(lan+'/api/health');if(!health.ok)throw Error('Local network address failed')}
 const child=await import('node:child_process');const duplicate=child.spawnSync('../work/final-runtime/CommunityGIS.exe',['--no-browser'],{encoding:'buffer'});if(duplicate.status!==0)throw Error('Second instance did not exit gracefully')
 result.mapDraw=true;result.mapEditSave=true;result.snapControl=true;result.secondInstance=true;result.lanAddress=lan||result.lanAddress
 await fs.writeFile('../work/checks/result.json',JSON.stringify(result,null,2));console.log(JSON.stringify(result))
}finally{await browser.close()}
