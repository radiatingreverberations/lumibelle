// Splits the browser suite into CI shards of similar duration. Every spec file in
// tests/browser runs in exactly one shard, so a new spec cannot be left out of CI.
// Files are weighted by their measured duration in tests/browser/durations.json; a
// file without one counts as the median file until the durations are refreshed.
//
//   node build/browser-shards.mjs <shard> <total>   Playwright filters for one shard (1-based)
//   node build/browser-shards.mjs --plan <total>    each shard's files and expected seconds
//   node build/browser-shards.mjs --update <report> refresh durations from a full run's JSON report:
//     PLAYWRIGHT_JSON_OUTPUT_NAME=test-results/report.json npx playwright test --reporter=json
import {readdirSync, readFileSync, writeFileSync} from 'node:fs';
const folder = 'tests/browser', store = `${folder}/durations.json`;
const specs = () => readdirSync(folder).filter(f => f.endsWith('.spec.js')).sort();
const durations = () => { try { return JSON.parse(readFileSync(store, 'utf8')); } catch (e) { if (e.code === 'ENOENT') return {}; throw e; } };

function plan(total) {
  const measured = durations(), files = specs();
  const known = files.map(f => measured[f]).filter(Number.isFinite).sort((a, b) => a - b);
  const median = known.length ? known[Math.floor(known.length / 2)] : 1;
  const shards = Array.from({length: total}, () => ({files: [], seconds: 0}));
  // Longest first, each to the least loaded shard: deterministic for a given file list.
  for (const [file, seconds] of files.map(f => [f, measured[f] ?? median]).sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0]))) {
    const target = shards.reduce((least, s) => s.seconds < least.seconds ? s : least);
    target.files.push(file); target.seconds += seconds;
  }
  for (const s of shards) s.files.sort();
  return shards;
}

function update(report) {
  const seconds = {};
  const visit = (suite, file) => {
    file = suite.file ?? file;
    for (const spec of suite.specs ?? []) for (const test of spec.tests) for (const result of test.results)
      seconds[file] = (seconds[file] ?? 0) + result.duration / 1000;
    for (const child of suite.suites ?? []) visit(child, file);
  };
  for (const suite of JSON.parse(readFileSync(report, 'utf8')).suites) visit(suite);
  const names = Object.keys(seconds).map(f => [f.split(/[\\/]/).pop(), Math.max(1, Math.round(seconds[f]))]).sort((a, b) => a[0].localeCompare(b[0]));
  writeFileSync(store, JSON.stringify(Object.fromEntries(names), null, 2) + '\n');
  console.log(`Saved durations for ${names.length} spec files to ${store}.`);
}

const [command, value, extra] = process.argv.slice(2);
const count = text => { const n = Number(text); if (!Number.isInteger(n) || n < 1) throw new Error(`Expected a positive whole number, not "${text}".`); return n; };
if (command === '--update') update(value);
else if (command === '--plan') plan(count(value)).forEach((s, i) => console.log(`${i + 1}: ${Math.round(s.seconds)} s  ${s.files.join(' ')}`));
else {
  const shard = count(command), total = count(value);
  if (shard > total || extra !== undefined) throw new Error('Usage: node build/browser-shards.mjs <shard> <total>');
  // Playwright reads each argument as a regular expression over the file path: anchor
  // and escape it, or shots.spec.js would also select unified-shots.spec.js.
  for (const file of plan(total)[shard - 1].files) console.log(`${folder}/${file}$`.replaceAll('.', '\\.'));
}
