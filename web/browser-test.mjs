import assert from 'node:assert/strict'
import { spawn } from 'node:child_process'
import { createServer } from 'node:net'
import { once } from 'node:events'
import { cp, mkdir, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises'
import { createWriteStream } from 'node:fs'
import { tmpdir } from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { chromium } from 'playwright'
import { verifyWordDownloads } from './word-browser-test.mjs'

const directory = path.dirname(fileURLToPath(import.meta.url))
const executableArgument = process.argv.indexOf('--executable')
assert(executableArgument >= 0 && process.argv[executableArgument + 1], '--executable is required')
const sourceExecutable = path.resolve(process.argv[executableArgument + 1])
const evidence = path.join(directory, 'test-results')
await mkdir(evidence, { recursive: true })
const temporary = await mkdtemp(path.join(tmpdir(), 'hbtool-browser-'))
const installation = path.join(temporary, 'application')
await mkdir(installation)
const executable = path.join(installation, path.basename(sourceExecutable))
await cp(sourceExecutable, executable)
await cp(path.join(path.dirname(sourceExecutable), 'wwwroot'), path.join(installation, 'wwwroot'), { recursive: true })

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

try {
  await waitForHealthy()
  const bootstrap = await api('/bootstrap')
  const point = await api('/points', { method: 'POST', body: {
    name: '浏览器验收点位', communityId: bootstrap.communities[0].id, typeId: bootstrap.pointTypes[0].id,
    address: '示例路 18 号', contact: '测试联系人', phone: '13800000008', frequency: 'weekly', notes: '自动化验收使用的合成数据。', disabled: false
  } })
  browser = await chromium.launch({ args: ['--no-proxy-server', '--host-resolver-rules=MAP hbtool.test 127.0.0.1, EXCLUDE localhost'] })
  report.browser = browser.version()
  const context = await browser.newContext({ viewport: { width: 1440, height: 1040 }, locale: 'zh-CN' })
  await context.route('**/*', async (route) => {
    const url = route.request().url()
    if (/^https?:/.test(url) && new URL(url).origin !== origin) {
      report.externalRequests.push(url)
      await route.abort('blockedbyclient')
    } else await route.continue()
  })
  context.on('request', (request) => {
    const url = request.url()
    if (/\.(?:wasm|css|js)(?:\?|$)/.test(url)) report.resources.push(new URL(url).pathname)
  })
  page = await context.newPage()
  page.on('pageerror', (error) => report.pageErrors.push(String(error)))
  page.on('console', (message) => {
    if (message.type() === 'error') report.consoleErrors.push({ text: message.text(), expectedWasmFailure })
  })

  await test('Fresh HTTP page with external origins blocked, local styles and icons', async () => {
    await page.goto(origin + '/', { waitUntil: 'networkidle' })
    await waitForText('逾期')
    report.secureContext = await page.evaluate(() => isSecureContext)
    assert.equal(report.secureContext, false)
    assert((await iconCount()) >= 5, 'Local Lucide icons did not render')
    await noHorizontalOverflow()
    await screenshot('desktop-dashboard')
  })

  await test('Hash navigation, query persistence and responsive layouts', async () => {
    await page.goto(origin + '/#/points?q=' + encodeURIComponent(point.name), { waitUntil: 'networkidle' })
    await waitForText(point.name)
    await page.reload({ waitUntil: 'networkidle' })
    assert(page.url().includes(encodeURIComponent(point.name)), 'URL filter was lost on reload')
    await waitForText(point.name)
    await screenshot('desktop-points')
    await page.goto(origin + '/#/issues?status=pending&overdue=true', { waitUntil: 'networkidle' })
    await waitForText('整改')
    await screenshot('desktop-issues')
    await page.setViewportSize({ width: 390, height: 844 })
    await page.goto(origin + '/#/', { waitUntil: 'networkidle' })
    await waitForText('逾期')
    await noHorizontalOverflow()
    assert(await page.locator('.mobile-tabs').isVisible(), 'Mobile tabs are missing')
    assert(!await page.locator('.desktop-sidebar').isVisible(), 'Desktop navigation is visible on mobile')
    await screenshot('mobile-dashboard')
  })

  let inspectionId
  let issueId
  let fixtureJpeg
  await test('Mobile inspection: JPEG workers, actual WASM failure fallback and durable draft', async () => {
    await page.goto(origin + '/#/inspections/new', { waitUntil: 'networkidle' })
    await page.getByTestId('inspection-point-search').fill(point.name)
    await page.getByTestId('inspection-point-option').filter({ hasText: point.name }).click()
    await page.getByTestId('inspection-date').fill(bootstrap.today)
    await page.getByTestId('inspection-inspector').fill('浏览器验收员')
    await page.getByTestId('inspection-result-issues').click()
    const description = '河岸边有散落生活垃圾，需要清理并加强日常巡查。'
    await page.getByTestId('inspection-issue-description').first().fill(description)
    await page.getByTestId('inspection-issue-deadline').first().fill(bootstrap.today)
    const encoded = await page.evaluate(() => {
      const canvas = document.createElement('canvas')
      canvas.width = 2400; canvas.height = 1800
      const drawing = canvas.getContext('2d')
      const pixels = drawing.createImageData(canvas.width, canvas.height)
      let random = 137
      for (let y = 0; y < canvas.height; y++) for (let x = 0; x < canvas.width; x++) {
        random = (Math.imul(random, 1664525) + 1013904223) >>> 0
        const noise = ((random >>> 24) - 128) * 0.6
        const index = (y * canvas.width + x) * 4
        pixels.data[index] = 60 + x / 18 + noise
        pixels.data[index + 1] = 90 + y / 22 + noise
        pixels.data[index + 2] = 110 + Math.sin(x / 14) * 35 + noise
        pixels.data[index + 3] = 255
      }
      drawing.putImageData(pixels, 0, 0)
      drawing.fillStyle = '#fff'; drawing.font = 'bold 120px sans-serif'; drawing.fillText('UP', 70, 170)
      return canvas.toDataURL('image/jpeg', 0.96).split(',')[1]
    })
    fixtureJpeg = Buffer.from(encoded, 'base64')
    const uploaded = page.waitForResponse((response) => new URL(response.url()).pathname === '/api/photos' && response.request().method() === 'PUT' && response.status() === 201)
    await page.getByTestId('photo-file-input').first().setInputFiles({ name: 'inspection-site.jpg', mimeType: 'image/jpeg', buffer: fixtureJpeg })
    const response = await uploaded
    const firstPhoto = await response.json()
    assert(Math.max(firstPhoto.width, firstPhoto.height) <= 1600)
    await page.locator('[data-testid="photo-uploaded"][data-compression-engine="mozjpeg-worker"]').first().waitFor()
    const jpegBytes = response.request().postDataBuffer()
    assert(jpegBytes && jpegBytes[0] === 255 && jpegBytes[1] === 216)
    assert(jpegBytes.length < fixtureJpeg.length * 0.6, 'Compression did not reduce the synthetic photo enough')
    report.compression = { inputBytes: fixtureJpeg.length, mozjpegBytes: jpegBytes.length, width: firstPhoto.width, height: firstPhoto.height }

    expectedWasmFailure = true
    await context.route('**/*.wasm', (route) => route.abort('failed'))
    const fallbackUploaded = page.waitForResponse((result) => new URL(result.url()).pathname === '/api/photos' && result.request().method() === 'PUT' && result.status() === 201)
    await page.getByTestId('photo-file-input').first().setInputFiles({ name: 'inspection-fallback.jpg', mimeType: 'image/jpeg', buffer: fixtureJpeg })
    const fallbackResponse = await fallbackUploaded
    const fallbackPhoto = await fallbackResponse.json()
    assert(Math.max(fallbackPhoto.width, fallbackPhoto.height) <= 1600)
    await page.locator('[data-testid="photo-uploaded"][data-compression-engine="canvas-fallback"]').first().waitFor()
    report.compression.canvasFallbackBytes = fallbackResponse.request().postDataBuffer().length
    await context.unroute('**/*.wasm')
    expectedWasmFailure = false
    await page.getByTestId('inspection-save').waitFor({ state: 'visible' })
    await page.waitForFunction(() => !document.querySelector('[data-testid="inspection-save"]')?.disabled)
    const draftBefore = await page.evaluate(() => JSON.parse(localStorage.getItem('street-eco.inspection-draft.v1')))
    assert.deepEqual(draftBefore.draft.issues[0].photos.map((photo) => photo.id), [firstPhoto.id, fallbackPhoto.id])
    await page.reload({ waitUntil: 'networkidle' })
    assert.equal(await page.getByTestId('inspection-issue-description').first().inputValue(), description)
    assert.equal(await page.getByTestId('photo-uploaded').count(), 2)
    await noHorizontalOverflow()
    await screenshot('mobile-inspection-draft')
    const saved = page.waitForResponse((result) => new URL(result.url()).pathname === '/api/inspections' && result.request().method() === 'POST' && result.status() === 201)
    await page.getByTestId('inspection-save').click()
    const savedInspection = await (await saved).json()
    inspectionId = savedInspection.inspection.id
    issueId = savedInspection.issues[0].id
    await page.waitForURL('**/#/inspections/' + inspectionId)
    assert.equal(await page.evaluate(() => localStorage.getItem('street-eco.inspection-draft.v1')), null)
    assert.equal((await api('/issues/' + issueId)).photos.length, 2)
    await assertMobileActionLayout('排查已保存')
    await screenshot('mobile-inspection-saved')
  })

  await test('Feedback, review without a required photo, and return with historical evidence', async () => {
    await page.goto(origin + '/#/issues/' + issueId, { waitUntil: 'networkidle' })
    await page.getByTestId('feedback-open').click()
    await page.getByTestId('feedback-date').fill(bootstrap.today)
    await page.getByTestId('feedback-notes').fill('社区已安排人员完成现场清理。')
    const uploaded = page.waitForResponse((response) => new URL(response.url()).pathname === '/api/photos' && response.request().method() === 'PUT' && response.status() === 201)
    await page.getByRole('dialog').getByTestId('photo-file-input').setInputFiles({ name: 'after.jpg', mimeType: 'image/jpeg', buffer: fixtureJpeg })
    await uploaded
    const feedbackSaved = page.waitForResponse((response) => new URL(response.url()).pathname === '/api/issues/' + issueId + '/feedback' && response.status() === 200)
    await page.getByTestId('feedback-save').click()
    await feedbackSaved
    await page.getByTestId('review-open').waitFor()
    await waitForDialogsClosed()
    assert.equal((await api('/issues/' + issueId)).issue.status, 'feedback')
    await assertMobileActionLayout('社区反馈已记录')
    await screenshot('mobile-before-after')
    await page.getByTestId('review-open').click()
    await page.getByTestId('review-date').fill(bootstrap.today)
    await page.getByTestId('review-conclusion').fill('现场复查已清理到位，予以闭环。')
    const reviewed = page.waitForResponse((response) => new URL(response.url()).pathname === '/api/issues/' + issueId + '/review' && response.status() === 200)
    await page.getByTestId('review-save').click()
    await reviewed
    await waitForDialogsClosed()
    assert.equal((await api('/issues/' + issueId)).issue.status, 'closed')
    await assertMobileActionLayout('已复查闭环')
    await page.getByTestId('return-open').click()
    await page.getByTestId('return-reason').fill('补充整改要求后继续跟进。')
    const returned = page.waitForResponse((response) => new URL(response.url()).pathname === '/api/issues/' + issueId + '/return' && response.status() === 200)
    await page.getByTestId('return-save').click()
    await returned
    await page.getByTestId('feedback-open').waitFor()
    await waitForDialogsClosed()
    const detail = await api('/issues/' + issueId)
    assert.equal(detail.issue.status, 'pending')
    assert.equal(detail.actions.length, 3)
    assert.equal(detail.photos.length, 3)
    await noHorizontalOverflow()
    await assertMobileActionLayout('已退回待整改')
    await screenshot('mobile-issue-history')
  })

  await test('Normal inspection produces a durable record', async () => {
    await page.goto(origin + '/#/inspections/new?pointId=' + point.id, { waitUntil: 'networkidle' })
    await page.getByTestId('inspection-result-normal').waitFor()
    assert.equal(await page.getByTestId('inspection-result-normal').getAttribute('aria-pressed'), 'true')
    const saved = page.waitForResponse((response) => new URL(response.url()).pathname === '/api/inspections' && response.request().method() === 'POST' && response.status() === 201)
    await page.getByTestId('inspection-save').click()
    const result = await (await saved).json()
    await page.waitForURL('**/#/inspections/' + result.inspection.id)
    assert.equal(result.inspection.result, 'normal')
    assert.equal(result.issues.length, 0)
    assert.equal((await api('/inspections/' + result.inspection.id)).inspection.pointId, point.id)
  })

  await test('Word record, notice and ledger download through the actual UI', async () => {
    await verifyWordDownloads({ page, origin, evidence, inspectionId, communityId: point.communityId, today: bootstrap.today, report, screenshot })
  })

  await test('Backup restore requires a visible second confirmation and preserves old data', async () => {
    const response = await fetch(directOrigin + '/api/backups/download')
    assert.equal(response.status, 200)
    const backup = Buffer.from(await response.arrayBuffer())
    const extra = await api('/communities', { method: 'POST', body: { name: '恢复前临时社区', title: '恢复前临时社区居民委员会' } })
    await page.goto(origin + '/#/settings', { waitUntil: 'networkidle' })
    await page.getByTestId('restore-file-input').setInputFiles({ name: 'browser-backup.zip', mimeType: 'application/zip', buffer: backup })
    const dialog = page.getByRole('dialog', { name: '核对备份并确认恢复' })
    await dialog.waitFor()
    const restoreButton = dialog.getByRole('button', { name: '确认覆盖并恢复' })
    assert(await restoreButton.isDisabled(), 'Restore must be disabled until the user confirms')
    assert((await api('/communities')).some((community) => community.id === extra.id), 'Upload must not replace live data')
    await dialog.getByRole('checkbox').check()
    await screenshot('mobile-restore-confirmation')
    const restored = page.waitForResponse((result) => new URL(result.url()).pathname === '/api/backups/restore' && result.status() === 200)
    await restoreButton.click()
    const restoredResult = await (await restored).json()
    assert(restoredResult.preservedBackup.startsWith('backup/before-restore-'))
    await waitForDialogsClosed()
    assert(!(await api('/communities')).some((community) => community.id === extra.id))
    assert.equal((await api('/issues/' + issueId)).photos.length, 3)
  })

  await test('Settings and local UI remain styled when the browser is fully offline', async () => {
    await page.goto(origin + '/#/settings', { waitUntil: 'networkidle' })
    await waitForText('街道')
    const before = await iconCount()
    await screenshot('mobile-settings')
    await context.setOffline(true)
    assert((await iconCount()) >= before, 'Bundled icons disappeared offline')
    const style = await page.evaluate(() => ({ font: getComputedStyle(document.body).fontFamily, background: getComputedStyle(document.body).backgroundColor }))
    assert(style.font.includes('Microsoft YaHei') && style.background !== 'rgba(0, 0, 0, 0)')
    await noHorizontalOverflow()
    await screenshot('mobile-offline')
    await context.setOffline(false)
  })

  await test('Unsupported browser gets a Chinese explanation', async () => {
    const legacy = await context.newPage()
    await legacy.addInitScript(() => Object.defineProperty(window.CSS, 'registerProperty', { configurable: true, value: undefined }))
    await legacy.goto(origin + '/', { waitUntil: 'networkidle' })
    assert(await legacy.locator('#unsupported-browser').isVisible())
    assert((await legacy.locator('#unsupported-browser').textContent()).includes('请升级浏览器'))
    await legacy.screenshot({ path: path.join(evidence, 'unsupported-browser.png'), fullPage: true, animations: 'disabled' })
    await legacy.close()
  })
  assert.deepEqual(report.externalRequests, [], 'The app attempted to load an external origin')
  assert.deepEqual(report.pageErrors, [], 'Unhandled browser errors occurred')
  assert.deepEqual(report.consoleErrors.filter((entry) => !entry.expectedWasmFailure), [], 'Unexpected browser console errors occurred')
  report.passed = true
} catch (error) {
  report.error = error.stack || String(error)
  console.error(report.error)
  if (page) {
    await screenshot('failure').catch(() => {})
    await writeFile(path.join(evidence, 'failure.html'), await page.content().catch(() => '')).catch(() => {})
  }
  process.exitCode = 1
} finally {
  if (browser) await browser.close()
  if (application.exitCode === null) application.kill('SIGINT')
  const ended = await Promise.race([applicationExit, new Promise((resolve) => setTimeout(() => resolve(null), 10000))])
  if (!ended) {
    application.kill('SIGKILL')
    report.shutdown = { timedOut: true }
    report.passed = false
    process.exitCode = 1
    await applicationExit
  } else {
    report.shutdown = ended
    if (ended.code !== 0) { report.passed = false; process.exitCode = 1 }
  }
  log.end()
  report.resources = [...new Set(report.resources)]
  await writeFile(path.join(evidence, 'browser-report.json'), JSON.stringify(report, null, 2))
  await rm(temporary, { recursive: true, force: true })
  console.log(JSON.stringify({ passed: report.passed, cases: report.cases.length, externalRequests: report.externalRequests.length, evidence }))
}
