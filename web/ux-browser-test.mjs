import assert from 'node:assert/strict'
import { execFileSync } from 'node:child_process'
import { mkdir } from 'node:fs/promises'
import path from 'node:path'
import { expect } from 'playwright/test'

export async function verifyUsability({ browser, origin, api, test, bootstrap, activityId, evidence }) {
  const context = await browser.newContext({ viewport: { width: 375, height: 812 }, locale: 'zh-CN', reducedMotion: 'reduce' })
  const page = await context.newPage()
  const errors = []
  page.on('pageerror', error => errors.push(String(error)))
  const go = route => page.goto(origin + '/#' + route, { waitUntil: 'networkidle' })
  const choose = async (control, name) => { await control.click(); await page.getByRole('option', { name, exact: true }).click() }
  const savedResponse = (route, method) => page.waitForResponse(response => new URL(response.url()).pathname === '/api' + route && response.request().method() === method)
  const output = path.join(evidence, 'usability'); await mkdir(output, { recursive: true })
  const settings = await api('/settings')
  async function screenshot(name) {
    await expect(page.getByText('已保存', { exact: true })).toHaveCount(0)
    await page.screenshot({ path: path.join(output, name), fullPage: true, animations: 'disabled' })
  }
  async function document(button, filename) {
    const pending = page.waitForEvent('download')
    await button.click()
    const download = await pending
    assert.equal(await download.failure(), null)
    const file = path.join(output, filename); await download.saveAs(file)
    return execFileSync('python3', ['-c', "import sys,zipfile,xml.etree.ElementTree as E; z=zipfile.ZipFile(sys.argv[1]); print(''.join(E.fromstring(z.read('word/document.xml')).itertext()))", file], { encoding: 'utf8' })
  }
  try {
    await test('UX activity edit protects unsaved changes and optional fields remain editable', async () => {
      const original = (await api('/activities/' + activityId)).activity
      await go('/activities/new?edit=' + activityId)
      await page.getByTestId('activity-name').fill('未保存修改应保留')
      await page.getByRole('button', { name: '更多信息', exact: true }).click()
      await page.getByLabel('参加人数', { exact: true }).fill('25')
      await page.getByRole('link', { name: '取消', exact: true }).click()
      const dialog = page.getByRole('dialog', { name: '放弃未保存的修改？', exact: true })
      await expect(dialog).toBeVisible()
      await dialog.getByRole('button', { name: '继续编辑', exact: true }).click()
      await expect(page.getByTestId('activity-name')).toHaveValue('未保存修改应保留')
      await expect(page.getByLabel('参加人数', { exact: true })).toHaveValue('25')
      await page.getByRole('link', { name: '取消', exact: true }).click()
      await dialog.getByRole('button', { name: '离开', exact: true }).click()
      await page.waitForURL('**/#/activities/' + activityId)
      assert.equal((await api('/activities/' + activityId)).activity.name, original.name)
      await go('/activities/new?edit=' + activityId)
      await page.getByTestId('activity-content').fill('编辑保护后的正式内容')
      const response = savedResponse('/activities/' + activityId, 'PUT')
      await page.getByTestId('activity-save').click()
      assert.equal((await response).status(), 200)
      await page.waitForURL('**/#/activities/' + activityId)
      assert.equal((await api('/activities/' + activityId)).activity.content, '编辑保护后的正式内容')
      await go('/activities/new')
      await expect(page.getByLabel('参加人数', { exact: true })).toBeHidden()
      await page.getByTestId('activity-name').fill('离开时立即保存草稿')
      await page.getByRole('link', { name: '取消', exact: true }).click()
      await go('/activities/new')
      await expect(page.getByTestId('activity-name')).toHaveValue('离开时立即保存草稿')
      await screenshot('activity-form-375.png')
    })

    await test('UX library tags preserve dotted names and unfinished text; dictionaries are grouped', async () => {
      await go('/settings?tab=basic')
      const input = page.getByTestId('default-inspector')
      const host = input.locator('xpath=ancestor::*[contains(@class,"names-field")][1]')
      await input.fill('')
      while (await host.locator('.name-remove').count()) await host.locator('.name-remove').first().click()
      await input.fill('')
      await input.evaluate(element => {
        const clipboardData = new DataTransfer(); clipboardData.setData('text/plain', '环保办、张三；Dr.Li')
        element.dispatchEvent(new ClipboardEvent('paste', { clipboardData, bubbles: true, cancelable: true }))
      })
      await input.press('Enter')
      await expect(host.locator('.name-edit-tag')).toHaveCount(3)
      await expect(host).toContainText('Dr.Li')
      await input.fill('未按回车姓名')
      await screenshot('name-input-375.png')
      const response = savedResponse('/settings', 'PUT')
      await page.getByTestId('settings-save').click()
      assert.equal((await response).status(), 200)
      assert.equal((await api('/settings')).defaultInspector, '环保办 张三 Dr.Li 未按回车姓名')
      await go('/settings?tab=dictionaries')
      await choose(page.getByRole('combobox', { name: '选择分类组' }), '问题类型')
      await page.getByRole('searchbox', { name: '搜索问题类型' }).fill(bootstrap.issueTypes[0].name)
      await expect(page.locator('.group-list .list-row')).toHaveCount(1)
      await choose(page.getByRole('combobox', { name: '选择分类组' }), '活动类型')
      await expect(page.getByRole('heading', { name: '活动类型', exact: true })).toBeVisible()
      await expect(page.getByRole('heading', { name: '问题类型', exact: true })).toHaveCount(0)
      await screenshot('dictionary-375.png')
    })

    await test('UX inspection category and current export retain search, category and exact issue count', async () => {
      const point = await api('/points', { method: 'POST', body: { name: '体验筛选点位', communityId: bootstrap.communities[0].id, typeId: bootstrap.pointTypes[0].id, address: '测试地址', frequency: 'weekly' } })
      const create = (inspector, type, description) => api('/inspections', { method: 'POST', body: { pointId: point.id, date: bootstrap.today, inspector, result: 'issues', issues: [{ issueTypeId: type, description, deadline: bootstrap.today }] } })
      await create('专用筛选人员', bootstrap.issueTypes[0].id, '精确导出的问题内容')
      await create('排除的另一人员', bootstrap.issueTypes[0].id, '搜索不匹配不得出现')
      await create('专用筛选人员', bootstrap.issueTypes[1].id, '类型不匹配不得出现')
      await go('/inspections')
      await page.getByRole('searchbox', { name: '搜索排查' }).fill('专用筛选人员')
      await choose(page.getByRole('combobox', { name: '问题类型筛选' }), bootstrap.issueTypes[0].name)
      await expect(page.getByTestId('inspection-row')).toHaveCount(1)
      await page.getByRole('button', { name: '导出台账', exact: true }).filter({ visible: true }).click()
      const dialog = page.getByRole('dialog', { name: '导出台账', exact: true })
      await expect(dialog.getByRole('button', { name: '当前筛选', exact: true })).toHaveAttribute('aria-pressed', 'true')
      await dialog.getByTestId('ledger-kind-rectification').click()
      await expect(dialog.getByTestId('ledger-preview')).toHaveText('预计 1 项问题')
      await expect(dialog.getByTestId('ledger-summary')).toContainText('专用筛选人员')
      const text = await document(dialog.getByTestId('export-ledger'), 'current-issues.docx')
      assert(text.includes('精确导出的问题内容'))
      assert(!text.includes('搜索不匹配不得出现')); assert(!text.includes('类型不匹配不得出现'))
    })

    await test('UX activity search is debounced and exports current results or a custom range', async () => {
      const typeId = bootstrap.activityTypes[0].id
      await api('/activities', { method: 'POST', body: { name: '专用活动筛选结果', typeId, date: bootstrap.today, content: '活动命中正文' } })
      await api('/activities', { method: 'POST', body: { name: '其他活动排除', typeId, date: bootstrap.today, content: '活动排除正文' } })
      await go('/activities')
      await expect(page.getByTestId('activity-row').filter({ hasText: '其他活动排除' })).toBeVisible()
      const requests = []
      const listener = request => { if (new URL(request.url()).pathname === '/api/activities' && request.method() === 'GET') requests.push(request.url()) }
      page.on('request', listener)
      await page.getByRole('searchbox', { name: '搜索活动' }).pressSequentially('专用活动筛选', { delay: 15 })
      await expect(page.getByTestId('activity-row')).toHaveCount(1)
      page.off('request', listener)
      assert.equal(requests.length, 1, 'Typing a search should produce one settled request: ' + requests.join(' | '))
      await page.getByRole('button', { name: '导出活动台账', exact: true }).click()
      const dialog = page.getByRole('dialog', { name: '导出活动台账', exact: true })
      await expect(dialog.getByTestId('ledger-preview')).toHaveText('预计 1 条记录')
      const text = await document(dialog.getByTestId('activity-ledger-export'), 'current-activities.docx')
      assert(text.includes('活动命中正文')); assert(!text.includes('活动排除正文'))
      await go('/activities?q=' + encodeURIComponent('专用活动筛选') + '&q=ignored')
      await expect(page.getByTestId('activity-row')).toHaveCount(1)
      await page.getByRole('button', { name: '导出活动台账', exact: true }).click()
      await expect(dialog.getByTestId('ledger-summary')).toContainText('专用活动筛选')
      await expect(dialog.getByTestId('ledger-preview')).toHaveText('预计 1 条记录')
      await dialog.getByRole('button', { name: '取消', exact: true }).click()
      await page.getByRole('searchbox', { name: '搜索活动' }).fill('不会命中任何活动')
      await page.getByRole('button', { name: '导出活动台账', exact: true }).click()
      await expect(dialog.getByTestId('ledger-summary')).toContainText('不会命中任何活动')
      await expect(dialog.getByTestId('ledger-preview')).toHaveText('预计 0 条记录')
      await dialog.getByRole('button', { name: '取消', exact: true }).click()
      await page.getByRole('button', { name: '导出活动台账', exact: true }).click()
      await dialog.getByRole('button', { name: '自定义范围', exact: true }).click()
      await dialog.getByRole('button', { name: '上月', exact: true }).click()
      await expect(dialog.getByTestId('ledger-preview')).toHaveText('预计 0 条记录')
      await expect(dialog.getByTestId('ledger-summary')).not.toContainText('专用活动筛选')
      await screenshot('export-scope-375.png')
      await dialog.getByRole('button', { name: '取消', exact: true }).click()
    })
    assert.deepEqual(errors, [])
  } catch (error) {
    console.error('UX page', await page.locator('body').innerText())
    await page.screenshot({ path: path.join(output, 'failure.png'), fullPage: true })
    throw error
  } finally {
    await api('/settings', { method: 'PUT', body: settings })
    await context.close()
  }
}
