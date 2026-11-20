import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
const code = await readFile(new URL('../wwwroot/qr-scan.js', import.meta.url), 'utf8');
const scanner = await import('data:text/javascript;base64,' + Buffer.from(code).toString('base64'));
const deferred = () => { let resolve; const promise = new Promise(r => resolve = r); return { promise, resolve }; };
function media(promise) { Object.defineProperty(globalThis, 'navigator', { configurable: true, value: { mediaDevices: { getUserMedia: () => promise } } }); }

test('cancel during permission prompt stops a subsequently granted stream', async () => {
    let stopped = 0;
    const pending = deferred(); media(pending.promise);
    const opening = scanner.open('Synthetic', 'Cancel');
    scanner.close();
    pending.resolve({ getTracks: () => [{ stop: () => stopped++ }] });
    assert.equal(await opening, 'closed');
    assert.equal(stopped, 1);
    assert.equal(await scanner.nextFrame(), 'closed');
});

test('cancel while detecting support cannot create a late overlay', async () => {
    let stopped = 0;
    const formats = deferred();
    globalThis.BarcodeDetector = { getSupportedFormats: () => formats.promise };
    media(Promise.resolve({ getTracks: () => [{ stop: () => stopped++ }] }));
    const opening = scanner.open('Synthetic', 'Cancel');
    await Promise.resolve();
    scanner.close(); formats.resolve([]);
    assert.equal(await opening, 'closed');
    assert.equal(stopped, 1);
    delete globalThis.BarcodeDetector;
});

test('closing an opened scanner removes its overlay and releases a waiting frame', async () => {
    let stopped = 0, removed = 0;
    media(Promise.resolve({ getTracks: () => [{ stop: () => stopped++ }] }));
    globalThis.document = { body: { append() {} }, createElement: () => ({ style: {}, setAttribute() {}, append() {}, remove() { removed++; }, play: async () => {} }) };
    assert.equal(await scanner.open('Synthetic', 'Cancel'), 'ok');
    const frame = scanner.nextFrame(); scanner.close();
    assert.equal(await frame, 'closed');
    assert.equal(stopped, 1); assert.equal(removed, 1);
    delete globalThis.document;
});
