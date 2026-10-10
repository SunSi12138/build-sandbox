import assert from 'node:assert/strict'
import { mkdir, readFile } from 'node:fs/promises'
import path from 'node:path'

// Exercise the actual download buttons against the packaged NativeAOT process.
// The source inspection contains JPEGs uploaded through the production Worker.
export async function verifyWordDownloads({ page, origin, evidence, inspectionId, communityId, today, report, screenshot }) {
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

  await page.setViewportSize({ width: 1440, height: 1040 })
  await page.goto(origin + '/#/inspections/' + inspectionId, { waitUntil: 'networkidle' })
  await page.getByTestId('export-record').waitFor()
  await download('export-record', '排查记录单', 'record.docx')
  await download('export-notice', '整改通知单', 'notice.docx')
  await screenshot('desktop-word-downloads')

  const query = new URLSearchParams({ from: today, to: today, communityId, photos: 'true' })
  await page.goto(origin + '/#/exports?' + query, { waitUntil: 'networkidle' })
  await page.getByTestId('export-ledger').waitFor()
  await page.waitForFunction(() => document.querySelector('[data-testid="export-ledger"]')?.getAttribute('aria-disabled') !== 'true')
  await download('export-ledger', '整改台账', 'ledger-photos.docx')
  await screenshot('desktop-ledger-export')
  assert(page.url().includes('photos=true'), 'Ledger photo filter was not preserved in the URL')
  await page.setViewportSize({ width: 390, height: 844 })
}
