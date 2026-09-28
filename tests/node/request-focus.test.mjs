import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { test } from 'node:test';
import vm from 'node:vm';

async function fixture() {
    const frames = [], dialogs = [];
    const document = { body: {}, querySelectorAll: () => dialogs };
    document.activeElement = document.body;
    const control = () => ({ isConnected: true, getClientRects: () => [{}], closest: () => null, matches: () => false,
        focus() { document.activeElement = this; } });
    const button = control();
    const context = vm.createContext({ document, crypto: { randomUUID: () => 'tab' },
        requestAnimationFrame: callback => frames.push(callback), getComputedStyle: () => ({ visibility: 'visible' }) });
    const module = new vm.SourceTextModule(await readFile(new URL('../../src/Lumibelle.UI/wwwroot/ai-jobs.js', import.meta.url), 'utf8'), { context });
    await module.link(() => { throw new Error('Unexpected import'); });
    await module.evaluate();
    return { document, button, control, dialogs, frames,
        restore: () => module.namespace.restoreRequestFocus({ querySelector: () => button }),
        flush: () => { while (frames.length) frames.shift()(); } };
}

test('closing a request restores its trigger after the dialog focus trap settles', async () => {
    const f = await fixture();
    f.restore();
    assert.equal(f.document.activeElement, f.document.body);
    f.flush();
    assert.equal(f.document.activeElement, f.button);
});

test('delayed request focus restoration preserves a newer keyboard target', async () => {
    const f = await fixture();
    f.restore(); f.frames.shift()();
    const picture = f.control(); picture.focus();
    f.flush();
    assert.equal(f.document.activeElement, picture);
});

test('delayed request focus restoration does not interrupt another dialog', async () => {
    const f = await fixture();
    f.restore(); f.dialogs.push(f.control()); f.flush();
    assert.equal(f.document.activeElement, f.document.body);
});
