import assert from 'node:assert/strict'
import path from 'node:path'
import { mkdir, writeFile, unlink, rm } from 'node:fs/promises'
import MVT from 'ol/format/MVT.js'
import { createMvtPmtilesFixture } from './pmtiles-mvt-test-fixture.mjs'

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
      await editor.getByLabel('加载深度', { exact: true }).click()
      await page.getByRole('option', { name: '2 · 高清 +2 级', exact: true }).click()
      await editor.getByRole('checkbox', { name: '强制更新缓存', exact: true }).check()
      const saved = response('/map/settings/' + first.id, 'PUT')
      await editor.getByRole('button', { name: '保存', exact: true }).click(); await saved
      await page.getByRole('button', { name: '自定义卫星底图操作', exact: true }).waitFor()
      const configured = (await api('/map/settings')).providers[0]
      assert.equal(configured.folder, 'custom-satellite'); assert.equal(configured.apiKey, 'test-service-key'); assert.equal(configured.mapKey, 'test-map-key'); assert.equal(configured.forceRefresh, true)
      assert.equal(configured.preload, 2)
      assert.equal((await api('/map/layers')).find(layer => layer.id === configured.id).preload, 2)
      await page.getByTestId('map-layer-new').click()
      const add = page.getByRole('dialog', { name: '新增图层', exact: true })
      await add.getByLabel('名称', { exact: true }).fill('地名标注层')
      await add.getByLabel('缓存目录', { exact: true }).fill('custom-labels')
      await add.getByLabel('图层格式', { exact: true }).click()
      await page.getByRole('option', { name: 'MVT 矢量地名（PBF）', exact: true }).click()
      const created = response('/map/settings', 'POST')
      await add.getByRole('button', { name: '保存', exact: true }).click()
      const extra = await (await created).json()
      assert.equal(extra.scheme, 'xyz'); assert.equal(extra.format, 'mvt')
      await page.getByRole('button', { name: '上移地名标注层', exact: true }).waitFor()
      const reordered = response('/map/settings/order', 'PUT')
      await page.getByRole('button', { name: '上移地名标注层', exact: true }).click(); await reordered
      await page.waitForFunction(() =>
        document.querySelector('[data-testid="map-layer-row"] .row-body')?.textContent?.includes('地名标注层'))
      const toggled = response('/map/settings/' + extra.id + '/enabled', 'PUT')
      await page.getByRole('checkbox', { name: '启用地名标注层', exact: true }).click(); await toggled
      await page.getByRole('checkbox', { name: '启用地名标注层', exact: true }).waitFor()
      assert.deepEqual((await api('/map/layers')).map(layer => layer.id), [first.id])
      await page.getByTestId('map-layer-new').click()
      const archiveEditor = page.getByRole('dialog', { name: '新增图层', exact: true })
      await archiveEditor.getByLabel('名称', { exact: true }).fill('离线单文件地名')
      await archiveEditor.getByLabel('图层格式', { exact: true }).click()
      await page.getByRole('option', { name: 'PMTiles 单文件（MVT / 图片）', exact: true }).click()
      await archiveEditor.getByLabel('本地 PMTiles 文件路径', { exact: true }).fill('labels.pmtiles')
      await archiveEditor.getByLabel('加载深度', { exact: true }).click()
      await page.getByRole('option', { name: '3 · 高清 +3 级', exact: true }).click()
      const archiveCreated = response('/map/settings', 'POST')
      await archiveEditor.getByRole('button', { name: '保存', exact: true }).click()
      const archiveLayer = await (await archiveCreated).json()
      assert.equal(archiveLayer.format, 'pmtiles')
      assert.equal(archiveLayer.scheme, 'xyz')
      assert.equal(archiveLayer.folder, 'labels.pmtiles')
      assert.equal(archiveLayer.preload, 3)
      assert((await api('/map/layers')).some(layer => layer.id === archiveLayer.id && layer.kind === 'pmtiles' && layer.preload === 3))
      await page.getByRole('button', { name: '检查离线单文件地名图层可用性', exact: true }).click()
      await page.getByText('尚未找到本地 PMTiles 文件。可将归档复制到设置的路径。', { exact: true }).waitFor()
      const archiveDisabled = response('/map/settings/' + archiveLayer.id + '/enabled', 'PUT')
      await page.getByRole('checkbox', { name: '启用离线单文件地名', exact: true }).click()
      await archiveDisabled
      await page.screenshot({ path: path.join(evidence, 'map-management-375.png'), fullPage: true, animations: 'disabled' })
      await go('/settings?tab=maps')
      const reopened = await editLayer('自定义卫星底图')
      assert.equal(await reopened.getByLabel('缓存目录', { exact: true }).inputValue(), 'custom-satellite')
      assert.equal(await reopened.getByRole('checkbox', { name: '强制更新缓存', exact: true }).isChecked(), true)
      assert((await reopened.getByLabel('加载深度', { exact: true }).textContent()).includes('2 · 高清 +2 级'))
      await page.screenshot({ path: path.join(evidence, 'map-layer-editor-375.png'), fullPage: true, animations: 'disabled' })
      await page.keyboard.press('Escape')
    })

    await test('M06 depth 2 renders higher zoom tiles instead of only lower zoom preload', async () => {
      const settings = await api('/map/settings')
      const raster = settings.providers.find(layer => layer.enabled && layer.format === 'raster' && layer.preload === 2)
      assert(raster, 'M01 should have configured an enabled raster layer at depth 2')
      await page.evaluate(() => localStorage.setItem('street-eco.last-map-view.v1',
        JSON.stringify({ longitude: 88.893426, latitude: 29.274331, zoom: 16 })))
      const finer = page.waitForRequest(request =>
        new URL(request.url()).pathname.includes('/api/map/tiles/' + raster.id + '/18/'),
      { timeout: 15000 })
      await go('/points?view=map&q=' + encodeURIComponent('depth-regression-no-matching-points'))
      await finer
    })
    await test('M05 real offline PMTiles MVT uses local style directory for geometry', async () => {
      const { archive, tile } = createMvtPmtilesFixture()
      assert.equal(new MVT().readFeatures(tile).length, 1, 'Fixture should decode as a real MVT/PBF tile')
      const config = await api('/map/settings')
      const existingEnabled = config.providers.filter(layer => layer.enabled)
      const fixturePath = path.join(config.cacheRoot, 'mvt-render-fixture.pmtiles')
      const styleDirectory = path.join(config.cacheRoot, 'mvt-style-fixture')
      let layer
      await mkdir(styleDirectory, { recursive: true })
      await writeFile(fixturePath, archive)
      await writeFile(path.join(styleDirectory, 'style.json'), JSON.stringify({
        version: 8,
        sources: { fixture: { type: 'vector', tiles: ['/tiles/{z}/{x}/{y}.pbf'], minzoom: 0, maxzoom: 0 } },
        layers: [{ id: 'water', type: 'fill', source: 'fixture', 'source-layer': 'water', paint: { 'fill-color': '#1982a5' } }],
      }))
      try {
        for (const existing of existingEnabled) {
          await api('/map/settings/' + existing.id + '/enabled', { method: 'PUT', body: { enabled: false } })
        }
        layer = await api('/map/settings', { method: 'POST', body: {
          ...config.providers[0], id: '', name: '矢量渲染回归',
          folder: 'mvt-render-fixture.pmtiles', scheme: 'xyz', format: 'pmtiles',
          urlTemplate: '', forceRefresh: false, enabled: true, preload: 1,
          styleDirectory: 'mvt-style-fixture',
        } })
        assert.equal(layer.format, 'pmtiles')
        assert.equal(layer.hasStyle, true)
        const matching = (await api('/map/layers')).find(item => item.id === layer.id)
        assert.equal(matching.styleUrl, '/api/map/style/' + layer.id + '/style.json')
        const renderedStyle = await api('/map/style/' + layer.id + '/style.json')
        assert.equal(renderedStyle.sources.fixture.tiles[0], 'hbtiles://' + layer.id + '/{z}/{x}/{y}')
        const checked = await api('/map/settings/' + layer.id + '/check')
        assert.equal(checked.status, 'cached')
        assert(checked.message.includes('MVT/PBF 矢量'))
        const fetched = page.waitForResponse(response =>
          new URL(response.url()).pathname.endsWith('/api/map/archive/' + layer.id) && response.status() === 206)
        await page.setViewportSize({ width: 1440, height: 900 })
        await go('/points?view=map')
        await fetched
        await page.locator('.simple-map-canvas .maplibregl-canvas').waitFor({ timeout: 12000 })
        await page.waitForFunction(() => {
          const canvas = document.querySelector('.simple-map-canvas .maplibregl-canvas')
          return canvas && canvas.width > 50 && canvas.height > 50
        }, null, { timeout: 12000 })
      } finally {
        if (layer) await api('/map/settings/' + layer.id + '?confirm=true', { method: 'DELETE' })
        for (const existing of existingEnabled)
          await api('/map/settings/' + existing.id + '/enabled', { method: 'PUT', body: { enabled: true } })
        await unlink(fixturePath).catch(() => {})
        await rm(styleDirectory, { recursive: true, force: true })
      }
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
    await test('M03 clicking map center returns GCJ-02 coordinates instead of WGS84', async () => {
      await page.setViewportSize({ width: 1440, height: 900 })
      await go('/points')
      await page.getByTestId('point-create').click()
      const editor = page.getByRole('dialog', { name: '新增点位', exact: true })
      if (await editor.getByRole('button', { name: '编辑位置', exact: true }).count())
        await editor.getByRole('button', { name: '清除', exact: true }).click()
      await editor.getByRole('button', { name: '标记位置', exact: true }).click()
      const picker = page.getByRole('dialog', { name: '标记位置', exact: true })
      await picker.getByRole('button', { name: '选点', exact: true }).click()
      const canvas = picker.locator('.simple-map-canvas')
      await canvas.locator('.ol-viewport').waitFor()
      const box = await canvas.boundingBox()
      assert(box && box.width > 100 && box.height > 100, 'OpenLayers map should be visible')
      const persisted = await page.evaluate(() => {
        const last = JSON.parse(localStorage.getItem('street-eco.last-map-view.v1') || 'null')
        return [last?.longitude ?? 88.893426, last?.latitude ?? 29.274331]
      })
      await page.mouse.click(box.x + box.width / 2, box.y + box.height / 2)
      await picker.getByRole('button', { name: '确定', exact: true }).click()
      await editor.getByText('手动填写经纬度', { exact: true }).click()
      const value = await editor.getByLabel('经纬度', { exact: true }).inputValue()
      const [longitude, latitude] = value.split(',').map(Number)
      assert(Math.abs(longitude - persisted[0]) < 0.0001 && Math.abs(latitude - persisted[1]) < 0.0001,
        `Expected GCJ-02 center near ${persisted}, got ${value}`)
      await editor.getByRole('button', { name: '取消', exact: true }).click()
    })
    await test('M04 Ctrl-drag rotates the map and the north button resets bearing', async () => {
      await page.setViewportSize({ width: 1440, height: 900 })
      await go('/points')
      await page.getByTestId('point-create').click()
      const editor = page.getByRole('dialog', { name: '新增点位', exact: true })
      await editor.getByRole('button', { name: /^(标记位置|编辑位置)$/ }).click()
      const picker = page.getByRole('dialog', { name: '标记位置', exact: true })
      const canvas = picker.locator('.simple-map-canvas')
      await canvas.locator('.ol-viewport').waitFor()
      const box = await canvas.boundingBox()
      assert(box && box.width > 100 && box.height > 100)
      const x = box.x + box.width * 0.75
      await page.mouse.move(x, box.y + box.height * 0.42)
      await page.keyboard.down('Control')
      await page.mouse.down()
      await page.mouse.move(x, box.y + box.height * 0.70, { steps: 12 })
      await page.mouse.up()
      await page.keyboard.up('Control')
      const north = picker.getByRole('button', { name: '恢复正北方向' })
      await north.waitFor({ timeout: 5000 })
      await north.click()
      await north.waitFor({ state: 'hidden', timeout: 5000 })
      await picker.getByRole('button', { name: '取消', exact: true }).click()
      await editor.getByRole('button', { name: '取消', exact: true }).click()
    })
    assert.deepEqual(errors, [])
  } catch (error) {
    await page.screenshot({ path: path.join(evidence, 'map-feature-failure.png'), fullPage: true }).catch(() => {})
    throw error
  } finally { await context.close() }
}
