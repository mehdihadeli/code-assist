import { mkdtemp, mkdir, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { runTests } from '@vscode/test-electron';

const directory = await mkdtemp(join(tmpdir(), 'code-assist-vscode-host-'));
const workspace = join(directory, 'workspace');
await mkdir(workspace);
try {
  await runTests({
    extensionDevelopmentPath: fileURLToPath(new URL('../../', import.meta.url)),
    extensionTestsPath: fileURLToPath(new URL('./index.cjs', import.meta.url)),
    ...(process.env.VSCODE_EXECUTABLE_PATH ? { vscodeExecutablePath: process.env.VSCODE_EXECUTABLE_PATH } : {}),
    launchArgs: [workspace, '--disable-extensions', '--disable-workspace-trust', '--skip-welcome', '--skip-release-notes', '--user-data-dir', join(directory, 'profile'), '--extensions-dir', join(directory, 'extensions')],
  });
} finally { await rm(directory, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 }); }