import assert from 'node:assert/strict'
import { createHash } from 'node:crypto'
import { spawn } from 'node:child_process'
import { createServer } from 'node:net'
import { once } from 'node:events'
import { chmod, cp, mkdir, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises'
import { createWriteStream } from 'node:fs'
import { tmpdir } from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { chromium } from 'playwright'
import { verifyWordDownloads } from './word-browser-test.mjs'
import { verifyOptimization } from './optimization-browser-test.mjs'
import { verifyRound3 } from './round3-browser-test.mjs'
import { verifyUsability } from './ux-browser-test.mjs'
import { verifyFreshArchitecture } from './fresh-architecture-browser-test.mjs'
import { verifyConsistency } from './consistency-browser-test.mjs'
import { verifyMapManagement } from './map-management-browser-test.mjs'

const directory = path.dirname(fileURLToPath(import.meta.url))
const executableArgument = process.argv.indexOf('--executable')
assert(executableArgument >= 0 && process.argv[executableArgument + 1], '--executable is required')
const sourceExecutable = path.resolve(process.argv[executableArgument + 1])
const evidence = path.join(directory, 'test-results')
const screens = path.join(evidence, 'screens')
await mkdir(screens, { recursive: true })
await mkdir(evidence, { recursive: true })
const temporary = await mkdtemp(path.join(tmpdir(), 'hbtool-browser-'))
const installation = path.join(temporary, 'application')
await mkdir(installation)
const executable = path.join(installation, path.basename(sourceExecutable))
await cp(sourceExecutable, executable)
if (process.platform !== 'win32') await chmod(executable, 0o755)
await cp(process.env.HBTOOL_WEB_ROOT || path.join(path.dirname(sourceExecutable), 'wwwroot'), path.join(installation, 'wwwroot'), { recursive: true })

async function availablePort() {
  const server = createServer()
  server.listen(0, '127.0.0.1')
  await once(server, 'listening')
  const port = server.address().port
  await new Promise((resolve, reject) => server.close((error) => error ? reject(error) : resolve()))
  return port
}
const port = await availablePort()
const origin = `http://hbtool.test:${port}`
const directOrigin = `http://127.0.0.1:${port}`
const log = createWriteStream(path.join(evidence, 'browser-server.log'))
const application = spawn(executable, ['--port', String(port), '--no-browser', '--seed-demo'], { cwd: temporary, stdio: ['ignore', 'pipe', 'pipe'] })
application.stdout.pipe(log, { end: false })
application.stderr.pipe(log, { end: false })
const applicationExit = new Promise((resolve) => application.once('exit', (code, signal) => resolve({ code, signal })))
const report = { passed: false, cases: [], externalRequests: [], pageErrors: [], consoleErrors: [], resources: [], browser: '', secureContext: null }
let browser
let page
let expectedWasmFailure = false
let activeCase = ''

async function api(route, options = {}) {
  const response = await fetch(directOrigin + '/api' + route, {
    ...options,
    headers: options.body ? { 'content-type': 'application/json', ...options.headers } : options.headers,
    body: options.body && typeof options.body !== 'string' ? JSON.stringify(options.body) : options.body
  })
  if (!response.ok) throw new Error(`API ${route}: ${response.status} ${await response.text()}`)
  return response.status === 204 ? null : response.json()
}

async function test(name, action) {
  const started = Date.now()
  activeCase = name
  console.log('RUN ' + name)
  try {
    await action()
    report.cases.push({ name, passed: true, durationMs: Date.now() - started })
    console.log('PASS ' + name)
  } catch (error) {
    report.cases.push({ name, passed: false, error: String(error), durationMs: Date.now() - started })
    throw error
  }
}

async function waitForHealthy() {
  for (let attempt = 0; attempt < 100; attempt++) {
    if (application.exitCode !== null) throw new Error('Packaged application exited before readiness')
    try { if ((await fetch(directOrigin + '/api/health')).ok) return } catch {}
    await new Promise((resolve) => setTimeout(resolve, 100))
  }
  throw new Error('Packaged application did not start')
}

async function waitForText(text) {
  await page.waitForFunction((expected) => document.querySelector('main')?.textContent?.includes(expected), text)
}

async function screenshot(name) {
  await page.screenshot({ path: path.join(evidence, name + '.png'), fullPage: true, animations: 'disabled' })
}

async function waitForDialogsClosed() {
  // Role locators exclude aria-hidden content before its exit animation finishes.
  // Inspect the rendered layers as well, including dialogs retained for photo drafts.
  await page.waitForFunction(() => [...document.querySelectorAll('[role="dialog"], [data-slot="overlay"]')].every((element) => {
    const style = getComputedStyle(element)
    return style.display === 'none' || style.visibility === 'hidden' || element.getClientRects().length === 0
  }), null, { timeout: 10000 })
  // Allow the close/after-leave focus restoration to complete without changing focus in the test.
  await page.evaluate(() => new Promise((resolve) => requestAnimationFrame(() => requestAnimationFrame(resolve))))
}

async function assertDialogInViewport(dialog, name) {
  await dialog.waitFor()
  const bounds = await dialog.boundingBox(), viewport = page.viewportSize()
  assert(bounds && bounds.width > 0 && bounds.height > 0 && bounds.x >= -1 && bounds.y >= -1 && bounds.x + bounds.width <= viewport.width + 1 && bounds.y + bounds.height <= viewport.height + 1,
    `Dialog is outside the viewport: ${JSON.stringify({ name, bounds, viewport })}`)
  report.dialogLayoutChecks ??= []
  report.dialogLayoutChecks.push({ name, bounds, viewport })
}

async function assertMobileActionLayout(toastTitle) {
  const toastSelector = '[data-swipe-direction][data-state="open"]'
  await page.locator(toastSelector).filter({ hasText: toastTitle }).first().waitFor({ state: 'visible' })
  // Wait only for toast roots' entrance/stack transitions, never their dismissal timer or progress bar.
  await page.waitForFunction((selector) => [...document.querySelectorAll(selector)].every((element) =>
    element.getAnimations().every((animation) => !animation.pending && animation.playState !== 'running')
  ), toastSelector, { timeout: 5000 })
  const layout = await page.evaluate((selector) => {
    function visibleBox(element) {
      if (!element) return null
      const style = getComputedStyle(element)
      const box = element.getBoundingClientRect()
      if (style.display === 'none' || style.visibility === 'hidden' || Number(style.opacity) === 0 ||
        box.width <= 0 || box.height <= 0 || box.bottom <= 0 || box.top >= innerHeight || box.right <= 0 || box.left >= innerWidth) return null
      return { left: box.left, top: box.top, right: box.right, bottom: box.bottom }
    }
    return {
      navigation: visibleBox(document.querySelector('.mobile-tabs')),
      skipLink: visibleBox(document.querySelector('.skip-link')),
      skipLinkFocused: Boolean(document.activeElement?.closest('.skip-link')),
      toasts: [...document.querySelectorAll(selector)].map((element) => ({ text: element.textContent, box: visibleBox(element) })).filter((toast) => toast.box)
    }
  }, toastSelector)
  assert(layout.navigation, 'Mobile navigation is not visible after ' + toastTitle)
  assert(!layout.skipLinkFocused && !layout.skipLink, 'Pointer action unexpectedly exposed or focused the skip link after ' + toastTitle)
  assert(layout.toasts.some((toast) => toast.text.includes(toastTitle)), 'Expected success toast is no longer visible: ' + toastTitle)
  for (const toast of layout.toasts) {
    const intersects = toast.box.left < layout.navigation.right && toast.box.right > layout.navigation.left &&
      toast.box.top < layout.navigation.bottom && toast.box.bottom > layout.navigation.top
    assert(!intersects, `Success toast obscures mobile navigation: ${JSON.stringify({ toast, navigation: layout.navigation })}`)
  }
  report.mobileLayoutChecks ??= []
  report.mobileLayoutChecks.push({ action: toastTitle, ...layout })
}

async function noHorizontalOverflow() {
  const dimensions = await page.evaluate(() => ({ width: innerWidth, scrollWidth: document.documentElement.scrollWidth }))
  assert(dimensions.scrollWidth <= dimensions.width + 2, `Horizontal overflow: ${JSON.stringify(dimensions)}`)
}

async function iconCount() {
  return page.evaluate(() => [...document.querySelectorAll('[class*="iconify"]')].filter((element) => {
    const style = getComputedStyle(element)
    const mask = style.maskImage || style.webkitMaskImage
    return mask && mask !== 'none' && element.getBoundingClientRect().width > 0
  }).length)
}

async function selectOption(control, label) {
  await control.click()
  await page.getByRole('option', { name: label, exact: true }).click()
}
async function uploadIn(control, name, buffer) {
  const response = page.waitForResponse(response => new URL(response.url()).pathname === '/api/photos' && response.request().method() === 'PUT' && response.status() === 201)
  await control.setInputFiles({ name, mimeType: 'image/jpeg', buffer })
  const uploadedResponse = await response
  const photo = await uploadedResponse.json()
  await page.locator(`[data-testid="photo-uploaded"][data-photo-id="${photo.id}"]`).waitFor()
  const thumbnail = await fetch(directOrigin + `/photos/${photo.id}?size=thumb`)
  assert.equal(thumbnail.status, 200)
  const thumbnailBytes = (await thumbnail.arrayBuffer()).byteLength
  assert(thumbnailBytes <= 200 * 1024, 'Frontend thumbnail should be smaller than 200 KB')
  const thumbSize = await page.evaluate(async id => { const blob = await (await fetch(`/photos/${id}?size=thumb`)).blob(); const bitmap = await createImageBitmap(blob); const size = [bitmap.width, bitmap.height]; bitmap.close(); return size }, photo.id)
  assert(Math.max(...thumbSize) <= 360, 'Frontend thumbnail exceeds its 360 px limit')
  report.thumbnails ??= []
  report.thumbnails.push({ id: photo.id, bytes: thumbnailBytes, width: thumbSize[0], height: thumbSize[1] })
  return { photo, response: uploadedResponse }
}
async function waitForVisualPage(name, title) {
  await page.getByRole('heading', { name: title, exact: true }).waitFor()
  // A hash navigation can inherit the previous page's network-idle state.
  // Wait for each route's rendered data as well as its loading placeholders.
  await page.waitForFunction(name => {
    const content = document.querySelector('main')
    if (!content) return false
    if ([...content.querySelectorAll('[role="status"][aria-label="正在加载"], [aria-busy="true"]')].some(element => element.checkVisibility())) return false
    const count = selector => content.querySelectorAll(selector).length
    const input = testId => content.querySelector(`[data-testid="${testId}"]`)
    switch (name) {
      case 'home': return count('.stat-block') === 4 && count('[data-testid="home-todos"] .list-row') > 0
      case 'points': return count('.mobile-records .list-row') > 0
      case 'point-detail': return count('.detail-grid') > 0 && count('.photo-grid img') > 0
      case 'inspections': return count('.mobile-records [data-testid="inspection-row"]') > 0
      case 'inspection-new': return Boolean(input('inspection-date')?.value && (input('inspection-inspector')?.value || input('inspection-inspector')?.closest('.names-editor')?.querySelector('.name-edit-tag')) && count('[data-testid="inspection-point-option"]') > 0)
      case 'inspection-detail': return count('.issue-block') >= 2 && Boolean(input('batch-review'))
      case 'matters': return Boolean(document.querySelector('main'))
      case 'activities': return count('[data-testid="activity-row"]') > 0
      case 'matter-new': return Boolean(input('matter-save') && !input('matter-save').disabled)
      case 'activity-new': return Boolean(input('activity-save') && !input('activity-save').disabled)
      case 'settings': return Boolean(content.querySelector('#settings-basic input')?.value)
      default: return false
    }
  }, name, { timeout: 10000 })
  await page.waitForLoadState('networkidle')
  await page.evaluate(async () => {
    await document.fonts.ready
    const images = [...document.images]
    images.forEach(image => { image.loading = 'eager' })
    await Promise.all(images.map(image => image.decode().catch(() => {})))
  })
  await page.mouse.move(0, 0)
  await page.waitForFunction(() => [...document.querySelectorAll('[data-swipe-direction]')].every(element => !element.checkVisibility({ checkOpacity: true, checkVisibilityCSS: true })), null, { timeout: 10000 })
}
async function visualAssertions(name, width) {
  const value = await page.evaluate(() => {
    function visible(element) {
      if (!element.checkVisibility({ checkOpacity: true, checkVisibilityCSS: true })) return false
      const style = getComputedStyle(element)
      return !element.closest('[aria-hidden="true"], [inert]') && style.clip !== 'rect(0px, 0px, 0px, 0px)' && !element.classList.contains('sr-only')
    }
    const header = document.querySelector('.page-header')?.getBoundingClientRect()
    const buttons = [...document.querySelectorAll('a[href],button,input:not([type=hidden]),textarea,select,[role=button],[role=checkbox],[role=tab],[role=option],[role=menuitem]')].filter(visible).map(element => {
      const rect = element.getBoundingClientRect()
      return { tag: element.tagName, label: element.getAttribute('aria-label') || element.textContent?.trim().slice(0, 60), width: rect.width, height: rect.height }
    })
    const smallText = []
    const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT)
    for (let text = walker.nextNode(); text; text = walker.nextNode()) {
      const element = text.parentElement
      if (!text.textContent.trim() || !element || !visible(element) || element.closest('script,style,svg,.mobile-tabs')) continue
      const size = parseFloat(getComputedStyle(element).fontSize)
      if (size < 12) smallText.push({ text: text.textContent.trim().slice(0, 60), size })
    }
    const filters = document.querySelector('[data-testid="list-filters"]')
    const columns = filters ? getComputedStyle(filters).gridTemplateColumns : null
    const dateInputs = [...(filters?.querySelectorAll('.filter-dates input[type="date"]') ?? [])].map(element => ({
      label: element.getAttribute('aria-label'), visible: visible(element)
    }))
    const dateToggle = filters?.querySelector('.filter-date-toggle')
    const dateToggleVisible = Boolean(dateToggle && visible(dateToggle))
    const tableRows = [...document.querySelectorAll('.table-wrap tbody tr')].filter(visible).map(element => ({
      height: element.getBoundingClientRect().height, text: element.textContent.trim().slice(0, 80)
    }))
    const coordinateLabel = [...document.querySelectorAll('.detail-grid dt')].find(element => element.textContent.trim() === '经纬度')
    const coordinateValue = coordinateLabel?.nextElementSibling
    const coordinates = coordinateValue ? { text: coordinateValue.textContent.trim(), height: coordinateValue.getBoundingClientRect().height, lineHeight: parseFloat(getComputedStyle(coordinateValue).lineHeight) } : null
    const nav = document.querySelector('.mobile-tabs')?.getBoundingClientRect()
    const visibleRows = [...document.querySelectorAll('.mobile-records [data-testid="inspection-row"]')].filter(element => {
      const rect = element.getBoundingClientRect()
      return visible(element) && rect.top >= 48 && rect.bottom <= (nav?.top ?? innerHeight)
    }).length
    function visiblePosition(element) {
      if (!element) return null
      const rect = element.getBoundingClientRect()
      return { visible: visible(element), top: rect.top, bottom: rect.bottom, height: rect.height }
    }
    const firstTodo = visiblePosition(document.querySelector('[data-testid="home-todos"] .list-row'))
    const stats = [...document.querySelectorAll('.stat-block')].map(element => ({
      ...visiblePosition(element), fontSize: parseFloat(getComputedStyle(element.querySelector('.stat-number')).fontSize)
    }))
    return { width: innerWidth, scrollWidth: document.documentElement.scrollWidth, headerHeight: header?.height, buttons, smallText, columns, dateInputs, dateToggleVisible, tableRows, coordinates, visibleRows, firstTodo, stats, firstTodoBottom: firstTodo?.bottom, lastStatBottom: stats.at(-1)?.bottom, navTop: nav?.top }
  })
  assert(value.scrollWidth <= width + 1, `${name}: horizontal overflow ${value.scrollWidth} > ${width}`)
  assert.deepEqual(value.smallText, [], `${name}: text smaller than 12 px`)
  if (name === 'inspections' || name === 'activities') {
    assert.equal(value.dateInputs.length, 2, `${name}: both date filters must render`)
    assert(value.dateInputs.every(input => input.visible === (width >= 768)), `${name}: date filters must be visible on desktop and initially collapsed on mobile`)
    assert.equal(value.dateToggleVisible, width < 768, `${name}: mobile date toggle must remain accessible`)
  }
  if (name === 'point-detail') {
    assert(value.coordinates, 'Point detail must show its coordinates')
    assert(value.coordinates.height <= value.coordinates.lineHeight + 1, 'Coordinate digits wrap onto a second line')
    assert.match(value.coordinates.text, /^-?\d+\.\d{6},-?\d+\.\d{6}$/, 'Coordinates must retain six decimal places')
  }
  if (width < 768) {
    const small = value.buttons.filter(button => button.width < 39.5 || button.height < 39.5)
    assert.deepEqual(small, [], `${name}: touch targets smaller than 40 × 40`)
    assert.equal(value.headerHeight, 48, `${name}: mobile header must be 48 px`)
    if (value.columns) assert.equal(value.columns.split(' ').length, 2, `${name}: mobile filters need two columns`)
    if (name === 'inspections' && width === 375) assert(value.visibleRows >= 5, `Only ${value.visibleRows} inspection rows are visible on the first screen`)
    if (name === 'home') assert(value.stats.every(stat => stat.fontSize <= 20), 'Mobile statistics exceed 20 px')
    if (name === 'home' && width === 375) {
      assert.equal(value.stats.length, 4, 'Home must render all four statistics')
      assert(value.stats.every(stat => stat.visible && stat.height > 0 && stat.top >= 48 && stat.bottom <= value.navTop), 'Statistics not visible on first screen')
      assert(value.firstTodo?.visible && value.firstTodo.height > 0 && value.firstTodo.top >= 48 && value.firstTodo.bottom <= value.navTop, 'First to-do row not visible on first screen')
    }
  } else {
    assert(value.headerHeight <= 64, `${name}: desktop header exceeds 64 px`)
    if (['points', 'inspections'].includes(name)) {
      assert(value.tableRows.length > 0, `${name}: desktop data rows must render`)
      assert(value.tableRows.every(row => Math.abs(row.height - 44) <= 0.5), `${name}: standard fixture rows must be 44 px: ${JSON.stringify(value.tableRows)}`)
    }
  }
  delete value.buttons
  report.layoutAssertions ??= []
  report.layoutAssertions.push({ name, ...value })
}
try {
  await waitForHealthy()
  const bootstrap = await api('/bootstrap')
  const point = await api('/points', { method: 'POST', body: { name: '浏览器验收点位', communityId: bootstrap.communities[0].id, typeId: bootstrap.pointTypes[0].id, address: '示例路 18 号', contact: '测试联系人', phone: '13800000008', frequency: 'weekly', notes: '自动化测试数据', disabled: false } })
  browser = await chromium.launch({ executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE || undefined, args: ['--no-proxy-server', '--host-resolver-rules=MAP hbtool.test 127.0.0.1, EXCLUDE localhost'] })
  report.browser = browser.version()
  const context = await browser.newContext({ viewport: { width: 1440, height: 900 }, locale: 'zh-CN', reducedMotion: 'reduce' })
  await context.route('**/*', async route => {
    const url = route.request().url()
    if (/^https?:/.test(url) && new URL(url).origin !== origin) { report.externalRequests.push(url); await route.abort('blockedbyclient') }
    else await route.continue()
  })
  context.on('request', request => { const url = request.url(); if (/\.(?:wasm|css|js)(?:\?|$)/.test(url)) report.resources.push(new URL(url).pathname) })
  page = await context.newPage()
  page.on('pageerror', error => report.pageErrors.push(String(error)))
  page.on('requestfailed', request => { report.failedRequests ??= []; report.failedRequests.push({ url: request.url(), error: request.failure()?.errorText, case: activeCase }) })
  page.on('console', message => {
    if (message.type() !== 'error') return
    const location = message.location()
    // The listener-change case deliberately restarts HTTP; SystemListen retries this health probe.
    const expectedRebindFailure = activeCase === 'Local listen popover: mutually exclusive all-interfaces selection and live rebind'
      && location.url === origin + '/api/health' && message.text().includes('net::ERR_CONNECTION_REFUSED')
    report.consoleErrors.push({ text: message.text(), location, case: activeCase, expectedWasmFailure, expectedRebindFailure })
  })

  if (process.argv.includes('--map-only')) {
    await verifyMapManagement({ browser, origin, api, test, bootstrap, evidence })
  } else if (process.argv.includes('--consistency-only')) {
    await verifyConsistency({ browser, origin, directOrigin, api, test, bootstrap, evidence })
  } else if (process.argv.includes('--fresh-only')) {
    await verifyFreshArchitecture({ browser, origin, api, test, bootstrap, point, evidence })
  } else {
  await test('Fresh HTTP page: offline resources, six navigation items and hash query persistence', async () => {
    await page.goto(origin + '/', { waitUntil: 'networkidle' }); await waitForText('逾期')
    report.secureContext = await page.evaluate(() => isSecureContext); assert.equal(report.secureContext, false)
    assert((await iconCount()) >= 5)
    assert.equal(await page.locator('.sidebar-nav-link').count(), 6)
    assert.equal(await page.locator('.mobile-tab').count(), 6)
    await page.goto(origin + '/#/points?q=' + encodeURIComponent(point.name), { waitUntil: 'networkidle' })
    await page.reload({ waitUntil: 'networkidle' }); await waitForText(point.name)
    assert(page.url().includes(encodeURIComponent(point.name)))
    await page.goto(origin + '/#/inspections?rectify=pending&overdue=true', { waitUntil: 'networkidle' })
    await page.waitForURL('**/#/inspections?**'); assert(page.url().includes('rectify=pending'))
    await page.setViewportSize({ width: 375, height: 812 })
  })

  await test('Home issue reminders retain mixed inspection states and matching problem counts', async () => {
    const dashboard = await api('/dashboard')
    assert(dashboard.awaitingReview.count > 0, 'Demo fixture must include a problem awaiting review')
    const mixed = await api('/inspections/' + dashboard.awaitingReview.items[0].inspectionId)
    assert(mixed.issues.some(issue => issue.status === 'pending') && mixed.issues.some(issue => issue.status === 'feedback'), 'Demo fixture must include mixed pending and feedback problems')
    await page.goto(origin + '/#/', { waitUntil: 'networkidle' })
    let response = page.waitForResponse(response => new URL(response.url()).pathname === '/api/inspections' && new URL(response.url()).searchParams.get('awaitingReview') === 'true' && response.status() === 200)
    await page.locator('.stat-block').filter({ hasText: '待复查' }).click()
    const awaiting = await (await response).json()
    assert.equal(awaiting.matchingIssueCount, dashboard.awaitingReview.count)
    assert(awaiting.items.some(item => item.id === mixed.inspection.id), 'Mixed inspection disappeared from the awaiting-review reminder')
    await waitForText(`涉及 ${awaiting.total} 条排查 · ${awaiting.matchingIssueCount} 项问题`)
    await page.getByRole('button', { name: '待复查问题', exact: true }).click()
    await page.waitForURL('**/#/inspections')
    await page.goto(origin + '/#/', { waitUntil: 'networkidle' })
    response = page.waitForResponse(response => new URL(response.url()).pathname === '/api/inspections' && new URL(response.url()).searchParams.get('openOnly') === 'true' && response.status() === 200)
    await page.getByTestId('home-all-todos').click()
    const open = await (await response).json()
    assert(open.items.every(item => item.openIssueCount > 0), 'Todo list contains inspections without open problems')
    assert(open.items.some(item => item.id === mixed.inspection.id), 'Todo list lost a mixed inspection')
    assert.equal(open.matchingIssueCount, open.items.reduce((sum, item) => sum + item.openIssueCount, 0))
    await waitForText(`涉及 ${open.total} 条排查 · ${open.matchingIssueCount} 项问题`)
    await page.getByRole('button', { name: '未闭环问题', exact: true }).click()
    await page.waitForURL('**/#/inspections')
    report.homeFilterChecks = { mixedInspectionId: mixed.inspection.id, awaitingReview: { inspectionCount: awaiting.total, issueCount: awaiting.matchingIssueCount }, openOnly: { inspectionCount: open.total, issueCount: open.matchingIssueCount } }
  })

  let inspectionId, issueId, closedInspectionId, remediationId, fixtureJpeg
  await test('Regular mobile inspection: worker JPEG, thumbnail, WASM fallback and version-4 draft', async () => {
    await page.goto(origin + '/#/inspections/new', { waitUntil: 'networkidle' })
    await page.getByTestId('inspection-point-search').fill(point.name)
    await page.getByTestId('inspection-point-option').filter({ hasText: point.name }).click()
    await page.getByTestId('inspection-date').fill(bootstrap.today)
    await page.getByTestId('inspection-inspector').fill('浏览器验收员')
    await page.getByTestId('inspection-result-issues').click()
    const description = '河岸边有散落生活垃圾，需要清理并加强日常巡查。'
    await selectOption(page.getByTestId('inspection-issue-type').first(), bootstrap.issueTypes[0].name)
    await page.getByTestId('inspection-issue-description').first().fill(description)
    await page.getByTestId('inspection-issue-deadline').first().fill(bootstrap.today)
    const encoded = await page.evaluate(() => {
      const canvas = document.createElement('canvas'); canvas.width = 2400; canvas.height = 1800
      const drawing = canvas.getContext('2d'), pixels = drawing.createImageData(canvas.width, canvas.height)
      let random = 137
      for (let y = 0; y < canvas.height; y++) for (let x = 0; x < canvas.width; x++) {
        random = (Math.imul(random, 1664525) + 1013904223) >>> 0
        const noise = ((random >>> 24) - 128) * 0.6, index = (y * canvas.width + x) * 4
        pixels.data[index] = 60 + x / 18 + noise; pixels.data[index + 1] = 90 + y / 22 + noise; pixels.data[index + 2] = 110 + Math.sin(x / 14) * 35 + noise; pixels.data[index + 3] = 255
      }
      drawing.putImageData(pixels, 0, 0); drawing.fillStyle = 'white'; drawing.font = 'bold 120px sans-serif'; drawing.fillText('UP', 70, 170)
      return canvas.toDataURL('image/jpeg', 0.96).split(',')[1]
    })
    fixtureJpeg = Buffer.from(encoded, 'base64')
    const first = await uploadIn(page.getByTestId('photo-file-input').first(), 'site.jpg', fixtureJpeg)
    assert(Math.max(first.photo.width, first.photo.height) <= 1600)
    const bytes = first.response.request().postDataBuffer(); assert(bytes && bytes[0] === 255 && bytes[1] === 216 && bytes.length < fixtureJpeg.length * 0.6)
    assert.equal(await page.locator('[data-compression-engine="mozjpeg-worker"]').count(), 1)
    report.compression = { inputBytes: fixtureJpeg.length, mozjpegBytes: bytes.length, width: first.photo.width, height: first.photo.height }
    expectedWasmFailure = true
    await context.route('**/*.wasm', route => route.abort('failed'))
    const fallback = await uploadIn(page.getByTestId('photo-file-input').first(), 'fallback.jpg', fixtureJpeg)
    assert.equal(await page.locator('[data-compression-engine="canvas-fallback"]').count(), 1)
    report.compression.canvasFallbackBytes = fallback.response.request().postDataBuffer().length
    await context.unroute('**/*.wasm'); expectedWasmFailure = false
    const draft = await page.evaluate(() => JSON.parse(localStorage.getItem('street-eco.inspection-draft.v4')))
    assert.equal(draft.version, 4)
    assert.deepEqual(draft.draft.issues[0].photos.map(photo => photo.id), [first.photo.id, fallback.photo.id])
    await page.reload({ waitUntil: 'networkidle' })
    assert.equal(await page.getByTestId('inspection-issue-description').first().inputValue(), description)
    assert.equal(await page.getByTestId('photo-uploaded').count(), 2)
    assert(!await page.locator('.mobile-tabs').isVisible())
    const saved = page.waitForResponse(response => new URL(response.url()).pathname === '/api/inspections' && response.request().method() === 'POST' && response.status() === 201)
    await page.getByTestId('inspection-save').click()
    const record = await (await saved).json(); inspectionId = record.inspection.id; issueId = record.issues[0].id
    await page.waitForURL('**/#/inspections/' + inspectionId)
    assert.equal(await page.evaluate(() => localStorage.getItem('street-eco.inspection-draft.v4')), null)
    assert.equal((await api('/issues/' + issueId)).photos.length, 2)
    await assertMobileActionLayout('已保存')
  })

  await test('Merged issue page: feedback, optional-photo review, return and history', async () => {
    await page.goto(origin + '/#/inspections/' + inspectionId + '?issue=' + issueId, { waitUntil: 'networkidle' })
    await page.waitForURL('**/#/inspections/' + inspectionId + '?issue=' + issueId)
    await page.getByTestId('batch-feedback').click()
    let dialog = page.getByRole('dialog', { name: '记录反馈', exact: true })
    await assertDialogInViewport(dialog, 'mobile-feedback')
    await dialog.getByTestId('batch-date').fill(bootstrap.today)
    await dialog.getByTestId('batch-notes-1').fill('社区已安排人员完成现场清理。')
    await uploadIn(dialog.getByTestId('photo-file-input'), 'after.jpg', fixtureJpeg)
    let response = page.waitForResponse(response => new URL(response.url()).pathname === `/api/inspections/${inspectionId}/feedback` && response.status() === 200)
    await dialog.getByTestId('batch-save').click(); await response; await waitForDialogsClosed()
    assert.equal((await api('/issues/' + issueId)).issue.status, 'feedback')
    await assertMobileActionLayout('已保存')
    await page.goto(origin + '/#/', { waitUntil: 'networkidle' })
    const todosResponse = page.waitForResponse(response => new URL(response.url()).pathname === '/api/inspections' && new URL(response.url()).searchParams.get('openOnly') === 'true' && response.status() === 200)
    await page.getByTestId('home-all-todos').click()
    const todos = await (await todosResponse).json()
    assert(todos.items.some(item => item.id === inspectionId && item.rectifyStatus === 'feedback'), 'Todo list lost an inspection whose only problem is awaiting review')
    report.homeFilterChecks.feedbackOnlyInspectionId = inspectionId
    await page.goto(origin + '/#/inspections/' + inspectionId + '?issue=' + issueId, { waitUntil: 'networkidle' })
    await page.getByTestId('batch-review').click()
    dialog = page.getByRole('dialog', { name: '复查闭环', exact: true })
    await assertDialogInViewport(dialog, 'mobile-review')
    await dialog.getByTestId('batch-notes-1').fill('现场复查已清理到位。')
    response = page.waitForResponse(response => new URL(response.url()).pathname === `/api/inspections/${inspectionId}/review` && response.status() === 200)
    await dialog.getByTestId('batch-save').click(); await response; await waitForDialogsClosed()
    assert.equal((await api('/issues/' + issueId)).issue.status, 'closed')
    await page.getByRole('button', { name: '退回待整改', exact: true }).click()
    await page.getByTestId('return-reason').fill('补充整改要求后继续跟进。')
    response = page.waitForResponse(response => new URL(response.url()).pathname === `/api/issues/${issueId}/return` && response.status() === 200)
    await page.getByTestId('return-save').click(); await response; await waitForDialogsClosed()
    const issue = await api('/issues/' + issueId)
    assert.equal(issue.issue.status, 'pending'); assert.equal(issue.actions.length, 3); assert.equal(issue.photos.length, 3)
    await page.getByRole('button', { name: '办理记录', exact: true }).click()
    await waitForText('补充整改要求后继续跟进。')
    await assertMobileActionLayout('已退回')
  })

  await test('Point pictures, coordinate validation and Gaode links', async () => {
    await page.goto(origin + '/#/points/' + point.id, { waitUntil: 'networkidle' })
    await page.getByRole('button', { name: '编辑', exact: true }).click()
    const dialog = page.getByRole('dialog', { name: '编辑点位', exact: true })
    await dialog.getByText('手动填写经纬度', { exact: true }).click()
    await dialog.getByLabel('经纬度', { exact: true }).fill('29.274331,88.893426')
    await dialog.getByTestId('point-save').click()
    await dialog.getByText('看起来顺序反了，应为 经度,纬度').waitFor()
    await dialog.getByLabel('经纬度', { exact: true }).fill('88.893426，29.274331')
    await uploadIn(dialog.getByTestId('photo-file-input'), 'point.jpg', fixtureJpeg)
    const response = page.waitForResponse(response => new URL(response.url()).pathname === '/api/points/' + point.id && response.request().method() === 'PUT' && response.status() === 200)
    await dialog.getByTestId('point-save').click(); await response; await waitForDialogsClosed()
    const result = await api('/points/' + point.id)
    assert.equal(result.point.longitude, 88.893426); assert.equal(result.point.latitude, 29.274331); assert.equal(result.photos.length, 1); assert(result.point.coverPhotoId)
    const marker = await page.getByRole('link', { name: '高德查看', exact: true }).getAttribute('href')
    const navigation = await page.getByRole('link', { name: '高德导航', exact: true }).getAttribute('href')
    assert(marker.includes('coordinate=gaode')); assert(navigation.includes('callnative=0') && !navigation.includes('coordinate='))
  })

  await test('Temporary-point draft, atomic creation, promotion and per-issue batch photo ownership', async () => {
    await page.goto(origin + '/#/inspections/new', { waitUntil: 'networkidle' })
    await page.getByTestId('temporary-point-mode').click()
    await page.getByTestId('temporary-point-name').fill('临时河岸排查点')
    await selectOption(page.getByLabel('社区', { exact: true }), bootstrap.communities[0].name)
    await selectOption(page.getByLabel('类型', { exact: true }), bootstrap.pointTypes[0].name)
    await page.getByLabel('详细地址', { exact: true }).fill('沿河路东侧临时堆放处')
    await page.getByText('手动填写经纬度', { exact: true }).click()
    await page.getByLabel('经纬度', { exact: true }).fill('88.893426 29.274331')
    await page.getByTestId('inspection-inspector').fill('临时点位验收员')
    await page.getByTestId('inspection-result-issues').click()
    await selectOption(page.getByTestId('inspection-issue-type').first(), bootstrap.issueTypes[0].name)
    await page.getByTestId('inspection-issue-description').first().fill('沿河道路存在垃圾堆积。')
    await page.getByRole('button', { name: '添加问题', exact: true }).click()
    await selectOption(page.getByTestId('inspection-issue-type').nth(1), bootstrap.issueTypes[1].name)
    await page.getByTestId('inspection-issue-description').nth(1).fill('路边排水沟有杂物堵塞。')
    const before = (await api('/points?q=' + encodeURIComponent('临时河岸排查点') + '&temporary=all')).total
    assert.equal(before, 0)
    await page.reload({ waitUntil: 'networkidle' })
    assert.equal(await page.getByTestId('temporary-point-mode').getAttribute('aria-pressed'), 'true')
    assert.equal(await page.getByTestId('temporary-point-name').inputValue(), '临时河岸排查点')
    assert.equal(await page.getByTestId('inspection-issue-description').count(), 2)
    let response = page.waitForResponse(response => new URL(response.url()).pathname === '/api/inspections' && response.request().method() === 'POST' && response.status() === 201)
    await page.getByTestId('inspection-save').click()
    const created = await (await response).json(); closedInspectionId = created.inspection.id
    assert.equal(created.inspection.pointTemporary, true)
    await page.waitForURL('**/#/inspections/' + closedInspectionId)
    await page.getByTestId('promote-point').click()
    response = page.waitForResponse(response => new URL(response.url()).pathname === `/api/points/${created.inspection.pointId}/promote` && response.status() === 200)
    await page.getByTestId('promote-confirm').click(); await response; await waitForDialogsClosed()
    assert.equal((await api('/points/' + created.inspection.pointId)).point.temporary, false)
    await page.getByTestId('batch-feedback').click()
    let dialog = page.getByRole('dialog', { name: '记录反馈', exact: true })
    assert(await dialog.getByTestId('batch-issue-1').isChecked()); assert(await dialog.getByTestId('batch-issue-2').isChecked())
    const photos = []
    for (let index = 0; index < 2; index++) {
      await dialog.getByTestId(`batch-notes-${index + 1}`).fill(`第 ${index + 1} 项已完成整改。`)
      photos.push((await uploadIn(dialog.getByTestId('photo-file-input').nth(index), `batch-${index + 1}.jpg`, fixtureJpeg)).photo.id)
    }
    response = page.waitForResponse(response => new URL(response.url()).pathname === `/api/inspections/${closedInspectionId}/feedback` && response.status() === 200)
    await dialog.getByTestId('batch-save').click()
    const batchResponse = await response
    const request = batchResponse.request().postDataJSON()
    assert.equal(request.items.length, 2)
    for (let index = 0; index < 2; index++) assert.deepEqual(request.items[index].photoIds, [photos[index]])
    await waitForDialogsClosed()
    for (let index = 0; index < 2; index++) assert.deepEqual((await api('/issues/' + created.issues[index].id)).photos.map(photo => photo.id), [photos[index]])
    await page.getByTestId('batch-review').click()
    dialog = page.getByRole('dialog', { name: '复查闭环', exact: true })
    await dialog.getByTestId('batch-notes-1').fill('垃圾已清运。'); await dialog.getByTestId('batch-notes-2').fill('排水沟已疏通。')
    response = page.waitForResponse(response => new URL(response.url()).pathname === `/api/inspections/${closedInspectionId}/review` && response.status() === 200)
    await dialog.getByTestId('batch-save').click(); await response; await waitForDialogsClosed()
    const final = await api('/inspections/' + closedInspectionId)
    assert.equal(final.inspection.rectifyStatus, 'closed'); assert.equal(final.inspection.closedCount, 2)
    await page.getByRole('button', { name: '导出', exact: true }).click()
    assert(await page.getByRole('menuitem', { name: '导出整改记录单', exact: true }).isVisible())
    await page.keyboard.press('Escape')
  })

  await test('Normal inspection persists and its notice number can be edited afterwards', async () => {
    await page.goto(origin + '/#/inspections/new?pointId=' + point.id, { waitUntil: 'networkidle' })
    assert.equal(await page.getByTestId('inspection-result-normal').getAttribute('aria-pressed'), 'true')
    let response = page.waitForResponse(response => new URL(response.url()).pathname === '/api/inspections' && response.request().method() === 'POST' && response.status() === 201)
    await page.getByTestId('inspection-save').click()
    const result = await (await response).json(); await page.waitForURL('**/#/inspections/' + result.inspection.id)
    assert.equal(result.inspection.result, 'normal'); assert.equal(result.issues.length, 0)
    await page.getByRole('button', { name: '更多', exact: true }).click(); await page.getByRole('menuitem', { name: '编辑排查信息', exact: true }).click()
    const dialog = page.getByRole('dialog', { name: '编辑排查信息', exact: true })
    await dialog.getByLabel('通知单编号', { exact: true }).fill('补录-2026-01')
    response = page.waitForResponse(response => new URL(response.url()).pathname === '/api/inspections/' + result.inspection.id && response.request().method() === 'PUT' && response.status() === 200)
    await dialog.getByRole('button', { name: '保存', exact: true }).click(); await response; await waitForDialogsClosed()
    assert.equal((await api('/inspections/' + result.inspection.id)).inspection.noticeNumber, '补录-2026-01')
  })

  await test('Remediation activity creation: local draft, three photo stages and quantity fields', async () => {
    await page.goto(origin + '/#/activities/new?template=remediation', { waitUntil: 'networkidle' })
    await page.getByTestId('activity-name').fill('东河沿岸卫生整治')
    await selectOption(page.getByTestId('activity-remediation-type'), bootstrap.remediationTypes[0].name)
    await page.getByTestId('activity-point-search').fill(point.name)
    await page.getByTestId('activity-point-option').filter({ hasText: point.name }).click()
    await page.getByRole('button', { name: '更多信息', exact: true }).click()
    await page.getByLabel('参加人数', { exact: true }).fill('12'); await page.getByTestId('activity-garbage-tons').fill('1.25')
    await page.getByTestId('activity-vehicle-count').fill('0')
    await page.getByTestId('activity-content').fill('清理河岸和道路两侧垃圾，疏通排水沟。')
    for (const phase of ['before', 'process', 'after']) await uploadIn(page.getByTestId('activity-' + phase + '-photos').getByTestId('photo-file-input'), `activity-${phase}.jpg`, fixtureJpeg)
    await page.reload({ waitUntil: 'networkidle' })
    assert.equal(await page.getByTestId('activity-name').inputValue(), '东河沿岸卫生整治'); assert.equal(await page.getByTestId('photo-uploaded').count(), 3)
    assert.equal(await page.getByLabel('参加人数', { exact: true }).inputValue(), '12')
    assert.equal(await page.getByTestId('activity-garbage-tons').inputValue(), '1.25')
    assert.equal(await page.getByTestId('activity-vehicle-count').inputValue(), '0')
    const response = page.waitForResponse(response => new URL(response.url()).pathname === '/api/activities' && response.request().method() === 'POST' && response.status() === 201)
    await page.getByTestId('activity-save').click()
    const result = await (await response).json(); remediationId = result.activity.id
    await page.waitForURL('**/#/activities/' + remediationId)
    assert.equal(result.activity.participantCount, 12); assert.equal(result.activity.remediation.garbageTons, 1.25); assert.equal(result.activity.remediation.vehicleCount, 0)
    assert.deepEqual(result.photos.map(photo => photo.phase).sort(), ['after', 'before', 'process'])
    assert.equal(await page.evaluate(() => localStorage.getItem('street-eco.activity-draft.v2')), null)
  })

  let activityId
  await test('Activity creation: type management, local photo compression, draft and Word record', async () => {
    await page.goto(origin + '/#/activities/new', { waitUntil: 'networkidle' })
    await page.getByTestId('activity-name').fill('环保工作部署会议')
    await selectOption(page.getByTestId('activity-type'), bootstrap.activityTypes.find(type => type.template === 'general').name)
    await page.getByTestId('activity-content').fill('安排近期环保专项宣传和重点问题排查工作。')
    await uploadIn(page.getByTestId('photo-file-input'), 'meeting.jpg', fixtureJpeg)
    await page.reload({ waitUntil: 'networkidle' })
    assert.equal(await page.getByTestId('activity-name').inputValue(), '环保工作部署会议')
    const response = page.waitForResponse(response => new URL(response.url()).pathname === '/api/activities' && response.request().method() === 'POST' && response.status() === 201)
    await page.getByTestId('activity-save').click()
    const result = await (await response).json()
    activityId = result.activity.id
    await page.waitForURL('**/#/activities/' + activityId)
    assert.equal(result.photos.length, 1)
    assert.equal(result.activity.communityId, null)
    const download = page.waitForEvent('download')
    await page.getByTestId('export-activity-record').click()
    assert((await download).suggestedFilename().endsWith('.docx'))
  })

  await test('Nine Word exports download through the current UI', async () => {
    await verifyWordDownloads({ page, origin, evidence, inspectionId: closedInspectionId, remediationId, activityId, communityId: point.communityId, today: bootstrap.today, report, screenshot })
  })

  await test('Local listen popover: mutually exclusive all-interfaces selection and live rebind', async () => {
    await page.goto(origin + '/#/', { waitUntil: 'networkidle' })
    await page.getByTestId('listen-trigger').click()
    assert(await page.getByRole('checkbox', { name: '仅本机', exact: true }).isChecked())
    await page.getByRole('checkbox', { name: '所有网卡', exact: true }).check()
    assert(!await page.getByRole('checkbox', { name: '仅本机', exact: true }).isChecked())
    let response = page.waitForResponse(response => new URL(response.url()).pathname === '/api/system/listen' && response.request().method() === 'PUT' && response.status() === 200)
    await page.getByTestId('listen-apply').click(); await response
    await page.waitForFunction(() => document.querySelector('[data-testid="listen-trigger"]')?.textContent.includes('局域网'), null, { timeout: 12000 })
    assert.deepEqual((await api('/system')).listen, ['0.0.0.0'])
    await page.getByRole('checkbox', { name: '仅本机', exact: true }).check()
    response = page.waitForResponse(response => new URL(response.url()).pathname === '/api/system/listen' && response.status() === 200)
    await page.getByTestId('listen-apply').click(); await response
    await page.waitForFunction(() => document.querySelector('[data-testid="listen-trigger"]')?.textContent.includes('仅本机'), null, { timeout: 12000 })
    assert.deepEqual((await api('/system')).listen, ['127.0.0.1'])
    await page.keyboard.press('Escape')
  })

  const fresh = await verifyFreshArchitecture({ browser, origin, api, test, bootstrap, point, evidence })

  await test('Backup restore: preview leaves live data intact, confirmation is required, photographs survive', async () => {
    const response = await fetch(directOrigin + '/api/backups/download'); assert.equal(response.status, 200)
    const backup = Buffer.from(await response.arrayBuffer())
    const extra = await api('/communities', { method: 'POST', body: { name: '恢复前临时社区', title: '恢复前临时社区居民委员会' } })
    await page.goto(origin + '/#/settings?tab=backup', { waitUntil: 'networkidle' })
    await page.getByTestId('restore-file-input').setInputFiles({ name: 'browser-backup.zip', mimeType: 'application/zip', buffer: backup })
    const dialog = page.getByRole('dialog', { name: '恢复备份？', exact: true })
    await assertDialogInViewport(dialog, 'mobile-restore')
    assert(await dialog.getByTestId('restore-confirm').isDisabled())
    assert((await api('/communities')).some(community => community.id === extra.id))
    await dialog.getByRole('checkbox').check()
    const restored = page.waitForResponse(result => new URL(result.url()).pathname === '/api/backups/restore' && result.status() === 200)
    await dialog.getByTestId('restore-confirm').click()
    assert((await (await restored).json()).preservedBackup.startsWith('backup/before-restore-'))
    await waitForDialogsClosed()
    assert(!(await api('/communities')).some(community => community.id === extra.id))
    assert.equal((await api('/issues/' + issueId)).photos.length, 3)
    assert.equal((await api('/activities/' + remediationId)).photos.length, 3)
    assert.equal((await api('/matters/' + fresh.matterId)).actions.length, 3)
    assert.equal((await api('/tags')).find(tag => tag.id === fresh.tagId).usageCount, 2)
    const attachment = (await api('/points/' + point.id)).attachments.find(item => item.id === fresh.attachmentId)
    assert.equal(attachment.name, fresh.attachmentName)
    const original = await fetch(directOrigin + '/api/attachments/' + fresh.attachmentId)
    assert.equal(original.status, 200)
    const restoredBytes = Buffer.from(await original.arrayBuffer())
    assert.equal(restoredBytes.length, fresh.attachmentSize)
    assert.equal(createHash('sha256').update(restoredBytes).digest('hex'), fresh.attachmentSha256.toLowerCase())
  })

  await test('Thirty-three screenshots and responsive design assertions across eleven pages', async () => {
    for (let index = 0; index < 6; index++) await api('/inspections', { method: 'POST', body: { pointId: point.id, date: bootstrap.today, inspector: '列表验收员 ' + (index + 1), result: 'normal' } })
    const pages = [ ['home', '/', '首页'], ['points', '/points', '点位管理'], ['point-detail', '/points/' + point.id, point.name], ['inspections', '/inspections', '排查记录'], ['inspection-new', '/inspections/new', '新增排查'], ['inspection-detail', '/inspections/' + closedInspectionId, '临时河岸排查点'], ['matters', '/matters', '事项办理'], ['matter-new', '/matters/new', '登记事项'], ['activities', '/activities', '活动记录'], ['activity-new', '/activities/new', '新增活动'], ['settings', '/settings?tab=basic', '工具设置'] ]
    report.screenshots = []
    for (const [width, height] of [[375, 812], [768, 1024], [1440, 900]]) {
      await page.setViewportSize({ width, height })
      for (const [name, route, title] of pages) {
        await page.goto(origin + '/#' + route, { waitUntil: 'networkidle' })
        if (name === 'settings') await page.reload({ waitUntil: 'networkidle' })
        await waitForVisualPage(name, title)
        await visualAssertions(name, width)
        const file = `${name}-${width}x${height}.png`
        await page.screenshot({ path: path.join(screens, file), fullPage: true, animations: 'disabled' })
        report.screenshots.push(file)
      }
    }
    await page.setViewportSize({ width: 360, height: 800 })
    for (const [name, route, title] of pages) { await page.goto(origin + '/#' + route, { waitUntil: 'networkidle' }); if (name === 'settings') await page.reload({ waitUntil: 'networkidle' }); await waitForVisualPage(name, title); await visualAssertions(name, 360) }
    assert.equal(report.screenshots.length, pages.length * 3)
  })

  await test('Fully offline UI keeps its local fonts, colors and icons', async () => {
    await page.goto(origin + '/#/settings?tab=basic', { waitUntil: 'networkidle' }); await page.reload({ waitUntil: 'networkidle' }); await waitForText('街道')
    const before = await iconCount(); await context.setOffline(true)
    assert((await iconCount()) >= before)
    const style = await page.evaluate(() => ({ font: getComputedStyle(document.body).fontFamily, background: getComputedStyle(document.body).backgroundColor }))
    assert(style.font.includes('Microsoft YaHei') && style.background !== 'rgba(0, 0, 0, 0)')
    await noHorizontalOverflow(); await context.setOffline(false)
  })

  await test('Unsupported browser displays the Chinese compatibility screen', async () => {
    const legacy = await context.newPage()
    await legacy.addInitScript(() => Object.defineProperty(window.CSS, 'registerProperty', { configurable: true, value: undefined }))
    await legacy.goto(origin + '/', { waitUntil: 'networkidle' })
    assert(await legacy.locator('#unsupported-browser').isVisible()); assert((await legacy.locator('#unsupported-browser').textContent()).includes('请升级浏览器'))
    await legacy.screenshot({ path: path.join(evidence, 'unsupported-browser.png'), fullPage: true, animations: 'disabled' }); await legacy.close()
  })
  await verifyOptimization({ browser, origin, directOrigin, api, test, report, bootstrap, fixtureJpeg })
  await verifyRound3({ browser, origin, directOrigin, api, test, report, bootstrap, fixtureJpeg, evidence })
  await verifyUsability({ browser, origin, api, test, bootstrap, activityId, evidence, directOrigin, fixtureJpeg })
  await verifyConsistency({ browser, origin, directOrigin, api, test, bootstrap, evidence })
  await verifyMapManagement({ browser, origin, api, test, bootstrap, evidence })
  }
  assert.deepEqual(report.externalRequests, [], 'The app attempted to load an external origin')
  assert.deepEqual(report.pageErrors, [], 'Unhandled browser errors occurred')
  assert.deepEqual(report.consoleErrors.filter(entry => !entry.expectedWasmFailure && !entry.expectedRebindFailure), [], 'Unexpected browser console errors occurred')
  report.passed = true
} catch (error) {
  report.error = error.stack || String(error); console.error(report.error)
  if (page) { await screenshot('failure').catch(() => {}); await writeFile(path.join(evidence, 'failure.html'), await page.content().catch(() => '')).catch(() => {}) }
  process.exitCode = 1
} finally {
  if (browser) await browser.close()
  if (application.exitCode === null) application.kill('SIGINT')
  const ended = await Promise.race([applicationExit, new Promise(resolve => setTimeout(() => resolve(null), 10000))])
  if (!ended) { application.kill('SIGKILL'); report.shutdown = { timedOut: true }; report.passed = false; process.exitCode = 1; await applicationExit }
  else { report.shutdown = ended; if (ended.code !== 0) { report.passed = false; process.exitCode = 1 } }
  log.end(); report.resources = [...new Set(report.resources)]
  await writeFile(path.join(evidence, 'browser-report.json'), JSON.stringify(report, null, 2)); await rm(temporary, { recursive: true, force: true })
  console.log(JSON.stringify({ passed: report.passed, cases: report.cases.length, externalRequests: report.externalRequests.length, screenshots: report.screenshots?.length ?? 0, evidence }))
}
