import assert from 'node:assert/strict';
import { fileURLToPath } from 'node:url';
import { test } from 'node:test';
import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { ManagedServer } from '../dist/server.js';

const fixture = fileURLToPath(new URL('./fixtures/server.mjs', import.meta.url));

test('starts an owned loopback server with a private generated token and stops it', async () => {
  const server = await ManagedServer.start(process.execPath, [fixture], process.cwd());
  try { assert.equal(server.alive, true); await server.client.check(); }
  finally { await server.dispose(); }
  assert.equal(server.alive, false);
  await assert.rejects(server.client.check());
  await server.dispose();
});

test('rejects invalid launch settings and canceled startup', async () => {
  await assert.rejects(ManagedServer.start('', [], process.cwd()), /Invalid/);
  await assert.rejects(ManagedServer.start(process.execPath, ['-e', 'process.exit(1)'], process.cwd()), /failed to start/);
  const abort = new AbortController(); abort.abort();
  await assert.rejects(ManagedServer.start(process.execPath, [fixture], process.cwd(), abort.signal), /canceled/);
});

test('reports missing executable without leaking inherited token', async () => {
  await assert.rejects(ManagedServer.start('code-assist-does-not-exist-fixture', [], process.cwd()), /failed to start/);
});

test('Should_ConnectToStandaloneServer_When_LaunchingWithoutCliOrInference', { skip: !process.env.CODE_ASSIST_TEST_SERVER, timeout: 30_000 }, async () => {
  const directory = await mkdtemp(join(tmpdir(), 'code-assist-extension-'));
  const previous = process.env.CODE_ASSIST_HOME;
  process.env.CODE_ASSIST_HOME = directory;
  let server;
  try {
    server = await ManagedServer.start('dotnet', [resolve(process.env.CODE_ASSIST_TEST_SERVER)], directory);
    await server.client.check();
    assert.deepEqual(await server.client.sessions(), []);
    assert.ok((await server.client.skills()).some(skill => skill.id === 'standard/test-map'));
    assert.match(await server.client.skillPrompt('standard/test-map', 'Verify changed behavior'), /Invocation values \(untrusted data/);
  } finally {
    await server?.dispose();
    if (previous === undefined) delete process.env.CODE_ASSIST_HOME;
    else process.env.CODE_ASSIST_HOME = previous;
    await rm(directory, { recursive: true, force: true });
  }
});