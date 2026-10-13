from pathlib import Path

def edit(path, old, new):
    p=Path(path);s=p.read_text();assert s.count(old)==1,(path,old[:80],s.count(old));p.write_text(s.replace(old,new))

edit('server/WordExporter.cs', '_landscape ? 3400 : 5100', '_landscape ? 3000 : 5100')
edit('web/optimization-browser-test.mjs', '''      await page.waitForURL('**/#/points')
      paused.release()
      await page.unroute('**/api/photos/*/thumb', paused.handler)
      await expect.poll(() => serverPhotoStatus(photo.id)).toBe(404)''', '''      await page.waitForURL('**/#/points')
      // Hash changes before the route guard resolves. Wait for actual navigation
      // and unmount; releasing the response on hash alone races acceptance.
      await expect(page.getByRole('heading', { name: '点位管理', exact: true })).toBeVisible()
      await expect(page.getByTestId('inspection-save')).toHaveCount(0)
      paused.release()
      await page.unroute('**/api/photos/*/thumb', paused.handler)
      await expect.poll(() => serverPhotoStatus(photo.id), { timeout: 10000 }).toBe(404)''')
p=Path('.github/workflows/build.yml');s=p.read_text();assert '    needs: [web, native]\n' in s
s=s.replace('    needs: [web, native]\n','    needs: [web, native, browser, word-layout]\n',1)
s+='''
  word-layout:
    name: Render seven document types before and after
    needs: [native]
    runs-on: ubuntu-22.04
    timeout-minutes: 25
    steps:
      - uses: actions/checkout@v7
      - uses: actions/checkout@v7
        with:
          ref: 05eba126e7b9f3de13e5a2b055624ac6f97ee76d
          path: baseline-source
          persist-credentials: false
      - uses: actions/setup-dotnet@v6
        with:
          global-json-file: global.json
      - name: Install test-only local render tools
        run: sudo apt-get update && sudo apt-get install -y clang zlib1g-dev libreoffice-writer poppler-utils fonts-noto-cjk fonts-dejavu python3-pil
      - uses: actions/download-artifact@v8
        with:
          name: bin-linux-x64
          path: layout-program
      - name: Build the pinned original implementation
        run: dotnet publish baseline-source/server/StreetEcoTool.csproj -c Release -r linux-x64 -o layout-baseline-program -p:PublishAot=true -p:SelfContained=true -warnaserror
      - name: Export identical synthetic scenarios through real APIs
        run: |
          chmod +x layout-program/StreetEcoTool-linux-x64
          python3 tests/word-layout-samples.py --executable layout-baseline-program/StreetEcoTool --source-ref 05eba126e7b9f3de13e5a2b055624ac6f97ee76d --output test-results/word-layout/before
          python3 tests/word-layout-samples.py --executable layout-program/StreetEcoTool-linux-x64 --source-ref "$GITHUB_SHA" --expect-optimized --output test-results/word-layout/after
      - name: Convert and extract every page in the same font environment
        run: |
          python3 tests/render-word.py test-results/word-layout/before
          python3 tests/render-word.py test-results/word-layout/after
      - uses: actions/upload-artifact@v7
        if: always()
        with:
          name: word-layout-evidence
          path: test-results/word-layout/
          if-no-files-found: error
          retention-days: 14
'''
p.write_text(s)
Path('/tmp/hbtool-stage-message.txt').write_text('test(docx): real API samples, fixed-font rendering and gated release; refine photo layout\n')
