import assert from 'node:assert/strict'
import path from 'node:path'

export async function verifyMapManagement({ browser, origin, api, test, bootstrap, evidence }) {
  const context = await browser.newContext({ viewport: { width: 375, height: 812 }, locale: 'zh-CN', reducedMotion: 'no-preference' })
  const page = await context.newPage(), errors = []
  page.on('pageerror', error => errors.push(String(error)))
  await page.addInitScript(() => {
    window.mapAnimations = []
    const animate = Element.prototype.animate
    Element.prototype.animate = function (frames, options) {
      if (this.classList.contains('record-map-surface')) window.mapAnimations.push(typeof options === 'number' ? options : options.duration)
      return animate.call(this, frames, options)
    }
  })
  await page.route('**/api/map/tiles/**', route => route.fulfill({ status: 200, contentType: 'image/png', body: Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+j7VQAAAAASUVORK5CYII=', 'base64') }))
  const go = route => page.goto(origin + '/#' + route, { waitUntil: 'networkidle' })
  const response = (endpoint, method = 'GET') => page.waitForResponse(r => new URL(r.url()).pathname === '/api' + endpoint && r.request().method() === method && r.status() < 400)
  async function editLayer(name) {
    await page.getByRole('button', { name: name + '操作', exact: true }).click()
    await page.getByRole('menuitem', { name: '编辑图层', exact: true }).click()
    return page.getByRole('dialog', { name: '编辑图层', exact: true })
  }
  async function waitFullscreen(value) {
    await page.waitForFunction(value => {
      const button = document.querySelector('[data-testid="map-fullscreen"]')
      return button && button.getAttribute('aria-pressed') === String(value) && !button.disabled
    }, value)
  }
  try {
    await test('M01 map layers can be configured, added, reordered and disabled in settings', async () => {
      const original = await api('/map/settings'); assert.equal(original.providers.length, 1)
      const first = original.providers[0]
      await go('/settings?tab=maps')
      await page.getByTestId('map-layer-row').first().waitFor()
      const editor = await editLayer(first.name)
      await editor.getByLabel('名称', { exact: true }).fill('自定义卫星底图')
      await editor.getByLabel('调用链接', { exact: true }).fill('http://127.0.0.1:9/{z}/{x}/{y}?key={key}&mk={mk}')
      await editor.getByLabel('服务密钥（key）', { exact: true }).fill('test-service-key')
      await editor.getByLabel('地图密钥（mk）', { exact: true }).fill('test-map-key')
      await editor.getByLabel('缓存目录', { exact: true }).fill('custom-satellite')
      await editor.getByRole('checkbox', { name: '强制更新缓存', exact: true }).check()
      const saved = response('/map/settings/' + first.id, 'PUT')
      await editor.getByRole('button', { name: '保存', exact: true }).click(); await saved
      await page.getByRole('button', { name: '自定义卫星底图操作', exact: true }).waitFor()
      const configured = (await api('/map/settings')).providers[0]
      assert.equal(configured.folder, 'custom-satellite'); assert.equal(configured.apiKey, 'test-service-key'); assert.equal(configured.mapKey, 'test-map-key'); assert.equal(configured.forceRefresh, true)
      await page.getByTestId('map-layer-new').click()
      const add = page.getByRole('dialog', { name: '新增图层', exact: true })
      await add.getByLabel('名称', { exact: true }).fill('地名标注层')
      await add.getByLabel('缓存目录', { exact: true }).fill('custom-labels')
      const created = response('/map/settings', 'POST')
      await add.getByRole('button', { name: '保存', exact: true }).click()
      const extra = await (await created).json()
      await page.getByRole('button', { name: '上移地名标注层', exact: true }).waitFor()
      const reordered = response('/map/settings/order', 'PUT')
      await page.getByRole('button', { name: '上移地名标注层', exact: true }).click(); await reordered
      assert((await page.getByTestId('map-layer-row').first().textContent()).includes('地名标注层'))
      const toggled = response('/map/settings/' + extra.id + '/enabled', 'PUT')
      await page.getByRole('checkbox', { name: '启用地名标注层', exact: true }).uncheck(); await toggled
      assert.deepEqual((await api('/map/layers')).map(layer => layer.id), [first.id])
      await page.screenshot({ path: path.join(evidence, 'map-management-375.png'), fullPage: true, animations: 'disabled' })
      await go('/settings?tab=maps')
      const reopened = await editLayer('自定义卫星底图')
      assert.equal(await reopened.getByLabel('缓存目录', { exact: true }).inputValue(), 'custom-satellite')
      assert.equal(await reopened.getByRole('checkbox', { name: '强制更新缓存', exact: true }).isChecked(), true)
      await page.screenshot({ path: path.join(evidence, 'map-layer-editor-375.png'), fullPage: true, animations: 'disabled' })
      await page.keyboard.press('Escape')
    })

    await test('M02 background map fullscreen preserves controls and animates both directions', async () => {
      for (let i = 0; i < 2; i++) await api('/points', { method: 'POST', body: { name: '全屏回归点位' + i, communityId: bootstrap.communities[i % bootstrap.communities.length].id, typeId: bootstrap.pointTypes[0].id, address: '全屏回归地址', frequency: 'irregular', longitude: 88.9 + i / 100, latitude: 29.3 } })
      await page.setViewportSize({ width: 1440, height: 900 })
      await go('/points?view=map&q=' + encodeURIComponent('全屏回归点位'))
      await page.getByTestId('map-fullscreen').waitFor()
      const filterBox = await page.getByTestId('list-filters').boundingBox()
      const header = await page.locator('.page-header').boundingBox()
      const canvas = await page.locator('.simple-map-canvas').elementHandle()
      assert.equal(await page.locator('.ol-attribution').count(), 0)
      await page.getByTestId('map-fullscreen').click(); await waitFullscreen(true)
      const bounds = await page.locator('#main-map-layer .record-map-surface').boundingBox()
      const main = await page.locator('.main-shell').boundingBox()
      assert(bounds && main && Math.abs(bounds.x - main.x) < 1 && Math.abs(bounds.width - main.width) < 1 && bounds.y === 0 && bounds.height === 900)
      assert.deepEqual(await page.getByTestId('list-filters').boundingBox(), filterBox)
      assert.deepEqual(await page.locator('.page-header').boundingBox(), header)
      assert(await canvas.evaluate(element => document.getElementById('main-map-layer').contains(element)))
      await page.getByLabel('社区筛选', { exact: true }).click()
      const changed = response('/points')
      await page.getByRole('option', { name: bootstrap.communities[0].name, exact: true }).click(); await changed
      await page.waitForFunction(() => !document.querySelector('.record-map-shell')?.getAttribute('aria-busy') || document.querySelector('.record-map-shell')?.getAttribute('aria-busy') === 'false')
      assert.equal(await page.getByTestId('map-fullscreen').getAttribute('aria-pressed'), 'true')
      assert(await canvas.evaluate(element => element.isConnected && document.getElementById('main-map-layer').contains(element)))
      await page.screenshot({ path: path.join(evidence, 'map-fullscreen-1440.png'), fullPage: true, animations: 'disabled' })
      await page.getByTestId('map-fullscreen').click(); await waitFullscreen(false)
      assert.equal(await page.locator('#main-map-layer .record-map-surface').count(), 0)
      assert(await canvas.evaluate(element => element.isConnected && document.querySelector('.record-map-shell').contains(element)))
      const durations = await page.evaluate(() => window.mapAnimations)
      assert.equal(durations.length, 2); assert(durations.every(duration => duration > 0))
      await page.setViewportSize({ width: 375, height: 812 })
      await page.getByTestId('map-fullscreen').click(); await waitFullscreen(true)
      const navigation = await page.getByRole('navigation', { name: '底部主导航' }).boundingBox()
      assert(await page.evaluate(({ x, y }) => !!document.elementFromPoint(x, y)?.closest('.mobile-tabs'), { x: navigation.x + 20, y: navigation.y + 20 }))
      await page.screenshot({ path: path.join(evidence, 'map-fullscreen-375.png'), fullPage: true, animations: 'disabled' })
      await page.keyboard.press('Escape'); await waitFullscreen(false)
    })
    assert.deepEqual(errors, [])
  } catch (error) {
    await page.screenshot({ path: path.join(evidence, 'map-feature-failure.png'), fullPage: true }).catch(() => {})
    throw error
  } finally { await context.close() }
}
