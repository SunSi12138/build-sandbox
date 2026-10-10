import { createServer } from 'node:http'
import { readFile, writeFile, mkdir } from 'node:fs/promises'
import { resolve, extname, sep } from 'node:path'
import assert from 'node:assert/strict'
import { chromium } from 'playwright'

const directory = resolve('.output/public')
const output = resolve('evidence')
await mkdir(output, { recursive: true })
const contentTypes = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript', '.json': 'application/json', '.css': 'text/css', '.wasm': 'application/wasm' }
const server = createServer(async (req, res) => {
  const pathname = new URL(req.url, 'http://127.0.0.1').pathname
  const path = resolve(directory, '.' + (pathname === '/' ? '/index.html' : pathname))
  if (!path.startsWith(directory + sep)) { res.writeHead(403); res.end(); return }
  try {
    const data = await readFile(path)
    res.writeHead(200, { 'Content-Type': contentTypes[extname(path)] || 'application/octet-stream' })
    res.end(data)
  } catch { res.writeHead(404); res.end() }
})
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve))
const origin = 'http://127.0.0.1:' + server.address().port
let browser
const report = { browser: null, origin, externalRequests: [], pageErrors: [], consoleWarnings: [], assertions: [], localRequests: [] }

try {
  browser = await chromium.launch({ headless: true })
  report.browser = await browser.version()
  const context = await browser.newContext({ viewport: { width: 390, height: 844 }, locale: 'zh-CN' })
  await context.route('**/*', route => {
    const url = route.request().url()
    if (url.startsWith(origin + '/')) return route.continue()
    report.externalRequests.push(url)
    return route.abort('internetdisconnected')
  })
  const page = await context.newPage()
  page.on('pageerror', error => report.pageErrors.push(error.message))
  page.on('console', message => { if (['error', 'warning'].includes(message.type())) report.consoleWarnings.push(message.text()) })
  page.on('request', request => { if (request.url().startsWith(origin + '/')) report.localRequests.push(request.url().slice(origin.length)) })
  await page.goto(origin, { waitUntil: 'networkidle' })
  await page.getByRole('heading', { name: '排查登记验证' }).waitFor()
  assert.equal(await page.locator('#unsupported-browser').count(), 0)
  assert.equal(await page.getByRole('checkbox', { name: '整改后进行复查' }).isChecked(), true)
  report.assertions.push('Fresh page renders through plain HTTP with every external origin blocked')

  report.initialIcons = await page.locator('.iconify').evaluateAll(icons => icons.map(icon => ({ class: icon.className, mask: getComputedStyle(icon).maskImage, width: icon.getBoundingClientRect().width })))
  assert.ok(report.initialIcons.length >= 8, 'Expected bundled icons in the page')
  assert.ok(report.initialIcons.every(icon => icon.mask.startsWith('url(') && icon.width > 0), 'All icons must have a resolved SVG mask')
  report.assertions.push('All initial icons, including checkbox and select defaults, resolve from embedded SVG data')

  await page.getByRole('combobox').click()
  await page.getByRole('option', { name: '滨河社区' }).click()
  await page.getByRole('button', { name: '删除验证' }).click()
  await page.getByRole('heading', { name: '删除确认' }).waitFor()
  await page.getByRole('dialog').getByRole('button', { name: '关闭', exact: true }).click()
  await page.getByRole('button', { name: '保存验证' }).click()
  await page.getByText('离线组件正常', { exact: true }).waitFor()
  report.assertions.push('Nuxt UI select, modal, Chinese close label, checkbox and toast work with external traffic blocked')
  await page.screenshot({ path: resolve(output, 'mobile.png'), fullPage: true })

  await page.getByRole('link', { name: '设置', exact: true }).click()
  // Hash navigation completes asynchronously after Vue Router loads the page
  // chunk. A successful click does not mean that navigation has settled yet.
  await page.waitForURL(url => url.hash === '#/settings')
  await page.getByRole('heading', { name: '设置页面' }).waitFor()
  assert.equal(new URL(page.url()).hash, '#/settings')
  await page.reload({ waitUntil: 'networkidle' })
  await page.getByRole('heading', { name: '设置页面' }).waitFor()
  report.assertions.push('Hash route /#/settings survives a full reload without server fallback')
  await page.getByRole('link', { name: '返回首页' }).click()
  await page.getByRole('heading', { name: '排查登记验证' }).waitFor()
  await page.setViewportSize({ width: 1366, height: 900 })
  await page.screenshot({ path: resolve(output, 'desktop.png'), fullPage: true })

  await context.setOffline(true)
  await page.getByRole('button', { name: '保存验证' }).click()
  await page.getByText('离线组件正常', { exact: true }).first().waitFor()
  report.offlineAppearance = await page.evaluate(() => ({ font: getComputedStyle(document.body).fontFamily, background: getComputedStyle(document.body).backgroundColor, iconMasks: [...document.querySelectorAll('.iconify')].map(icon => getComputedStyle(icon).maskImage) }))
  assert.ok(report.offlineAppearance.font.includes('Microsoft YaHei'))
  assert.ok(report.offlineAppearance.iconMasks.every(mask => mask.startsWith('url(')))
  report.assertions.push('After fully disconnecting the loaded browser page, UI interactions, CSS, system-font stack and icon masks still work')
  await context.close()

  const legacy = await browser.newContext()
  await legacy.addInitScript(() => Object.defineProperty(window.CSS, 'registerProperty', { value: undefined }))
  const legacyPage = await legacy.newPage()
  await legacyPage.goto(origin, { waitUntil: 'networkidle' })
  await legacyPage.locator('#unsupported-browser').waitFor({ state: 'visible' })
  assert.ok((await legacyPage.locator('#unsupported-browser').textContent()).includes('请升级浏览器后使用'))
  report.assertions.push('ES5 compatibility guard displays Chinese upgrade guidance when a required feature is missing')
  await legacy.close()

  assert.deepEqual(report.externalRequests, [])
  assert.deepEqual(report.pageErrors, [])
  assert.deepEqual(report.consoleWarnings, [])
  report.pass = true
} catch (error) {
  report.pass = false
  report.failure = error.message
  throw error
} finally {
  await browser?.close()
  await new Promise(resolve => server.close(resolve))
  await writeFile(resolve(output, 'offline-report.json'), JSON.stringify(report, null, 2))
  console.log(JSON.stringify({ ...report, initialIcons: report.initialIcons?.length, offlineAppearance: report.offlineAppearance ? { font: report.offlineAppearance.font, background: report.offlineAppearance.background, icons: report.offlineAppearance.iconMasks.length } : null }, null, 2))
}
