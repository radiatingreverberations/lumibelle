import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { fileURLToPath } from 'node:url';

export const root = fileURLToPath(new URL('../../', import.meta.url));
export const configuration = process.env.LUMIBELLE_BROWSER_CONFIGURATION || 'Debug';

export default async function buildHost() {
  await promisify(execFile)('dotnet', ['build', 'tests/Lumibelle.BrowserHost', '-c', configuration, '--no-restore', '-m:1', '-nr:false'],
    { cwd: root, windowsHide: true, timeout: 120000, maxBuffer: 10 * 1024 * 1024 });
}
