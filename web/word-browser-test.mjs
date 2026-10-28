import assert from 'node:assert/strict'
import { mkdir, readFile } from 'node:fs/promises'
import path from 'node:path'

// Downloads originate from the visible v2 controls against the packaged process.
// The supplied inspection is fully closed; all images were added through the production uploader.
export async function verifyWordDownloads({ page, origin, evidence, inspectionId, remediationId, activityId, communityId, today, report, screenshot }) {
  assert(inspectionId && remediationId && activityId, 'Nine Word exports require a closed inspection, remediation and activity')
  const output = path.join(evidence, 'word-samples')
  await mkdir(output, { recursive: true })
  async function download(testId, kind, destination) {
    const pending = page.waitForEvent('download')
    await page.getByTestId(testId).click()
    const result = await pending
    assert.equal(await result.failure(), null, 'Word download failed')
    const filename = result.suggestedFilename()
    assert(filename.startsWith(kind + '_') && filename.endsWith('.docx'), 'Chinese download filename was lost: ' + filename)
    const file = path.join(output, destination)
    await result.saveAs(file)
    const bytes = await readFile(file)
    assert(bytes.length > 1000 && bytes.subarray(0, 4).equals(Buffer.from([0x50, 0x4b, 3, 4])), 'Downloaded Word file is not a ZIP package')
    report.wordDownloads ??= []
    report.wordDownloads.push({ kind, suggestedFilename: filename, bytes: bytes.length, sample: destination })
  }
  await page.setViewportSize({ width: 1440, height: 900 })
  await page.goto(origin + '/#/inspections/' + inspectionId, { waitUntil: 'networkidle' })
  await page.getByTestId('export-record').waitFor()
  await download('export-record', '排查记录单', 'record.docx')
  await download('export-notice', '整改通知单', 'notice.docx')
  await page.getByTestId('export-rectification').waitFor()
  await download('export-rectification', '整改记录单', 'rectification-record.docx')
  await screenshot('desktop-word-downloads')

  async function ledger(kind, includePhotos, filename) {
    const query = new URLSearchParams({ communityId })
    await page.goto(origin + `/#/inspections?${query}`, { waitUntil: 'networkidle' })
    await page.getByRole('button', { name: '导出台账', exact: true }).click()
    const dialog = page.getByRole('dialog', { name: '导出台账', exact: true })
    await dialog.waitFor()
    await dialog.getByRole('button', { name: '自定义范围', exact: true }).click()
    await dialog.getByTestId(`ledger-kind-${kind}`).click()
    await dialog.getByRole('button', { name: '自定义', exact: true }).click()
    await dialog.getByLabel('开始日期', { exact: true }).fill(today)
    await dialog.getByLabel('结束日期', { exact: true }).fill(today)
    if (includePhotos) await dialog.getByRole('checkbox', { name: '附照片', exact: true }).check()
    else if (kind === 'inspection') assert.equal(await dialog.getByRole('checkbox', { name: '附照片', exact: true }).count(), 0, 'Inspection ledger must not expose a photo option')
    await screenshot(`desktop-${kind}-ledger-export`)
    await download('export-ledger', kind === 'inspection' ? '排查台账' : '整改台账', filename)
    await dialog.waitFor({ state: 'hidden' })
  }
  await ledger('inspection', false, 'inspection-ledger.docx')
  await ledger('rectification', true, 'ledger-photos.docx')
  await page.goto(origin + '/#/activities/' + remediationId, { waitUntil: 'networkidle' })
  await page.getByTestId('export-activity-record').waitFor()
  await download('export-activity-record', '整治记录单', 'remediation-record.docx')
  await page.goto(origin + '/#/activities?template=remediation', { waitUntil: 'networkidle' })
  await page.getByRole('button', { name: '导出台账', exact: true }).click()
  const remediationDialog = page.getByRole('dialog', { name: '导出活动台账', exact: true })
  await remediationDialog.getByTestId('activity-ledger-kind-remediation').click()
  await remediationDialog.getByRole('checkbox', { name: /附.*照片/ }).check()
  await download('activity-ledger-export', '整治台账', 'remediation-ledger.docx')
  await remediationDialog.waitFor({ state: 'hidden' })
  await page.goto(origin + '/#/activities/' + activityId, { waitUntil: 'networkidle' })
  await download('export-activity-record', '活动开展情况记录单', 'activity-record.docx')
  await page.goto(origin + '/#/activities', { waitUntil: 'networkidle' })
  await page.getByRole('button', { name: '导出台账', exact: true }).click()
  const activityDialog = page.getByRole('dialog', { name: '导出活动台账', exact: true })
  await activityDialog.getByRole('button', { name: '自定义范围', exact: true }).click()
  await activityDialog.getByRole('button', { name: '自定义', exact: true }).click()
  await activityDialog.getByLabel('开始日期', { exact: true }).fill(today)
  await activityDialog.getByLabel('结束日期', { exact: true }).fill(today)
  await activityDialog.getByRole('checkbox', { name: '附活动照片', exact: true }).check()
  await download('activity-ledger-export', '活动台账', 'activity-ledger.docx')
  await activityDialog.waitFor({ state: 'hidden' })
  const kinds = report.wordDownloads.map(item => item.kind)
  for (const required of ['排查记录单', '整改通知单', '整改记录单', '排查台账', '整改台账', '整治记录单', '整治台账', '活动开展情况记录单', '活动台账']) assert(kinds.includes(required), 'Missing visible Word export: ' + required)
  await page.setViewportSize({ width: 375, height: 812 })
}
