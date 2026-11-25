import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { once } from 'node:events';
import { test } from 'node:test';
import { CodeAssistClient, serverAddress } from '../dist/client.js';

const token = 'fixture-token-not-for-production-123456789';
const sessionId = 'c2b11099-e3b4-4d53-a26e-55795775979a';
const turnId = '962b4eba-02ef-4e15-8a57-3a8b6206eab7';
const event = (sequence, kind, text = null) => `data: ${JSON.stringify({ turnId, sequence, kind, text })}\n\n`;

async function fixture(handler, run) {
  const server = createServer(handler);
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  const client = new CodeAssistClient(`http://127.0.0.1:${server.address().port}`, token);
  try { await run(client); }
  finally { client.dispose(); server.closeAllConnections(); await new Promise(resolve => server.close(resolve)); }
}

test('rejects remote, credential-bearing and path-bearing origins', () => {
  for (const address of ['https://remote.test', 'http://user:password@localhost', 'http://localhost/path', 'http://localhost/?token=secret', 'file:///tmp/server'])
    assert.throws(() => serverAddress(address));
  assert.equal(serverAddress('http://127.0.0.1:8080/'), 'http://127.0.0.1:8080');
});

test('authenticates and verifies server identity and protocol', async () => {
  await fixture((request, response) => {
    assert.equal(request.headers.authorization, `Bearer ${token}`);
    response.end(JSON.stringify({ application: 'code-assist', protocol: '1', status: 'ready' }));
  }, client => client.check());
  await fixture((_, response) => response.end('{"application":"other","protocol":"1","status":"ready"}'), async client => {
    await assert.rejects(client.check(), /incompatible/);
  });
});

test('creates and lists validated sessions', async () => {
  await fixture((request, response) => response.end(JSON.stringify(request.method === 'POST'
    ? { id: sessionId, model: 'test/chat', running: false, messageCount: 0 }
    : [{ id: sessionId, model: 'test/chat', running: false, messageCount: 0 }])) , async client => {
    assert.equal((await client.create()).id, sessionId);
    assert.equal((await client.sessions()).length, 1);
  });
});

test('lists bounded text-only plugin commands and expands explicit input', async () => {
  await fixture(async (request, response) => {
    if (request.url === '/v1/plugins/commands') {
      response.end(JSON.stringify([{ name: 'team.review/inspect', kind: 'prompt', description: 'Inspect', descriptionBytes: 30, contentBytes: 40 }]));
      return;
    }
    assert.equal(request.url, '/v1/plugins/expand');
    let body = '';
    for await (const chunk of request) body += chunk;
    assert.deepEqual(JSON.parse(body), { name: 'team.review/inspect', input: 'task' });
    response.end(JSON.stringify({ text: 'Review task' }));
  }, async client => {
    assert.equal((await client.plugins())[0].name, 'team.review/inspect');
    assert.equal(await client.pluginPrompt('team.review/inspect', 'task'), 'Review task');
    await assert.rejects(client.pluginPrompt('../escape', 'task'), /Invalid plugin/);
  });
});

test('lists human skills and sends structured values without implicit file capture', async () => {
  await fixture(async (request, response) => {
    if (request.url === '/v1/skills/commands') {
      response.end(JSON.stringify([{ id: 'project/review', description: 'Review', origin: 'project', parameters: ['target'] }])); return;
    }
    assert.equal(request.url, '/v1/skills/expand');
    let body = '';
    for await (const chunk of request) body += chunk;
    assert.deepEqual(JSON.parse(body), { id: 'project/review', input: 'task', arguments: ['changes'] });
    response.end(JSON.stringify({ text: 'Review changes' }));
  }, async client => {
    assert.equal((await client.skills())[0].id, 'project/review');
    assert.equal(await client.skillPrompt('project/review', 'task', ['changes']), 'Review changes');
    await assert.rejects(client.skillPrompt('../escape', ''), /Invalid skill/);
    await assert.rejects(client.skillPrompt('review', 'x'.repeat(4001)), /Invalid skill/);
  });
  await fixture((_, response) => response.end(JSON.stringify([{ id: 'review', description: 'Review', origin: 'project', parameters: ['bad-name'] }])), async client => {
    await assert.rejects(client.skills(), /Invalid skill/);
  });
});

test('validates session-owned connector control and untrusted prompt responses', async () => {
  const actions = [];
  await fixture(async (request, response) => {
    actions.push({ method: request.method, path: request.url });
    if (request.method === 'DELETE') { response.writeHead(204); response.end(); return; }
    if (request.url.endsWith('/connectors')) { response.end(JSON.stringify([{ server: 'fixture', connected: true, tools: 1 }])); return; }
    if (request.url.endsWith('/connection')) {
      let body = ''; for await (const chunk of request) body += chunk;
      assert.deepEqual(JSON.parse(body), { confirmed: true });
      response.end(JSON.stringify({ server: 'fixture', connected: true, tools: 1 })); return;
    }
    if (request.url.endsWith('/prompts')) { response.end(JSON.stringify([{ name: 'inspect', arguments: [{ name: 'target', required: true }] }])); return; }
    response.end(JSON.stringify({ messages: [{ role: 'user', content: { text: 'Untrusted guidance' } }] }));
  }, async client => {
    assert.equal((await client.connectors(sessionId))[0].server, 'fixture');
    await client.connectorChange(sessionId, 'fixture', 'connect');
    assert.equal((await client.connectorPrompts(sessionId, 'fixture'))[0].arguments[0].required, true);
    assert.match(await client.connectorPrompt(sessionId, 'fixture', 'inspect', { target: 'workspace' }), /Untrusted guidance/);
    await client.connectorChange(sessionId, 'fixture', 'disconnect');
    await client.connectorChange(sessionId, 'fixture', 'logout');
    await assert.rejects(client.connectorChange(sessionId, '../escape', 'connect'), /Invalid connector/);
    assert.equal(actions.filter(item => item.method === 'DELETE').length, 2);
  });
});

test('rejects executable plugin entries and oversized expanded guidance', async () => {
  await fixture((_, response) => response.end(JSON.stringify([{ name: 'team.review/inspect', kind: 'hook', description: 'Inspect', descriptionBytes: 30, contentBytes: 40 }])) , async client => {
    await assert.rejects(client.plugins(), /Invalid plugin/);
  });
  await fixture((_, response) => response.end(JSON.stringify({ text: 'x'.repeat(65_537) })), async client => {
    await assert.rejects(client.pluginPrompt('team.review/inspect', 'task'), /input limit/);
  });
});

test('decodes split UTF-8 SSE frames and completes only on terminal success', async () => {
  await fixture((_, response) => {
    response.setHeader('content-type', 'text/event-stream');
    const bytes = Buffer.from(event(1, 'turn.started') + event(2, 'text.delta', 'hello\nworld\u{1f642}') + event(3, 'turn.completed'));
    for (let offset = 0; offset < bytes.length; offset += 7) response.write(bytes.subarray(offset, offset + 7));
    response.end();
  }, async client => {
    let text = '';
    await client.stream(sessionId, 'hi', delta => { text += delta; });
    assert.equal(text, 'hello\nworld\u{1f642}');
  });
});

test('preserves bounded hosted activity without treating it as text or executing it', async () => {
  await fixture((_, response) => {
    response.setHeader('content-type', 'text/event-stream');
    response.end(event(1, 'turn.started') + event(2, 'hosted.call', JSON.stringify({ callId: 'fixture-call', name: 'lookup' })) +
      event(3, 'hosted.result', JSON.stringify({ callId: 'fixture-call', outputs: [{ text: 'data' }] })) + event(4, 'text.delta', 'Done') + event(5, 'turn.completed'));
  }, async client => {
    let text = '';
    const activity = [];
    await client.stream(sessionId, 'hi', delta => { text += delta; }, undefined, (kind, content) => activity.push({ kind, content }));
    assert.equal(text, 'Done');
    assert.deepEqual(activity.map(item => item.kind), ['hosted.call', 'hosted.result']);
    assert.equal(activity[0].content.name, 'lookup');
  });
});

test('rejects malformed hosted activity even without an activity consumer', async () => {
  for (const payload of ['not JSON', '[]']) {
    await fixture((_, response) => {
      response.setHeader('content-type', 'text/event-stream');
      response.end(event(1, 'turn.started') + event(2, 'hosted.call', payload) + event(3, 'turn.completed'));
    }, async client => { await assert.rejects(client.stream(sessionId, 'hi', () => {})); });
  }
});

test('rejects failed, truncated and out-of-order turns without retrying', async () => {
  for (const frames of [event(1, 'turn.started') + event(2, 'turn.failed', token), event(1, 'turn.started'), event(2, 'turn.started')]) {
    let calls = 0;
    await fixture((_, response) => { calls++; response.setHeader('content-type', 'text/event-stream'); response.end(frames); }, async client => {
      await assert.rejects(client.stream(sessionId, 'hi', () => {}), error => !error.message.includes(token));
      assert.equal(calls, 1);
    });
  }
});

test('does not follow redirects or disclose server error bodies', async () => {
  for (const status of [302, 401, 502]) {
    await fixture((_, response) => { response.writeHead(status, { Location: 'http://remote.test' }); response.end(token); }, async client => {
      await assert.rejects(client.check(), error => !error.message.includes(token));
    });
  }
});

test('cancels a pending stream when caller aborts', async () => {
  let started;
  const ready = new Promise(resolve => { started = resolve; });
  await fixture((_, response) => { response.setHeader('content-type', 'text/event-stream'); response.write(event(1, 'turn.started')); started(); }, async client => {
    const controller = new AbortController();
    const running = client.stream(sessionId, 'hi', () => {}, controller.signal);
    await ready;
    controller.abort();
    await assert.rejects(running, /canceled/);
  });
});

test('bounds generated text and rejects invalid session identifiers', async () => {
  await fixture((_, response) => {
    response.setHeader('content-type', 'text/event-stream');
    response.end(event(1, 'turn.started') + event(2, 'text.delta', 'x'.repeat(262_145)) + event(3, 'turn.completed'));
  }, async client => {
    await assert.rejects(client.stream(sessionId, 'hi', () => {}), /text limit/);
    await assert.rejects(client.cancel('../other-session'), /Invalid session/);
  });
});