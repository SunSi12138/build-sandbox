import assert from 'node:assert/strict'
import path from 'node:path'
import { execFileSync } from 'node:child_process'

export async function verifyConsistency({ browser, origin, directOrigin, api, test, bootstrap, evidence }) {
  const context = await browser.newContext({ viewport: { width: 375, height: 812 }, locale: 'zh-CN', reducedMotion: 'reduce', acceptDownloads: true })
  const page = await context.newPage(), errors = [], consoleErrors = [], expectedValidationUrls = new Set()
  page.on('pageerror', error => errors.push(String(error)))
  page.on('console', message => { if (message.type() === 'error' && !(expectedValidationUrls.has(message.location().url) && message.text().includes('400 (Bad Request)'))) consoleErrors.push(message.text()) })
  const go = route => page.goto(origin + '/#' + route, { waitUntil: 'networkidle' })
  const response = (endpoint, method = 'GET', predicate = () => true) => page.waitForResponse(r => new URL(r.url()).pathname === '/api' + endpoint && r.request().method() === method && predicate(new URL(r.url())))
  const choose = async (control, label) => { await control.click(); await page.getByRole('option', { name: label, exact: true }).click() }
  async function renderedMarker(color) {
    const handle = await page.waitForFunction(color => {
      const canvases = [...document.querySelectorAll('.simple-map canvas')]
      for (let index = 0; index < canvases.length; index++) {
        const canvas = canvases[index], context = canvas.getContext('2d')
        if (!context || !canvas.width || !canvas.height) continue
        const pixels = context.getImageData(0, 0, canvas.width, canvas.height).data
        let x = 0, y = 0, count = 0
        for (let i = 0; i < pixels.length; i += 4) if (pixels[i] === color[0] && pixels[i + 1] === color[1] && pixels[i + 2] === color[2] && pixels[i + 3] > 200) {
          x += (i / 4) % canvas.width; y += Math.floor(i / 4 / canvas.width); count++
        }
        if (count > 12) return { index, x: x / count / canvas.width, y: y / count / canvas.height }
      }
      return false
    }, color)
    const marker = await handle.jsonValue(); await handle.dispose()
    return marker
  }
  const generalType = bootstrap.activityTypes.find(type => type.template === 'general')
  const remediationType = bootstrap.activityTypes.find(type => type.template === 'remediation')
  let tag, point, temporaryPoint, matter, activity, activePoint
  try {
    await test('S01 disabled tags filter historical records and all tag overview modules load', async () => {
      tag = await api('/tags', { method: 'POST', body: { name: '扫描回归历史标签' } })
      point = await api('/points', { method: 'POST', body: { name: '扫描标签常态点', communityId: bootstrap.communities[0].id, typeId: bootstrap.pointTypes[0].id, address: '扫描地址', frequency: 'irregular', tagIds: [tag.id], longitude: 88.9, latitude: 29.3 } })
      matter = await api('/matters', { method: 'POST', body: { title: '扫描标签事项', source: 'public', receivedDate: bootstrap.today, content: '核实现场', tagIds: [tag.id] } })
      await api('/inspections', { method: 'POST', body: { pointId: point.id, date: bootstrap.today, inspector: '扫描人员', result: 'normal', tagIds: [tag.id], matterIds: [matter.id] } })
      activity = await api('/activities', { method: 'POST', body: { name: '扫描标签活动', typeId: generalType.id, date: bootstrap.today, content: '开展宣传', pointId: point.id, tagIds: [tag.id], matterIds: [matter.id] } })
      const temporary = await api('/inspections', { method: 'POST', body: { date: bootstrap.today, inspector: '扫描人员', result: 'normal', newPoint: { name: '扫描标签临时点', communityId: bootstrap.communities[0].id, typeId: bootstrap.pointTypes[0].id, address: '临时地址', tagIds: [tag.id] } } })
      temporaryPoint = temporary.inspection.pointId
      await api('/points/' + point.id + '/status', { method: 'PUT', body: { disabled: true } })
      await api('/tags/' + tag.id, { method: 'PUT', body: { name: tag.name, disabled: true } })
      let summaryRequests = 0
      const countSummary = request => { const url = new URL(request.url()); if (url.searchParams.get('tagId') === tag.id && url.searchParams.get('pageSize') === '1') summaryRequests++ }
      page.on('request', countSummary)
      await go('/tags/' + tag.id)
      for (const name of ['事项 1', '点位 2', '排查 1', '活动 1']) await page.getByRole('tab', { name, exact: true }).waitFor()
      await page.getByRole('link', { name: /扫描标签事项/ }).waitFor()
      await page.getByRole('tab', { name: '点位 2', exact: true }).click()
      await page.getByRole('link', { name: /扫描标签临时点/ }).waitFor()
      await page.getByRole('link', { name: /扫描标签常态点/ }).waitFor()
      await page.getByRole('tab', { name: '排查 1', exact: true }).click()
      await page.locator(`a[href*="/inspections/"]`).filter({ hasText: '扫描标签常态点' }).waitFor()
      await page.getByRole('tab', { name: '活动 1', exact: true }).click()
      await page.getByRole('link', { name: /扫描标签活动/ }).waitFor()
      assert.equal(summaryRequests, 4, 'Tab changes should not reload all four totals')
      page.off('request', countSummary)
      await page.screenshot({ path: path.join(evidence, 'scan-tag-overview-375.png'), fullPage: true, animations: 'disabled' })
      await go('/activities')
      await page.getByRole('button', { name: '筛选', exact: true }).click()
      const filtered = response('/activities', 'GET', url => url.searchParams.get('tagId') === tag.id)
      await choose(page.getByLabel('标签筛选', { exact: true }), tag.name + '（已停用）')
      await page.keyboard.press('Escape'); assert.equal((await filtered).status(), 200)
      await page.getByTestId('activity-row').filter({ hasText: activity.activity.name }).waitFor()
      assert.equal(await page.getByTestId('activity-row').count(), 1)
    })

    await test('S02 activity type changes and export scopes keep exactly the same filter', async () => {
      activePoint = await api('/points', { method: 'POST', body: { name: '扫描工作点', communityId: bootstrap.communities[0].id, typeId: bootstrap.pointTypes[0].id, address: '扫描工作地址', frequency: 'irregular' } })
      const ordinary = await api('/activities', { method: 'POST', body: { name: '扫描筛选普通活动', typeId: generalType.id, date: bootstrap.today, content: '普通记录' } })
      const special = await api('/activities', { method: 'POST', body: { name: '扫描筛选整治活动', typeId: remediationType.id, date: bootstrap.today, content: '整治记录', pointId: activePoint.id, remediation: { typeId: bootstrap.remediationTypes[0].id } } })
      await go(`/activities?q=${encodeURIComponent('扫描筛选')}&typeId=${remediationType.id}&remediationTypeId=${bootstrap.remediationTypes[0].id}`)
      await page.getByTestId('activity-row').filter({ hasText: special.activity.name }).waitFor()
      const updated = response('/activities', 'GET', url => url.searchParams.get('typeId') === generalType.id)
      await choose(page.getByLabel('类型筛选', { exact: true }), generalType.name); await updated
      await page.getByTestId('activity-row').filter({ hasText: ordinary.activity.name }).waitFor()
      assert(!page.url().includes('remediationTypeId='))
      assert.equal(await page.getByTestId('activity-row').count(), 1)
      await page.getByRole('button', { name: '导出活动台账', exact: true }).click()
      const dialog = page.getByRole('dialog', { name: '导出活动台账', exact: true })
      await dialog.getByTestId('ledger-preview').filter({ hasText: '预计 1 条记录' }).waitFor()
      await dialog.getByRole('button', { name: '自定义范围', exact: true }).click()
      await dialog.getByTestId('activity-ledger-kind-remediation').click()
      await dialog.getByRole('button', { name: '当前筛选', exact: true }).click()
      await dialog.getByTestId('ledger-preview').filter({ hasText: '预计 1 条记录' }).waitFor()
      assert.equal(await dialog.getByTestId('activity-ledger-kind-activity').getAttribute('aria-pressed'), 'true')
      assert.equal(await dialog.getByTestId('activity-ledger-kind-remediation').isDisabled(), true)
      const exported = response('/exports/activity-ledger', 'GET', url => url.searchParams.get('typeId') === generalType.id && !url.searchParams.has('remediationTypeId'))
      const downloadReady = page.waitForEvent('download')
      await dialog.getByTestId('activity-ledger-export').click()
      assert.equal((await exported).status(), 200)
      const download = await downloadReady, file = path.join(evidence, 'scan-current-activities.docx'); await download.saveAs(file)
      const xml = execFileSync('python3', ['-c', "import sys,zipfile;print(zipfile.ZipFile(sys.argv[1]).read('word/document.xml').decode())", file], { encoding: 'utf8' })
      assert(xml.includes(ordinary.activity.name)); assert(!xml.includes(special.activity.name))
    })

    await test('S03 activity drafts resolve selected issues outside the first search page', async () => {
      const detail = await api('/inspections', { method: 'POST', body: { pointId: activePoint.id, date: bootstrap.today, inspector: '扫描人员', result: 'issues', issues: Array.from({ length: 32 }, (_, i) => ({ description: '扫描分页问题' + String(i).padStart(2, '0'), requirement: '处理问题', deadline: bootstrap.today, issueTypeId: bootstrap.issueTypes[0].id })) } })
      const selected = detail.issues[0]
      const firstPage = await api(`/issues?pointId=${activePoint.id}&pageSize=30`)
      assert(!firstPage.items.some(issue => issue.id === selected.id))
      await go('/activities')
      await page.evaluate(draft => localStorage.setItem('street-eco.activity-draft.v2', JSON.stringify(draft)), { version: 2, data: { name: '扫描分页草稿活动', typeId: generalType.id, pointId: activePoint.id, date: bootstrap.today, content: '关联已选问题', issueIds: [selected.id] } })
      const resolved = response('/issues/' + selected.id)
      await go('/activities/new'); assert.equal((await resolved).status(), 200)
      await page.getByLabel('关联整改问题', { exact: true }).filter({ hasText: selected.description }).waitFor()
      assert(!(await page.getByLabel('关联整改问题', { exact: true }).textContent()).includes(selected.id))
      const saved = response('/activities', 'POST')
      await page.getByTestId('activity-save').click()
      const record = await (await saved).json()
      assert.deepEqual(record.activity.relatedIssues.map(issue => issue.id), [selected.id])
      await page.waitForURL('**/#/activities/' + record.activity.id)
    })

    await test('S04 interrupted attachment batches stay with their original point', async () => {
      let release, ready, failReady, settled
      const held = new Promise(resolve => { release = resolve }), uploaded = new Promise((resolve, reject) => { ready = resolve; failReady = reject }), finished = new Promise(resolve => { settled = resolve })
      const writes = []
      const observe = request => { const url = new URL(request.url()); if (request.method() === 'PUT' && /\/api\/points\/[^/]+\/attachments$/.test(url.pathname)) writes.push({ point: url.pathname.split('/')[3], name: url.searchParams.get('name') }) }
      page.on('request', observe)
      const pattern = `**/api/points/${point.id}/attachments?*`
      await page.route(pattern, async route => {
        if (route.request().method() !== 'PUT') { await route.continue(); return }
        try {
          const url = new URL(route.request().url())
          const result = await route.fetch({ url: directOrigin + url.pathname + url.search }); assert.equal(result.status(), 201); ready()
          await held; await route.fulfill({ response: result }).catch(() => {})
        } catch (cause) { failReady(cause); await route.abort().catch(() => {}) } finally { settled() }
      })
      try {
        await go('/points/' + point.id)
        await page.getByTestId('attachment-file').setInputFiles([
          { name: 'scan-first.txt', mimeType: 'text/plain', buffer: Buffer.from('belongs to first point') },
          { name: 'scan-second.txt', mimeType: 'text/plain', buffer: Buffer.from('must not move to next point') }
        ])
        await uploaded
        await page.evaluate(id => { window.location.hash = '/points/' + id }, activePoint.id)
        await page.getByRole('heading', { name: activePoint.name, exact: true }).waitFor()
        release(); await finished; await page.waitForLoadState('networkidle')
        assert.deepEqual(writes, [{ point: point.id, name: 'scan-first.txt' }])
        assert.deepEqual((await api('/points/' + point.id + '/attachments')).map(item => item.name), ['scan-first.txt'])
        assert.deepEqual(await api('/points/' + activePoint.id + '/attachments'), [])
      } finally { release(); page.off('request', observe); await page.unroute(pattern) }
    })

    await test('S05 map filtering and location editing render at phone and desktop sizes', async () => {
      // Deterministic local tile fixture: layout/interaction tests need no provider key or network.
      await page.route('**/api/map/tiles/**', route => route.fulfill({ status: 200, contentType: 'image/png', body: Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+j7VQAAAAASUVORK5CYII=', 'base64') }))
      await go(`/points?view=map&tagId=${tag.id}&disabled=all&temporary=all`)
      await page.getByText('已保存', { exact: true }).waitFor({ state: 'hidden' })
      const marker = await renderedMarker([100, 116, 139])
      const canvas = await page.locator('.simple-map canvas').nth(marker.index).boundingBox()
      assert(canvas)
      await page.mouse.click(canvas.x + marker.x * canvas.width, canvas.y + marker.y * canvas.height)
      await page.getByRole('region', { name: '地图记录' }).filter({ hasText: point.name }).waitFor()
      await page.getByRole('button', { name: /未标记 1 条/ }).click()
      await page.getByRole('region', { name: '地图记录' }).filter({ hasText: '扫描标签临时点' }).waitFor()
      const panel = await page.getByRole('region', { name: '地图记录' }).boundingBox()
      const navigation = await page.getByRole('navigation', { name: '底部主导航' }).boundingBox()
      assert(panel && navigation && panel.y + panel.height <= navigation.y, 'Map records must stay above the bottom navigation')
      const detailAction = await page.getByRole('region', { name: '地图记录' }).getByRole('link', { name: '查看详情', exact: true }).boundingBox()
      assert(detailAction && detailAction.y + detailAction.height <= panel.y + panel.height, 'The first record action must be fully visible')
      await page.screenshot({ path: path.join(evidence, 'scan-map-375.png'), fullPage: true, animations: 'disabled' })
      await page.setViewportSize({ width: 1440, height: 900 })
      await page.screenshot({ path: path.join(evidence, 'scan-map-1440.png'), fullPage: true, animations: 'disabled' })
      await page.setViewportSize({ width: 375, height: 812 })
      await go('/points/' + point.id)
      await page.getByRole('button', { name: '编辑', exact: true }).click()
      const editor = page.getByRole('dialog', { name: '编辑点位', exact: true })
      await editor.getByRole('button', { name: '编辑位置', exact: true }).click()
      const picker = page.getByRole('dialog', { name: '标记位置', exact: true })
      await renderedMarker([13, 148, 136])
      await picker.getByRole('button', { name: '画区域', exact: true }).waitFor()
      const bounds = await picker.boundingBox(); assert(bounds && bounds.x >= 0 && bounds.x + bounds.width <= 376 && bounds.y >= 0 && bounds.y + bounds.height <= 813)
      await page.screenshot({ path: path.join(evidence, 'scan-location-picker-375.png'), animations: 'disabled' })
      await picker.getByRole('button', { name: '取消', exact: true }).click()
      await editor.getByRole('button', { name: '取消', exact: true }).click()
    })
    await test('S06 failed relation saves keep the editor and its error visible', async () => {
      await go('/points/' + activePoint.id)
      await page.getByRole('button', { name: '编辑关联', exact: true }).click()
      const dialog = page.getByRole('dialog', { name: '编辑关联', exact: true })
      const endpoint = `**/api/points/${activePoint.id}/relations`
      expectedValidationUrls.add(`${origin}/api/points/${activePoint.id}/relations`)
      await page.route(endpoint, route => route.fulfill({ status: 400, contentType: 'application/json', body: JSON.stringify({ message: '所选关联无法保存，请调整后重试' }) }))
      try {
        await dialog.getByRole('button', { name: '保存', exact: true }).click()
        await dialog.getByText('所选关联无法保存，请调整后重试', { exact: true }).waitFor()
        assert.equal(await dialog.isVisible(), true)
        assert.deepEqual((await api('/points/' + activePoint.id)).point.tags, [])
        await dialog.getByRole('button', { name: '取消', exact: true }).click()
      } finally { await page.unroute(endpoint) }
    })
    assert.deepEqual(errors, [], 'Consistency flows produced page errors')
    assert.deepEqual(consoleErrors, [], 'Consistency flows produced browser console errors')
  } catch (error) {
    await page.screenshot({ path: path.join(evidence, 'scan-failure.png'), fullPage: true }).catch(() => {})
    throw error
  } finally { await context.close() }
}
