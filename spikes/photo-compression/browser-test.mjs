import assert from 'node:assert/strict'
import { readFile, writeFile } from 'node:fs/promises'
import { pathToFileURL, fileURLToPath } from 'node:url'
import { createServer } from 'node:http'
import { extname, resolve, sep } from 'node:path'

const playwright = process.env.CODEX_PRIMARY_RUNTIME_NODE_MODULES
  ? await import(pathToFileURL(`${process.env.CODEX_PRIMARY_RUNTIME_NODE_MODULES}/playwright/index.mjs`).href)
  : await import('playwright')
const origin = process.env.PHOTO_SPIKE_ORIGIN ?? 'http://hbtool-spike.test:5179'
const root = fileURLToPath(new URL('./dist/', import.meta.url))
const server = createServer(async (request, response) => {
  const path = resolve(root, '.' + new URL(request.url, origin).pathname.replace(/\/$/, '/index.html'))
  if (!path.startsWith(root.endsWith(sep) ? root : root + sep)) { response.writeHead(403).end(); return }
  try {
    const body = await readFile(path)
    response.writeHead(200, {
      'Content-Type': ({ '.html': 'text/html', '.js': 'text/javascript', '.wasm': 'application/wasm' })[extname(path)] ?? 'application/octet-stream',
      'Cache-Control': 'no-store',
    }).end(body)
  } catch { response.writeHead(404).end() }
})
await new Promise((resolve, reject) => { server.on('error', reject); server.listen(Number(new URL(origin).port), '0.0.0.0', resolve) })
const externalRequests = []
const wasmRequests = []
const errors = []
const browser = await playwright.chromium.launch({
  executablePath: process.env.PHOTO_SPIKE_BROWSER_EXECUTABLE || undefined,
  args: [
    '--no-sandbox', '--disable-dev-shm-usage', '--disable-gpu',
    '--host-resolver-rules=MAP hbtool-spike.test 127.0.0.1',
    ...(process.env.PHOTO_SPIKE_BROWSER_ARGS ? JSON.parse(process.env.PHOTO_SPIKE_BROWSER_ARGS) : []),
  ],
  headless: true,
})
try {
  const context = await browser.newContext()
  await context.route('**/*', route => {
    const url = route.request().url()
    if (new URL(url).origin !== origin) { externalRequests.push(url); return route.abort() }
    if (url.includes('.wasm')) wasmRequests.push(url)
    return route.continue()
  })
  const page = await context.newPage()
  page.on('pageerror', error => errors.push(error.message))
  await page.goto(origin)
  await page.waitForFunction(() => typeof window.runSpike === 'function')
  const primary = await page.evaluate(() => window.runSpike())
  assert.equal(primary.passed, true, 'EXIF/size/JPEG/concurrency assertions')
  assert.equal(primary.secureContext, false, 'Must verify on an ordinary HTTP origin')
  assert.equal(primary.crossOriginIsolated, false)
  assert.equal(primary.sharedArrayBufferAvailable, false)
  assert(primary.orientations.every(item => item.engine === 'mozjpeg-worker'))
  assert.equal(primary.large.engine, 'mozjpeg-worker')
  assert.equal(externalRequests.length, 0, 'All application requests must remain local')
  assert(wasmRequests.some(url => url.includes('mozjpeg_enc')))
  assert(wasmRequests.some(url => url.includes('squoosh_resize_bg')))
  await page.screenshot({ path: 'test-screenshot.png', fullPage: true })
  const images = await page.evaluate(() => window.exportSpikeImages())
  await writeFile('test-input-exif6.jpg', Buffer.from(images.input, 'base64'))
  await writeFile('test-output-exif6.jpg', Buffer.from(images.output, 'base64'))

  // A new context has no WASM cache. Abort real .wasm requests to test recovery.
  const fallbackContext = await browser.newContext()
  await fallbackContext.route('**/*', route => {
    const url = route.request().url()
    if (new URL(url).origin !== origin || url.includes('.wasm')) return route.abort()
    return route.continue()
  })
  const fallbackPage = await fallbackContext.newPage()
  await fallbackPage.goto(origin)
  await fallbackPage.waitForFunction(() => typeof window.runSpike === 'function')
  const fallback = await fallbackPage.evaluate(() => window.runSpike())
  assert.equal(fallback.passed, true)
  assert(fallback.orientations.every(item => item.engine === 'canvas-fallback'))
  assert.equal(fallback.large.engine, 'canvas-fallback')
  await fallbackPage.screenshot({ path: 'test-fallback-screenshot.png', fullPage: true })
  assert.equal(errors.length, 0)
  const result = { primary, fallback, externalRequests, wasmRequests: [...new Set(wasmRequests)], errors }
  await writeFile('test-results.json', JSON.stringify(result, null, 2))
  console.log(JSON.stringify(result, null, 2))
} finally { await browser.close(); server.close() }
