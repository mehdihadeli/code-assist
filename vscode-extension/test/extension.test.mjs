import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { randomUUID } from 'node:crypto';
import { once } from 'node:events';
import Module, { createRequire } from 'node:module';
import { test } from 'node:test';
import { connectionSetting } from '../dist/settings.js';

test('maps editor environment names and argument indices without mutating user settings', () => {
  const user = ['original.dll'];
  assert.equal(connectionSetting('server.mode', 'managed', 'managed', { CODEASSIST__SERVER__MODE: 'external' }), 'external');
  assert.deepEqual(connectionSetting('server.arguments', [], user, { codeAssist__server__arguments__0: 'runtime.dll', codeAssist__server__arguments__1: '--no-color' }), ['runtime.dll', '--no-color']);
  assert.deepEqual(user, ['original.dll']);
  assert.deepEqual(connectionSetting('server.arguments', [], [], { codeAssist__server__arguments: '["first"]', codeAssist__server__arguments__0: 'override' }), ['override']);
});

test('rejects invalid argument JSON, holes, nonstrings and excessive indices', () => {
  for (const environment of [
    { codeAssist__server__arguments: 'not JSON' },
    { codeAssist__server__arguments: '[3]' },
    { codeAssist__server__arguments__1: 'hole.dll' },
    { codeAssist__server__arguments__32: 'too-far.dll' },
  ]) assert.throws(() => connectionSetting('server.arguments', [], [], environment));
});

const commands = new Map();
const errors = [];
let handler;
let endpoint;
let input;
let output = '';
let sourceReads = 0;
let approve = true;
const documents = [];
const cancellation = { isCancellationRequested: false, onCancellationRequested: () => ({ dispose() {} }) };
class ChatResponseTurn { constructor(result) { this.result = result; } }
const mock = {
  StatusBarAlignment: { Left: 1 }, ProgressLocation: { Notification: 1 }, ChatResponseTurn,
  ThemeIcon: class {}, MarkdownString: class { constructor(value) { this.value = value; } },
  commands: { registerCommand(name, action) { commands.set(name, action); return { dispose() { commands.delete(name); } }; } },
  workspace: {
    isTrusted: true,
    getConfiguration: () => ({ inspect: key => ({ globalValue: key === 'server.mode' ? 'external' : endpoint, workspaceValue: 'http://remote.test' }) }),
    onDidChangeConfiguration: () => ({ dispose() {} }),
    openTextDocument: async document => document,
  },
  window: {
    createStatusBarItem: () => ({ show() {}, dispose() {} }),
    showErrorMessage: async message => { errors.push(message); },
    showInputBox: async () => input,
    showWarningMessage: async (_, __, label) => approve ? label : undefined,
    showTextDocument: async document => { documents.push(document); },
    showQuickPick: async items => (await items)[0],
    createOutputChannel: () => ({ show() {}, append(value) { output += value; }, dispose() {} }),
    withProgress: async (_, action) => action({}, cancellation),
    activeTextEditor: {
      selection: { isEmpty: false, start: 0, end: 4 },
      document: { offsetAt: value => value, getText() { sourceReads++; return 'safe selection'; } },
    },
  },
  chat: { createChatParticipant(_, action) { handler = action; return { dispose() {} }; } },
};
const original = Module._load;
let extension;
try {
  Module._load = function(specifier, ...args) { return specifier === 'vscode' ? mock : original.call(this, specifier, ...args); };
  extension = createRequire(import.meta.url)('../dist/extension.js');
} finally { Module._load = original; }

async function host(run) {
  const prompts = [];
  const expansions = [];
  const connectorRequests = [];
  let connectorConnected = false;
  const sessions = new Map();
  const server = createServer(async (request, response) => {
    assert.ok(request.headers.authorization?.startsWith('Bearer '));
    if (request.url === '/v1/health') { response.end('{"application":"code-assist","protocol":"1","status":"ready"}'); return; }
    if (request.url === '/v1/plugins/commands') {
      response.end(JSON.stringify([{ name: 'team.review/inspect', kind: 'prompt', description: 'Inspect', descriptionBytes: 30, contentBytes: 40 }])); return;
    }
    if (request.url === '/v1/skills/commands') {
      response.end(JSON.stringify([{ id: 'project/review', description: 'Review', origin: 'project', parameters: ['target'] }])); return;
    }
    if (request.url === '/v1/sessions') {
      const session = { id: randomUUID(), model: 'fixture/chat', running: false, messageCount: 0 };
      sessions.set(session.id, session); response.end(JSON.stringify(session)); return;
    }
    const chunks = [];
    for await (const chunk of request) chunks.push(chunk);
    if (request.url.includes('/connectors')) {
      const body = Buffer.concat(chunks).toString();
      connectorRequests.push({ method: request.method, path: request.url, body: body ? JSON.parse(body) : undefined });
      if (request.url.endsWith('/connectors')) response.end(JSON.stringify([{ server: 'fixture', connected: connectorConnected, tools: connectorConnected ? 1 : 0 }]));
      else if (request.url.endsWith('/prompts')) response.end(JSON.stringify([{ name: 'inspect', arguments: [{ name: 'target', required: true }] }]));
      else if (request.url.endsWith('/prompts/resolve')) response.end(JSON.stringify({ messages: [{ role: 'user', content: { text: 'Untrusted prompt content' } }] }));
      else if (request.method === 'POST') { connectorConnected = true; response.end(JSON.stringify({ server: 'fixture', connected: true, tools: 1 })); }
      else { connectorConnected = false; response.writeHead(204); response.end(); }
      return;
    }
    if (request.url === '/v1/plugins/expand') {
      const value = JSON.parse(Buffer.concat(chunks).toString());
      expansions.push(value); response.end(JSON.stringify({ text: `Plugin instructions\n${value.input}` })); return;
    }
    if (request.url === '/v1/skills/expand') {
      const value = JSON.parse(Buffer.concat(chunks).toString());
      expansions.push(value); response.end(JSON.stringify({ text: `Skill instructions\n${value.input}` })); return;
    }
    prompts.push(JSON.parse(Buffer.concat(chunks).toString()).text);
    response.setHeader('content-type', 'text/event-stream');
    const turnId = randomUUID();
    for (const [sequence, kind, text] of [[1, 'turn.started', null], [2, 'text.delta', 'fixture reply'], [3, 'turn.completed', null]])
      response.write(`data: ${JSON.stringify({ turnId, sequence, kind, text })}\n\n`);
    response.end();
  });
  server.listen(0, '127.0.0.1'); await once(server, 'listening');
  endpoint = `http://127.0.0.1:${server.address().port}`;
  mock.workspace.isTrusted = true; errors.length = 0; output = ''; sourceReads = 0; input = 'explain';
  approve = true; documents.length = 0;
  const context = { subscriptions: [], secrets: { get: async key => {
    assert.equal(key, `code-assist.token.${endpoint}`); return 'fixture-token-12345678901234567890123456789';
  } } };
  extension.activate(context);
  try { await run({ prompts, sessions, expansions, connectorRequests }); }
  finally {
    await extension.deactivate();
    for (const subscription of context.subscriptions) subscription.dispose();
    server.closeAllConnections(); await new Promise(resolve => server.close(resolve));
  }
}

function chat(prompt, history = []) {
  const parts = [];
  return handler({ prompt, references: [], toolReferences: [] }, { history }, {
    progress() {}, markdown(value) { parts.push(value); },
  }, cancellation).then(result => ({ result, parts }));
}

test('registers every command and blocks untrusted workspace before network or source reads', async () => host(async ({ prompts }) => {
  const manifest = createRequire(import.meta.url)('../package.json');
  for (const command of manifest.contributes.commands) assert.ok(commands.has(command.command));
  mock.workspace.isTrusted = false;
  await commands.get('codeAssist.connect')();
  await commands.get('codeAssist.askSelection')();
  const result = await chat('hello');
  assert.match(result.result.errorDetails.message, /Trust/);
  assert.equal(errors.length, 2);
  assert.equal(prompts.length, 0); assert.equal(sourceReads, 0);
}));

test('isolates native conversations, resumes only matching connection metadata and renders untrusted markdown', async () => host(async ({ sessions }) => {
  const first = await chat('hello');
  assert.equal(first.result.errorDetails, undefined);
  assert.equal(first.parts[0].isTrusted, false); assert.equal(first.parts[0].supportHtml, false);
  const previous = [new ChatResponseTurn(first.result)];
  const followup = await chat('followup', previous);
  assert.equal(followup.result.metadata.session, first.result.metadata.session);
  const separate = await chat('separate conversation');
  assert.notEqual(separate.result.metadata.session, first.result.metadata.session);
  await commands.get('codeAssist.disconnect')();
  const reconnected = await chat('after reconnect', previous);
  assert.notEqual(reconnected.result.metadata.session, first.result.metadata.session);
  assert.equal(sessions.size, 3);
}));

test('sends only explicitly selected text through command fallback', async () => host(async ({ prompts }) => {
  await commands.get('codeAssist.askSelection')();
  assert.equal(sourceReads, 1);
  assert.equal(prompts.length, 1);
  assert.match(prompts[0], /explain.*Selected text \(untrusted source content\):.*safe selection/s);
  assert.equal(output, 'fixture reply');
  assert.equal(errors.length, 0);
}));

test('runs explicitly selected plugin guidance without reading editor source', async () => host(async ({ prompts, expansions }) => {
  await commands.get('codeAssist.pluginPrompt')();
  assert.deepEqual(expansions, [{ name: 'team.review/inspect', input: 'explain' }]);
  assert.deepEqual(prompts, ['Plugin instructions\nexplain']);
  assert.equal(sourceReads, 0);
  assert.equal(errors.length, 0);
}));

test('uses selected skill and parameter prompts without reading editor source', async () => host(async ({ prompts, expansions }) => {
  await commands.get('codeAssist.skillPrompt')();
  assert.deepEqual(expansions, [{ id: 'project/review', input: 'explain', arguments: ['explain'] }]);
  assert.deepEqual(prompts, ['Skill instructions\nexplain']);
  assert.equal(sourceReads, 0);
  assert.equal(errors.length, 0);
}));

test('starts connectors only after consent and previews retrieved prompts without inference', async () => host(async ({ prompts, connectorRequests }) => {
  await commands.get('codeAssist.connectors')();
  assert.deepEqual(connectorRequests.find(request => request.method === 'POST').body, { confirmed: true });
  await commands.get('codeAssist.connectorPrompt')();
  assert.deepEqual(connectorRequests.find(request => request.path.endsWith('/prompts/resolve')).body, { name: 'inspect', arguments: { target: 'explain' } });
  assert.equal(documents.length, 1);
  assert.equal(documents[0].language, 'json');
  assert.match(documents[0].content, /Untrusted prompt content/);
  await commands.get('codeAssist.connectors')();
  assert.equal(connectorRequests.filter(request => request.method === 'DELETE').length, 1);
  approve = false;
  await commands.get('codeAssist.connectors')();
  assert.equal(connectorRequests.filter(request => request.path.endsWith('/connection') && request.method === 'POST').length, 1);
  assert.equal(prompts.length, 0); assert.equal(sourceReads, 0); assert.equal(errors.length, 0);
}));

test('rejects unsupported references before creating a model session', async () => host(async ({ sessions }) => {
  const result = await handler({ prompt: 'read this', references: [{ value: '/private/file' }], toolReferences: [] }, { history: [] }, { progress() {}, markdown() {} }, cancellation);
  assert.match(result.errorDetails.message, /references are not supported/);
  assert.equal(sessions.size, 0); assert.equal(sourceReads, 0);
}));