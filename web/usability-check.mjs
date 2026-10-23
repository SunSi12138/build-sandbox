// Run only against an empty scratch runtime on port 5201.
import {chromium} from 'playwright'
import fs from 'node:fs/promises'
const base='http://127.0.0.1:5201',out='../work/usability-checks'
await fs.mkdir(out,{recursive:true})
const browser=await chromium.launch({channel:'msedge',headless:true})
const errors=[]
try{
 const page=await browser.newPage({viewport:{width:390,height:844}})
 page.setDefaultTimeout(10000)
 page.on('pageerror',e=>errors.push(e.message))
 if(!process.argv.includes('--finish-only')){
 await page.goto(base+'/#/')
 await page.getByRole('heading',{name:'先添加一个网格'}).waitFor()
 await page.screenshot({path:out+'/first-use.png',fullPage:true})
 await page.getByRole('button',{name:'添加网格',exact:true}).click()
 await page.getByPlaceholder('例如：第一网格').fill('易用性测试网格')
 await page.getByRole('button',{name:'保存网格',exact:true}).click()
 await page.getByTestId('point-name').waitFor()
 await page.getByTestId('point-name').fill('便民服务楼')
 await page.getByRole('combobox',{name:'场所形态'}).click()
 await page.getByRole('option',{name:'建筑物',exact:true}).click()
 await page.getByRole('textbox',{name:'详细地址'}).fill('幸福路 1 号')
 if(await page.getByRole('textbox',{name:'经纬度'}).isVisible())throw Error('Advanced coordinate input not collapsed')
 await page.getByTestId('point-save').click()
 await page.getByRole('heading',{name:'便民服务楼',exact:true}).waitFor()
 const point=(await(await fetch(base+'/api/points')).json()).items[0]
 await page.goto(base+'/#/inspections/new?pointId='+point.id)
 await page.getByRole('heading',{name:'检查内容',exact:true}).waitFor()
 if(await page.getByRole('textbox',{name:'通知单编号',exact:true}).isVisible())throw Error('Notice number not collapsed')
 if(await page.getByRole('combobox',{name:'检查对象'}).isVisible())throw Error('Empty use selector should be hidden')
 await page.getByRole('button',{name:'存入草稿箱',exact:true}).click()
 await page.getByText('已存入草稿箱',{exact:true}).waitFor()
 await page.screenshot({path:out+'/inspection-mobile.png',fullPage:true})
 if(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth+1))throw Error('Mobile form overflow')
 await page.goto(base+'/#/')
 await page.getByRole('button',{name:'手机连接',exact:true}).click()
 await page.getByRole('button',{name:'开启手机访问',exact:true}).click()
 await page.getByText('已允许局域网访问',{exact:true}).waitFor()
 await page.screenshot({path:out+'/phone-connect.png',fullPage:true})
 }
 await page.goto(base+'/#/settings')
 await page.getByRole('textbox',{name:'单位名称'}).fill('测试社区')
 await page.getByTestId('settings-save').click()
 await page.getByText('已保存',{exact:true}).waitFor()
 const settings=await(await fetch(base+'/api/settings')).json();if(settings.issuingUnit!=='测试社区')throw Error('Issuing unit fallback')
 await page.goto(base+'/#/reports?kind=places')
 await page.getByLabel('选择第 1 条').check()
 await page.getByRole('button',{name:'导出所选 1 条 Excel',exact:true}).waitFor()
 if(await page.getByRole('columnheader',{name:'编号',exact:true}).count())throw Error('Long IDs still lead report')
 await page.screenshot({path:out+'/report-mobile.png',fullPage:true})
 if(errors.length)throw Error(errors.join('\n'))
 console.log(JSON.stringify({scope:process.argv.includes('--finish-only')?'settings-and-export':'full-flow',firstUse:!process.argv.includes('--finish-only'),inlineGrid:true,placeSaved:true,sharedDraft:true,collapsedOptions:true,mobileOverflow:false,lanToggle:true,settingsFallback:true,exportScope:true,pageErrors:errors}))
}finally{await browser.close()}
