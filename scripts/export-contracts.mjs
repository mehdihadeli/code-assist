import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { once } from 'node:events';
import fs from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('../', import.meta.url));
const configuration = process.argv.includes('--release') ? 'Release' : 'Debug';
const directory = await fs.mkdtemp(path.join(tmpdir(), 'codeassist-contracts-'));
const token = randomBytes(32).toString('hex');
const child = spawn('dotnet', [path.join(root, `src/CodeAssist.Server/bin/${configuration}/net10.0/CodeAssist.Server.dll`), '--listen', '127.0.0.1:0'], {
  cwd: directory,
  env: { ...process.env, CODE_ASSIST_HOME: directory, CODE_ASSIST_MANAGED_DIR: directory, Host__Listen__Http__Token: token, PWD: directory },
  stdio: ['ignore', 'pipe', 'pipe'], windowsHide: true,
});
child.stderr.resume();
try {
  const endpoint = await new Promise((resolve, reject) => {
    let pending = '';
    const timer = setTimeout(() => reject(new Error('Contract service startup timed out.')), 20000);
    const fail = () => { clearTimeout(timer); reject(new Error('Contract service failed to start.')); };
    child.once('error', fail);
    child.once('exit', fail);
    child.stdout.setEncoding('utf8');
    child.stdout.on('data', chunk => {
      pending += chunk;
      if (pending.length > 65536) { fail(); return; }
      const newline = pending.indexOf('\n');
      if (newline < 0) return;
      try {
        const ready = JSON.parse(pending.slice(0, newline));
        assert.equal(ready.kind, 'ready');
        assert.equal(ready.protocol, '1');
        assert.match(ready.endpoint, /^http:\/\/127\.0\.0\.1:[1-9][0-9]*$/);
        clearTimeout(timer);
        child.removeListener('exit', fail);
        resolve(ready.endpoint);
      } catch { fail(); }
    });
  });
  const response = await fetch(endpoint + '/openapi/v1.json', { headers: { authorization: `Bearer ${token}` }, signal: AbortSignal.timeout(15000), redirect: 'error' });
  assert.equal(response.status, 200);
  const document = await response.json();
  delete document.servers;
  const normalized = value => Array.isArray(value) ? value.map(normalized) : value && typeof value === 'object'
    ? Object.fromEntries(Object.keys(value).sort().map(key => [key, normalized(value[key])])) : value;
  const output = new URL('../codeassist-contracts/openapi.json', import.meta.url);
  const text = JSON.stringify(normalized(document), null, 2) + '\n';
  if (process.argv.includes('--update')) await fs.writeFile(output, text);
  else {
    const canonical = normalized(JSON.parse(await fs.readFile(output, 'utf8')));
    const differences = [];
    const compare = (expected, actual, pointer) => {
      if (JSON.stringify(expected) === JSON.stringify(actual)) return;
      if (expected && actual && typeof expected === 'object' && typeof actual === 'object') {
        for (const key of new Set([...Object.keys(expected), ...Object.keys(actual)])) compare(expected[key], actual[key], `${pointer}/${key}`);
      } else differences.push(`${pointer}: expected ${JSON.stringify(expected)}, received ${JSON.stringify(actual)}`);
    };
    compare(canonical, normalized(document), '');
    assert.equal(differences.length, 0, `Published API differs from canonical contracts:\n${differences.slice(0, 20).join('\n')}`);
  }
  console.log(`Canonical API verified: ${Object.keys(document.paths).length} paths.`);
} finally {
  if (child.exitCode === null && child.signalCode === null) {
    const exited = once(child, 'exit');
    child.kill();
    await exited;
  }
  await fs.rm(directory, { recursive: true, force: true });
}