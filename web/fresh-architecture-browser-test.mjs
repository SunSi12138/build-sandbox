import assert from 'node:assert/strict'
import { createHash } from 'node:crypto'
import { readFile, writeFile } from 'node:fs/promises'
import path from 'node:path'
import { expect } from 'playwright/test'

export async function verifyFreshArchitecture({ browser, origin, api, test, bootstrap, point, evidence }) {
  const context = await browser.newContext({ viewport: { width: 375, height: 812 }, locale: 'zh-CN', reducedMotion: 'reduce', acceptDownloads: true })
  const page = await context.newPage(), errors = []
  page.on('pageerror', error => errors.push(String(error)))
  const go = route => page.url() === origin + '/#' + route ? page.reload({ waitUntil: 'networkidle' }) : page.goto(origin + '/#' + route, { waitUntil: 'networkidle' })
  const response = (route, method) => page.waitForResponse(r => new URL(r.url()).pathname === '/api' + route && r.request().method() === method)
  const choose = async (control, label) => { await control.click(); await page.getByRole('option', { name: label, exact: true }).click() }
  const chooseTag = async name => { await choose(page.getByLabel('标签', { exact: true }), name); await page.keyboard.press('Escape') }
  let tag, matter, attachment
  try {
    await test('N01 shared tags are created through settings and applied to a new matter', async () => {
      await go('/settings?tab=tags')
      await page.getByRole('button', { name: '新增标签', exact: true }).click()
      await page.getByTestId('tag-name').fill('浏览器跨模块专项')
      const saved = response('/tags', 'POST')
      await page.getByTestId('tag-save').click()
      assert.equal((await saved).status(), 201)
      tag = (await api('/tags')).find(item => item.name === '浏览器跨模块专项')
      assert(tag && !tag.disabled)
      await go('/matters/new')
      await page.getByTestId('matter-title').fill('居民反映河岸垃圾待核实')
      await choose(page.getByTestId('matter-source'), '群众反映')
      await page.getByLabel('经办人', { exact: true }).fill('事项经办人')
      await page.getByTestId('matter-content').fill('居民反映河岸存在垃圾，需要现场核实并答复。')
      await chooseTag(tag.name)
      const created = response('/matters', 'POST')
      await page.getByTestId('matter-save').click()
      const result = await created
      assert.equal(result.status(), 201)
      matter = await result.json()
      await page.waitForURL('**/#/matters/' + matter.id)
      const detail = await api('/matters/' + matter.id)
      assert.equal(detail.matter.source, 'public')
      assert.equal(detail.matter.status, 'pending')
      assert.equal(detail.matter.pointId, null)
      assert.deepEqual(detail.matter.tags.map(item => item.id), [tag.id])
      await expect(page.getByRole('heading', { name: matter.title, exact: true })).toBeVisible()
    })

    await test('N02 a matter shows linked inspection and independent issue assignments', async () => {
      const other = await api('/matters', { method: 'POST', body: { title: '独立事项不应自动关联问题', source: 'assigned', receivedDate: bootstrap.today, content: '只关联本次核查' } })
      const inspection = await api('/inspections', { method: 'POST', body: { pointId: point.id, date: bootstrap.today, inspector: '核实人员', source: 'matter', matterIds: [matter.id, other.id], result: 'issues', issues: [{ issueTypeId: bootstrap.issueTypes[0].id, description: '河岸堆放垃圾', requirement: '清运垃圾', deadline: bootstrap.today, responsibleUnit: '责任单位', matterIds: [matter.id] }] } })
      const detail = await api('/matters/' + matter.id)
      assert.equal(detail.inspections[0].id, inspection.inspection.id)
      assert.equal(detail.issues.length, 1)
      assert.equal((await api('/matters/' + other.id)).issues.length, 0)
      await go('/matters/' + matter.id)
      await expect(page.getByText('河岸堆放垃圾', { exact: true })).toBeVisible()
      await expect(page.locator(`a[href$="/inspections/${inspection.inspection.id}"]`).filter({ hasText: point.name }).first()).toBeVisible()
      await go('/inspections/' + inspection.inspection.id)
      await expect(page.getByText(matter.title, { exact: true }).first()).toBeVisible()
      await expect(page.getByText(other.title, { exact: true }).first()).toBeVisible()
    })

    await test('N03 matter handling actions preserve history and allow transferred closure and reopening', async () => {
      await go('/matters/' + matter.id)
      await page.getByRole('button', { name: '添加办理记录', exact: true }).click()
      await page.getByTestId('matter-action-notes').fill('已联系责任单位，安排现场处理。')
      const saved = response('/matters/' + matter.id + '/actions', 'POST')
      await page.getByTestId('matter-action-save').click()
      assert.equal((await saved).status(), 200)
      assert.equal((await api('/matters/' + matter.id)).matter.status, 'processing')
      await page.getByRole('button', { name: '办结事项', exact: true }).click()
      await page.getByLabel('办结结论', { exact: true }).click()
      await expect(page.getByRole('option', { name: '已解决', exact: true })).toBeDisabled()
      await page.getByRole('option', { name: '已移交', exact: true }).click()
      await page.getByTestId('matter-action-notes').fill('移交有管辖权的部门办理')
      const closed = response('/matters/' + matter.id + '/actions', 'POST')
      await page.getByTestId('matter-action-save').click()
      assert.equal((await closed).status(), 200)
      await expect(page.getByText('移交有管辖权的部门办理', { exact: true }).first()).toBeVisible()
      await page.getByRole('button', { name: '重新办理', exact: true }).click()
      await page.getByTestId('matter-action-notes').fill('收到补充情况，重新办理')
      const reopened = response('/matters/' + matter.id + '/actions', 'POST')
      await page.getByTestId('matter-action-save').click()
      assert.equal((await reopened).status(), 200)
      await expect(page.getByText('收到补充情况，重新办理', { exact: true })).toBeVisible()
      const detail = await api('/matters/' + matter.id)
      assert.equal(detail.actions.length, 3)
      assert.equal(detail.matter.status, 'processing')
    })

    await test('N04 point attachments download the original bytes and are separate from photos', async () => {
      await go('/points/' + point.id)
      const bytes = Buffer.from('%PDF-1.4\n% Original point attachment, do not compress.\n%%EOF\n')
      const saved = response('/points/' + point.id + '/attachments', 'PUT')
      await page.getByTestId('attachment-file').setInputFiles({ name: '点位检测报告.pdf', mimeType: 'application/pdf', buffer: bytes })
      assert.equal((await saved).status(), 201)
      attachment = (await api('/points/' + point.id)).attachments.find(item => item.name === '点位检测报告.pdf')
      assert(attachment)
      assert.equal(attachment.size, bytes.length)
      assert.equal(attachment.sha256.toLowerCase(), createHash('sha256').update(bytes).digest('hex'))
      const row = page.getByTestId('attachment-row').filter({ hasText: attachment.name })
      await expect(row).toHaveCount(1)
      const pending = page.waitForEvent('download')
      await row.getByTestId('attachment-download').click()
      const download = await pending
      assert.equal(await download.failure(), null)
      assert.equal(download.suggestedFilename(), attachment.name)
      assert.deepEqual(await readFile(await download.path()), bytes)
      await page.reload({ waitUntil: 'networkidle' })
      await expect(page.getByTestId('attachment-row').filter({ hasText: attachment.name })).toHaveCount(1)
    })

    await test('N05 ordinary activities link a matter and a shared tag without remediation fields', async () => {
      await go('/activities/new')
      await page.getByTestId('activity-name').fill('事项后续宣传活动')
      await choose(page.getByTestId('activity-type'), bootstrap.activityTypes.find(item => item.template === 'general').name)
      await page.getByTestId('activity-content').fill('围绕居民反映问题开展现场宣传。')
      await page.getByRole('button', { name: '更多信息', exact: true }).click()
      await chooseTag(tag.name)
      await choose(page.getByLabel('关联事项', { exact: true }), matter.title)
      await page.keyboard.press('Escape')
      await expect(page.getByTestId('activity-remediation-type')).toHaveCount(0)
      const saved = response('/activities', 'POST')
      await page.getByTestId('activity-save').click()
      assert.equal((await saved).status(), 201)
      const result = await (await saved).json()
      assert.equal(result.activity.template, 'general')
      assert.equal(result.activity.remediation, null)
      assert.deepEqual(result.activity.tags.map(item => item.id), [tag.id])
      assert.deepEqual(result.activity.matters.map(item => item.id), [matter.id])
      await go('/matters/' + matter.id)
      await expect(page.locator(`a[href$="/activities/${result.activity.id}"]`)).toContainText('事项后续宣传活动')
      assert.equal((await api('/activities?tagId=' + tag.id)).total, 1)
      assert.equal((await api('/tags')).find(item => item.id === tag.id).usageCount, 2)
    })
    await test('N06 disabled tags retain historical links and cannot be newly selected', async () => {
      await go('/settings?tab=tags')
      await page.getByRole('button', { name: '编辑标签' + tag.name, exact: true }).click()
      await page.getByRole('checkbox', { name: '停用（保留历史记录，不再用于新增）', exact: true }).check()
      const saved = response('/tags/' + tag.id, 'PUT')
      await page.getByTestId('tag-save').click()
      assert.equal((await saved).status(), 200)
      assert.equal((await api('/tags')).find(item => item.id === tag.id).disabled, true)
      await go('/matters/' + matter.id)
      await expect(page.getByText(tag.name, { exact: true })).toBeVisible()
      assert.deepEqual((await api('/matters/' + matter.id)).matter.tags.map(item => item.id), [tag.id])
      await go('/matters/new')
      await page.getByLabel('标签', { exact: true }).click()
      // Reka marks non-native options with data-disabled; verify the blocked interaction too.
      const stopped = page.getByRole('option', { name: tag.name + '（已停用）', exact: true })
      await expect(stopped).toHaveAttribute('data-disabled', '')
      await stopped.click()
      await expect(stopped).toHaveAttribute('aria-selected', 'false')
      await page.keyboard.press('Escape')
      await expect(page.getByLabel('标签', { exact: true })).toContainText('选择标签')
    })
    assert.deepEqual(errors, [], 'New domain pages must not produce browser errors')
    return { matterId: matter.id, tagId: tag.id, attachmentId: attachment.id, attachmentName: attachment.name, attachmentSize: attachment.size, attachmentSha256: attachment.sha256 }
  } catch (error) {
    if (evidence) {
      await page.screenshot({ path: path.join(evidence, 'fresh-failure.png'), fullPage: true }).catch(() => {})
      await writeFile(path.join(evidence, 'fresh-failure.html'), await page.content()).catch(() => {})
    }
    throw error
  } finally { await context.close() }
}
