// No external dependencies: node --test mappreview/rotation-export.test.mjs
import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { join, dirname } from 'node:path';
import { runInNewContext } from 'node:vm';

const html = readFileSync(join(dirname(fileURLToPath(import.meta.url)), 'preview.html'), 'utf8');

// Exercise the actual, self-contained inline helpers used by preview.html.
// Avoid copying the math into the tests: a regression in the page must fail here.
function pageFunction(name, globals = {}) {
  const marker = '  function ' + name + '(';
  const start = html.indexOf(marker);
  assert.ok(start >= 0, name + ' not found in preview.html');
  const open = html.indexOf('{', start);
  let depth = 0;
  let end = -1;
  for (let i = open; i < html.length; i++) {
    if (html[i] === '{') depth++;
    if (html[i] === '}' && --depth === 0) { end = i + 1; break; }
  }
  assert.ok(end !== -1, name + ' has no closing brace');
  return runInNewContext('(' + html.slice(start, end) + ')', globals);
}

function close(actual, expected, message) {
  assert.ok(Math.abs(actual - expected) < 1e-7, message + ': ' + actual + ' != ' + expected);
}
function sameCoord(actual, expected, label) {
  close(actual[0], expected[0], label + ' / x');
  close(actual[1], expected[1], label + ' / y');
}

test('positive 90° view rotation maps screen right to north and screen down to east', () => {
  const coord = pageFunction('exportPixelToCoordinate');
  sameCoord(coord([100, 200], 2, Math.PI / 2, 400, 200, 210, 100), [100, 220], 'right');
  sameCoord(coord([100, 200], 2, Math.PI / 2, 400, 200, 200, 110), [120, 200], 'down');
  sameCoord(coord([100, 200], 2, 0, 400, 200, 210, 110), [120, 180], 'north up');
});

test('each rotated stripe exactly matches full-image coordinates, including a shorter final stripe', () => {
  const coord = pageFunction('exportPixelToCoordinate');
  const center = [1023043.25, 345642.75];
  const width = 1049, height = 1003, bandHeight = 128, resolution = 0.373;
  for (const rotation of [0, Math.PI / 6, -Math.PI / 4, Math.PI / 2, Math.PI, 2.71]) {
    for (let y0 = 0; y0 < height; y0 += bandHeight) {
      const bh = Math.min(bandHeight, height - y0);
      const bandCenter = coord(center, resolution, rotation, width, height, width / 2, y0 + bh / 2);
      for (const x of [0, width / 2, width - 1]) {
        for (const localY of [0, bh / 2, bh - 1]) {
          const whole = coord(center, resolution, rotation, width, height, x, y0 + localY);
          const inBand = coord(bandCenter, resolution, rotation, width, bh, x, localY);
          sameCoord(inBand, whole, 'rotation ' + rotation + ' y0 ' + y0);
        }
      }
    }
  }
});

function planInputs(checked, scale, force) {
  const globals = {
    MAX_TILE_ZOOM: 18,
    map: { getSize: () => [2000, 1200], getView: () => ({ getZoom: () => 14 }) },
    exportScaleEl: { value: String(scale) },
    exportDepthEl: { value: '4' },
    chunkForceEl: { checked: force },
    chunkHeightEl: { value: '512' },
    exportRotationEl: { checked }
  };
  return pageFunction('exportPlan', globals)();
}

test('rotated 16× exports retain chunk mode and are not downgraded to the single-image 80MP cap', () => {
  const plan = planInputs(true, 16, false);
  assert.equal(plan.chunked, true);
  assert.equal(plan.outScale, 16);
  assert.equal(plan.outW, 32000);
  assert.equal(plan.outH, 19200);
  assert.equal(plan.chunks, 38);
  assert.equal(plan.sourceMaxZoom, 18);
  assert.equal(plan.outW, plan.renderW);
  assert.equal(plan.outH, plan.renderH);
});

test('forced stripes work at small scales; regular exports remain single-image', () => {
  assert.equal(planInputs(true, 2, true).chunked, true);
  assert.equal(planInputs(false, 2, false).chunked, false);
});

test('layer canvases use their CSS transforms and opacity instead of stretching to the output size', () => {
  const ops = [];
  class Matrix {
    constructor(value) {
      [this.a, this.b, this.c, this.d, this.e, this.f] =
        value.slice(7, -1).split(',').map(Number);
    }
  }
  const compose = pageFunction('drawOffscreenLayers', {
    DOMMatrixReadOnly: Matrix,
    getComputedStyle: (canvas) => ({ transform: canvas.style.transform || 'none' }),
    Number,
    parseFloat
  });
  const canvases = [
    { id: 1, width: 300, height: 200, style: { transform: 'matrix(1,0,0,1,-20,9)' }, parentElement: { style: { opacity: '0.4' } } },
    { id: 2, width: 300, height: 200, style: { width: '150px', height: '100px', transform: 'none' }, parentElement: { style: { opacity: '' } } }
  ];
  const ctx = {
    globalAlpha: 1,
    save() { this.savedAlpha = this.globalAlpha; },
    restore() { this.globalAlpha = this.savedAlpha; },
    setTransform(...matrix) { this.matrix = matrix; },
    drawImage(canvas) { ops.push({ id: canvas.id, matrix: this.matrix, alpha: this.globalAlpha }); }
  };
  compose({ getViewport: () => ({ querySelectorAll: () => canvases }) }, ctx);
  assert.equal(ops.length, 2);
  assert.deepEqual(ops[0], { id: 1, matrix: [1, 0, 0, 1, -20, 9], alpha: 0.4 });
  assert.deepEqual(ops[1], { id: 2, matrix: [0.5, 0, 0, 0.5, 0, 0], alpha: 1 });
});

test('stripe rendering waits for rendercomplete and reports a real timeout', async () => {
  const wait = pageFunction('waitForTiles', { setTimeout, clearTimeout, Promise, Error });
  let listener = null;
  const fakeMap = {
    on(type, fn) { assert.equal(type, 'rendercomplete'); listener = fn; },
    un(type, fn) { assert.equal(type, 'rendercomplete'); assert.equal(fn, listener); listener = null; },
    renderSync() { setTimeout(() => listener?.(), 15); }
  };
  await wait(fakeMap, 200);
  assert.equal(listener, null);
  await assert.rejects(
    wait({ on() {}, un() {}, renderSync() {} }, 15),
    /渲染超时/
  );
});
