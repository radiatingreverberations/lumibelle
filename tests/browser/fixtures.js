import { test as base, expect } from '@playwright/test';
import { spawn } from 'node:child_process';
import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { root, configuration } from './build-host.js';

// Browser contexts do not isolate the server's queue, settings, Trash or mock state.
// Give each test its own real host, including tests that fail before their cleanup.
export const test = base.extend({
  hostLifecycle: async ({}, use) => { await use({}); },
  baseURL: async ({ hostLifecycle }, use, testInfo) => {
    const directory = await mkdtemp(path.join(tmpdir(), 'lumibelle-browser-'));
    const host = spawn('dotnet', [
      process.env.LUMIBELLE_BROWSER_HOST_DLL ?? path.join(root, 'tests/Lumibelle.BrowserHost/bin', configuration, 'net10.0/Lumibelle.BrowserHost.dll'),
      '--urls', 'http://127.0.0.1:0'
    ], { cwd: path.join(root, 'tests/Lumibelle.BrowserHost'), windowsHide: true,
      env: { ...process.env, LUMIBELLE_BROWSER_DATA: directory } });
    let output = '';
    const exited = new Promise(resolve => host.once('close', resolve));
    hostLifecycle.exited = exited;
    hostLifecycle.output = () => output;
    try {
      const address = await new Promise((resolve, reject) => {
        const timer = setTimeout(() => reject(new Error(`Browser host did not start.\n${output}`)), 20000);
        const fail = error => { clearTimeout(timer); reject(error); };
        host.once('error', fail);
        host.once('exit', code => fail(new Error(`Browser host exited (${code}).\n${output}`)));
        const read = data => {
          output = (output + data.toString()).slice(-2 * 1024 * 1024);
          const match = output.match(/Now listening on: (http:\/\/127\.0\.0\.1:\d+)/);
          if (match) { clearTimeout(timer); resolve(match[1]); }
        };
        host.stdout.on('data', read); host.stderr.on('data', read);
      });
      await use(address);
    } finally {
      if (host.exitCode === null) host.kill();
      await exited;
      if (testInfo.status !== testInfo.expectedStatus)
        await testInfo.attach('browser-host.log', { body: output, contentType: 'text/plain' });
      // Only remove the unique temporary directory created by this fixture.
      if (path.dirname(path.resolve(directory)) === path.resolve(tmpdir()) && path.basename(directory).startsWith('lumibelle-browser-'))
        await rm(directory, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 });
    }
  }
});

export { expect };
