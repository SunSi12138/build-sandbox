import assert from 'node:assert/strict'
import { expect } from 'playwright/test'

// Every mutation reaches the real isolated application. Routes only hold a real
// response or deliberately exercise a network failure; storage assertions use HTTP.
export async function verifyOptimization({ browser, origin, directOrigin, api, test, report, bootstrap, fixtureJpeg }) {
  const context = await browser.newContext({ viewport: { width: 375, height: 812 }, locale: 'zh-CN', reducedMotion: 'reduce' })
  const errors = []
  await context.route('**/*', async route => {
    const url = route.request().url()
    if (/^https?:/.test(url) && new URL(url).origin !== origin) {
      report.externalRequests.push(url)
      await route.abort('blockedbyclient')
    } else await route.continue()
  })
  const page = await context.newPage()
  page.on('pageerror', error => errors.push(String(error)))
  const point = await api('/points', { method: 'POST', body: { name: '优化回归点位', communityId: bootstrap.communities[0].id, typeId: bootstrap.pointTypes[0].id, address: '原地址', frequency: 'weekly' } })
  const goto = async route => page.goto(origin + '/#' + route, { waitUntil: 'networkidle' })
  const responseFor = (path, method) => page.waitForResponse(response => new URL(response.url()).pathname === '/api' + path && response.request().method() === method)
  const serverPhotoStatus = async id => (await fetch(directOrigin + '/photos/' + id)).status
  const draft = async () => page.evaluate(() => JSON.parse(localStorage.getItem('street-eco.inspection-draft.v2')))
  const choose = async (locator, name) => { await locator.click(); await page.getByRole('option', { name, exact: true }).click() }
  async function beginInspection() {
    await goto('/points')
    await page.evaluate(() => localStorage.removeItem('street-eco.inspection-draft.v2'))
    await goto('/inspections/new')
    await page.getByTestId('inspection-point-search').fill(point.name)
    await page.getByTestId('inspection-point-option').filter({ hasText: point.name }).click()
    await page.getByTestId('inspection-inspector').fill('回归人员')
  }
  function pauseThumbnail() {
    let resolveStarted, release
    const started = new Promise(resolve => { resolveStarted = resolve })
    const gate = new Promise(resolve => { release = resolve })
    const handler = async route => {
      const url = new URL(route.request().url())
      const upstream = await route.fetch({ url: directOrigin + url.pathname + url.search })
      resolveStarted()
      await gate
      await route.fulfill({ response: upstream }).catch(() => {})
    }
    return { started, release: () => release(), handler }
  }
  async function uploadPaused(control, paused, name = 'race.jpg') {
    await page.route('**/api/photos/*/thumb', paused.handler)
    const uploaded = responseFor('/photos', 'PUT')
    await control.setInputFiles({ name, mimeType: 'image/jpeg', buffer: fixtureJpeg })
    const photo = await (await uploaded).json()
    await paused.started
    return photo
  }
  async function removePending() {
    await page.getByRole('button', { name: '移除未上传照片', exact: true }).first().click()
    await page.getByRole('dialog', { name: '删除照片？', exact: true }).getByRole('button', { name: '删除', exact: true }).click()
  }
  try {
    await goto('/')
    await test('F01 narrow point photo and disabled writes preserve concurrent metadata', async () => {
      await goto('/points/' + point.id)
      await expect(page.getByTestId('photo-file-input')).toHaveCount(0)
      const upload = await fetch(directOrigin + '/api/photos?phase=point', { method: 'PUT', headers: { 'Content-Type': 'image/jpeg' }, body: fixtureJpeg })
      assert.equal(upload.status, 201)
      const photo = await upload.json()
      const newer = await api('/points/' + point.id, { method: 'PUT', body: { ...point, name: '另一窗口的新名称', address: '新地址', contact: '新联系人', phone: '新电话', notes: '新备注', disabled: true, photoIds: [] } })
      await api('/points/' + point.id + '/photos', { method: 'PUT', body: { photoIds: [photo.id] } })
      const saved = (await api('/points/' + point.id)).point
      for (const key of ['name', 'address', 'contact', 'phone', 'notes', 'disabled']) assert.equal(saved[key], newer[key])
      assert.equal(saved.photoCount, 1)
      await api('/points/' + point.id + '/status', { method: 'PUT', body: { disabled: false } })
      assert.equal((await api('/points/' + point.id)).point.name, newer.name)
      await api('/points/' + point.id, { method: 'PUT', body: { ...point, photoIds: [photo.id] } })
    })

    await test('F02 deleting during a real thumbnail response never resurrects a photo ID', async () => {
      await beginInspection()
      const paused = pauseThumbnail()
      const photo = await uploadPaused(page.getByTestId('photo-file-input').first(), paused)
      // Use a path-only waiter because the confirmation flag is a query argument.
      const actualDelete = responseFor('/photos/' + photo.id, 'DELETE')
      await removePending()
      assert.equal((await actualDelete).status(), 204)
      paused.release()
      await page.unroute('**/api/photos/*/thumb', paused.handler)
      await expect(page.getByTestId('photo-pending')).toHaveCount(0)
      await expect(page.getByTestId('photo-uploaded')).toHaveCount(0)
      assert.equal(await serverPhotoStatus(photo.id), 404)
      assert(!JSON.stringify(await draft()).includes(photo.id))
      const saved = responseFor('/inspections', 'POST')
      await page.getByTestId('inspection-save').click()
      const record = await (await saved).json()
      assert.equal(record.photos.length, 0)
      assert.equal(record.inspection.result, 'normal')
      await page.waitForURL('**/#/inspections/' + record.inspection.id)
    })

    await test('F02 failed deletion retains a retryable ID without re-emitting it', async () => {
      await beginInspection()
      const paused = pauseThumbnail()
      const photo = await uploadPaused(page.getByTestId('photo-file-input').first(), paused, 'retry-delete.jpg')
      const url = '**/api/photos/' + photo.id + '?confirm=true'
      await page.route(url, route => route.fulfill({ status: 503, contentType: 'application/json', body: JSON.stringify({ message: '测试删除失败' }) }))
      await removePending()
      await expect(page.getByRole('dialog', { name: '删除照片？', exact: true })).toContainText('测试删除失败')
      paused.release()
      await page.unroute('**/api/photos/*/thumb', paused.handler)
      await expect(page.getByTestId('photo-pending')).toHaveCount(1)
      await expect(page.getByTestId('photo-uploaded')).toHaveCount(0)
      assert.equal(await serverPhotoStatus(photo.id), 200)
      assert(!JSON.stringify(await draft()).includes(photo.id))
      await page.unroute(url)
      const deleted = responseFor('/photos/' + photo.id, 'DELETE')
      await page.getByRole('dialog', { name: '删除照片？', exact: true }).getByRole('button', { name: '删除', exact: true }).click()
      assert.equal((await deleted).status(), 204)
      await expect(page.getByTestId('photo-pending')).toHaveCount(0)
    })

    await test('F02 unmount aborts pending thumbnail and preserves no stale draft reference', async () => {
      const paused = pauseThumbnail()
      const photo = await uploadPaused(page.getByTestId('photo-file-input').first(), paused, 'unmount.jpg')
      await page.evaluate(() => { location.hash = '/points' })
      const leave = page.getByRole('dialog', { name: '照片尚未上传完成', exact: true })
      await leave.getByRole('button', { name: '离开', exact: true }).click()
      await page.waitForURL('**/#/points')
      // Hash changes before the route guard resolves. Wait for actual navigation
      // and unmount; releasing the response on hash alone races acceptance.
      await expect(page.getByRole('heading', { name: '点位管理', exact: true })).toBeVisible()
      await expect(page.getByTestId('inspection-save')).toHaveCount(0)
      paused.release()
      await page.unroute('**/api/photos/*/thumb', paused.handler)
      await expect.poll(() => serverPhotoStatus(photo.id), { timeout: 10000 }).toBe(404)
      assert(!JSON.stringify(await draft()).includes(photo.id))
    })

    await test('F04 all dirty batch close paths preserve edits unless explicitly discarded', async () => {
      const record = await api('/inspections', { method: 'POST', body: { pointId: point.id, date: bootstrap.today, inspector: '关闭回归', result: 'issues', issues: [{ description: '问题甲', deadline: bootstrap.today }, { description: '问题乙', deadline: bootstrap.today }] } })
      await goto('/inspections/' + record.inspection.id)
      const feedback = () => page.getByRole('dialog', { name: '记录反馈', exact: true })
      await page.getByTestId('batch-feedback').click()
      await feedback().getByRole('button', { name: '取消', exact: true }).click()
      await expect(feedback()).toBeHidden()
      await expect(page.getByRole('dialog', { name: '放弃未提交内容？', exact: true })).toBeHidden()
      await page.getByTestId('batch-feedback').click()
      await feedback().getByTestId('batch-notes-1').fill('保留第一项修改')
      await feedback().getByTestId('batch-notes-2').fill('保留第二项修改')
      for (const method of ['cancel', 'escape', 'close']) {
        if (method === 'cancel') await feedback().getByRole('button', { name: '取消', exact: true }).click()
        else if (method === 'escape') await page.keyboard.press('Escape')
        else await feedback().locator('[data-slot="close"]').click()
        const discard = page.getByRole('dialog', { name: '放弃未提交内容？', exact: true })
        await expect(discard).toBeVisible()
        await discard.getByRole('button', { name: '继续编辑', exact: true }).click()
        await expect(feedback().getByTestId('batch-notes-1')).toHaveValue('保留第一项修改')
        await expect(feedback().getByTestId('batch-notes-2')).toHaveValue('保留第二项修改')
      }
      await feedback().getByRole('button', { name: '取消', exact: true }).click()
      await page.getByRole('dialog', { name: '放弃未提交内容？', exact: true }).getByRole('button', { name: '放弃', exact: true }).click()
      await expect(feedback()).toBeHidden()
      await page.getByTestId('batch-feedback').click()
      await expect(feedback().getByTestId('batch-notes-1')).toHaveValue('')
      await feedback().getByTestId('batch-notes-1').fill('提交时保留')
      await feedback().getByTestId('batch-notes-2').fill('第二项')
      let entered
      const ready = new Promise(resolve => { entered = resolve })
      await page.route('**/api/inspections/' + record.inspection.id + '/feedback', async route => {
        entered()
        await new Promise(resolve => setTimeout(resolve, 2000))
        await route.fulfill({ status: 503, contentType: 'application/json', body: JSON.stringify({ message: '测试保存失败' }) })
      })
      await feedback().getByTestId('batch-save').click(); await ready
      await expect(feedback().getByTestId('batch-date')).toBeDisabled()
      await expect(feedback().getByTestId('batch-notes-1')).toBeDisabled()
      await expect(feedback().getByRole('button', { name: '取消', exact: true })).toBeDisabled()
      await expect(feedback()).toContainText('测试保存失败')
      await expect(feedback().getByTestId('batch-notes-1')).toBeEnabled()
      await expect(feedback().getByTestId('batch-notes-1')).toHaveValue('提交时保留')
      await feedback().getByRole('button', { name: '取消', exact: true }).click()
      await page.getByRole('dialog', { name: '放弃未提交内容？', exact: true }).getByRole('button', { name: '放弃', exact: true }).click()
    })

    await test('F03 campaign loading and slow-save locks cover every editable control', async () => {
      await goto('/campaigns/new')
      await page.getByTestId('campaign-name').fill('慢请求整治')
      await choose(page.getByTestId('campaign-type'), bootstrap.campaignTypes[0].name)
      await page.getByTestId('campaign-point-search').fill(point.name)
      await page.getByTestId('campaign-point-option').filter({ hasText: point.name }).click()
      await page.getByTestId('campaign-content').fill('请妥善保管，让每一项现场记录按原文上传。')
      let entered
      const ready = new Promise(resolve => { entered = resolve })
      await page.route('**/api/campaigns', async route => {
        if (route.request().method() !== 'POST') return route.continue()
        entered()
        await new Promise(resolve => setTimeout(resolve, 2000))
        await route.fulfill({ status: 503, contentType: 'application/json', body: JSON.stringify({ message: '测试保存失败' }) })
      })
      await page.getByTestId('campaign-save').click(); await ready
      const fields = await page.locator('main form input, main form textarea, main form button[role="combobox"]').evaluateAll(elements => elements.map(element => ({ type: element.type, disabled: element.disabled })))
      assert(fields.length >= 10)
      assert(fields.every(field => field.disabled), JSON.stringify(fields))
      await page.evaluate(() => { location.hash = '/campaigns' })
      await page.waitForURL('**/#/campaigns/new')
      await expect(page.getByRole('alert').filter({ hasText: '测试保存失败' })).toBeVisible()
      await expect(page.getByTestId('campaign-name')).toBeEnabled()
      await expect(page.getByTestId('campaign-name')).toHaveValue('慢请求整治')
      assert((await page.evaluate(() => localStorage.getItem('street-eco-campaign-draft-v3')))?.includes('慢请求整治'))
    })
    await test('M01 measure 50-issue draft input without weakening persistence', async () => {
      await beginInspection()
      await page.getByTestId('inspection-result-issues').click()
      await expect(page.getByTestId('inspection-issue-description')).toHaveCount(1)
      const pictures = []
      for (let i = 0; i < 2; i++) {
        const response = await fetch(directOrigin + '/api/photos?phase=site', { method: 'PUT', headers: { 'Content-Type': 'image/jpeg' }, body: fixtureJpeg })
        assert.equal(response.status, 201)
        pictures.push(await response.json())
      }
      const envelope = await draft()
      assert.equal(envelope.version, 2)
      assert.equal(envelope.draft.result, 'issues')
      // Leave the live editor before seeding a large persisted fixture. Its
      // beforeunload/pagehide handlers correctly flush its one in-memory issue;
      // editing localStorage in that live editor and then reloading would let
      // that flush overwrite the fixture, not test restoration of 50 issues.
      await page.getByRole('link', { name: '返回', exact: true }).first().click()
      await expect(page.getByTestId('inspection-save')).toHaveCount(0)
      await page.evaluate(({ envelope, pictures, today }) => {
        envelope.draft.issues = Array.from({ length: 50 }, (_, i) => ({ key: i + 1, description: '性能观察问题' + i + '现场检查。'.repeat(100), requirement: '保留最后输入。', deadline: today, responsibleUnit: '测试社区', photos: i < 2 ? [pictures[i]] : [] }))
        localStorage.setItem('street-eco.inspection-draft.v2', JSON.stringify(envelope))
      }, { envelope, pictures, today: bootstrap.today })
      await goto('/inspections/new')
      await expect(page.getByTestId('inspection-issue-description')).toHaveCount(50)
      // Now a real reload flushes and restores all 50 issues and their photos.
      await page.reload({ waitUntil: 'networkidle' })
      await expect(page.getByTestId('inspection-issue-description')).toHaveCount(50)
      const measurement = await page.evaluate(async () => {
        const original = Storage.prototype.setItem
        const writes = []
        Storage.prototype.setItem = function (key, value) {
          const start = performance.now()
          const result = original.call(this, key, value)
          if (key === 'street-eco.inspection-draft.v2') writes.push({ milliseconds: performance.now() - start, characters: value.length })
          return result
        }
        const input = document.querySelectorAll('[data-testid="inspection-issue-description"]')[49]
        const timings = []
        try {
          for (let i = 0; i < 20; i++) {
            const start = performance.now()
            input.value = '最后一条输入-' + i + '甲'.repeat(1000)
            input.dispatchEvent(new Event('input', { bubbles: true }))
            await new Promise(resolve => requestAnimationFrame(resolve))
            timings.push(performance.now() - start)
          }
        } finally { Storage.prototype.setItem = original }
        return { issueCount: 50, inputEvents: 20, inputToNextFrameMs: timings, storageWrites: writes, note: 'CI desktop Chromium with mobile viewport, not real-phone latency; serialization is included in input-to-frame, not setItem-only timing.' }
      })
      report.draftMeasurement = measurement
      await page.getByRole('link', { name: '返回', exact: true }).first().click()
      await goto('/inspections/new')
      await expect(page.getByTestId('inspection-issue-description')).toHaveCount(50)
      await expect(page.getByTestId('inspection-issue-description').last()).toHaveValue('最后一条输入-19' + '甲'.repeat(1000))
      const retained = await draft()
      assert.equal(retained.draft.issues[0].photos[0].id, pictures[0].id)
      assert.equal(retained.draft.issues[1].photos[0].id, pictures[1].id)
    })

    assert.deepEqual(errors, [], 'Optimization cases caused unhandled browser errors')
  } finally { await context.close() }
}
