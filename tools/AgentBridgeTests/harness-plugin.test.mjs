import { test } from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import { apply, redact } from '../../examples/DeepSeekHarnessPlugin/index.mjs';

async function fixture(options = {}) {
  const statuses = [], requests = [];
  let pendingResponse;
  const server = http.createServer(async (req, res) => {
    const chunks = [];
    for await (const chunk of req) chunks.push(chunk);
    const body = chunks.length ? JSON.parse(Buffer.concat(chunks)) : undefined;
    const respond = data => { res.setHeader('Content-Type', 'application/json'); res.end(JSON.stringify(data)); };
    if (req.url === '/health') return respond({ service: 'alpha-island-agent-bridge', statusReporting: true, apiDecisions: !!options.apiDecisions });
    if (req.url === '/status') { statuses.push(body); return respond({ ok: true }); }
    if (req.method === 'DELETE') { requests.push({ cancel: req.url }); pendingResponse?.end(JSON.stringify({ approved: false })); return respond({ ok: true }); }
    requests.push(body);
    if (options.pending) { pendingResponse = res; return; }
    respond({ approved: options.approved ?? false, decidedBy: options.decidedBy ?? 'mouse', timedOut: options.timedOut ?? false });
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const listeners = new Map(), cleanup = [];
  const ctx = {
    agents: { list: () => [] },
    on: (name, listener, prepend) => { listeners.set(name, listener); if (name === 'approval/request') assert.equal(prepend, true); },
    effect: fn => cleanup.push(fn()),
  };
  apply(ctx, { endpoint: `http://127.0.0.1:${server.address().port}/`, timeoutSec: 5 });
  const agent = { id: 'test-session', status: 'running' };
  const ask = (signal, next = async () => 'original-ui') => listeners.get('approval/request')({ agent, toolName: 'read', callId: 'call', reason: 'Read-only probe', signal }, next);
  return { listeners, statuses, requests, agent, ask,
    close: async () => { for (const dispose of cleanup) await dispose(); server.closeAllConnections(); await new Promise(resolve => server.close(resolve)); } };
}

test('native approval receives only a one-shot user grant', async () => {
  const f = await fixture({ approved: true });
  try {
    f.listeners.get('session/event')({ id: f.agent.id }, { type: 'tool/call', data: { callId: 'call', arguments: '{"path":"probe.txt","apiKey":"do-not-forward"}' } });
    assert.equal(await f.ask(), 'allowed-once');
    assert.equal(f.requests[0].args.path, 'probe.txt');
    assert.equal(f.requests[0].args.apiKey, '[已隐藏]');
    assert.ok(!JSON.stringify(f.requests).includes('do-not-forward'));
  } finally { await f.close(); }
});
test('deny and timeout never grant a tool', async () => {
  for (const options of [{ approved: false }, { approved: false, timedOut: true }, { approved: true, timedOut: true }, { approved: true, decidedBy: 'api' }]) {
    const f = await fixture(options);
    try { assert.equal(await f.ask(), 'rejected'); } finally { await f.close(); }
  }
});
test('insecure API-decision mode retains original Harness approval', async () => {
  const f = await fixture({ apiDecisions: true });
  try { assert.equal(await f.ask(), 'original-ui'); assert.equal(f.requests.length, 0); } finally { await f.close(); }
});
test('agent events show work and release the claim when done', async () => {
  const f = await fixture();
  try {
    f.listeners.get('agent/status')({ agent: f.agent, status: 'running' });
    await new Promise(resolve => setTimeout(resolve, 40));
    f.listeners.get('agent/assistant-stream')({ agent: f.agent, frame: { type: 'chunk', text: 'private chat content' } });
    await new Promise(resolve => setTimeout(resolve, 40));
    f.listeners.get('agent/status')({ agent: f.agent, status: 'idle' });
    await new Promise(resolve => setTimeout(resolve, 40));
    assert.ok(f.statuses.some(s => s.state === 'thinking'));
    assert.ok(f.statuses.some(s => s.state === 'streaming'));
    assert.ok(f.statuses.some(s => s.session !== `p${process.pid}-connection` && s.state === 'idle'));
    assert.ok(!JSON.stringify(f.statuses).includes('private chat content'));
  } finally { await f.close(); }
});
test('aborting a native request withdraws its island approval', async () => {
  const f = await fixture({ pending: true });
  try {
    const controller = new AbortController();
    const result = f.ask(controller.signal);
    for (let i = 0; !f.requests.length && i < 100; i++) await new Promise(resolve => setTimeout(resolve, 10));
    assert.ok(f.requests.length);
    controller.abort();
    assert.equal(await result, 'cancelled');
    for (let i = 0; !f.requests.some(r => r.cancel) && i < 100; i++) await new Promise(resolve => setTimeout(resolve, 10));
    assert.ok(f.requests.some(r => r.cancel));
  } finally { await f.close(); }
});
test('offline island preserves the original approval UI', async () => {
  const listeners = new Map(), cleanup = [];
  apply({ agents: { list: () => [] }, on: (name, fn) => listeners.set(name, fn), effect: fn => cleanup.push(fn()) },
    { endpoint: 'http://127.0.0.1:1/' });
  try {
    assert.equal(await listeners.get('approval/request')({ agent: { id: 'offline' }, toolName: 'read' }, async () => 'original-ui'), 'original-ui');
  } finally { for (const dispose of cleanup) await dispose(); }
});
test('sensitive fields and bearer tokens are redacted', () => {
  assert.deepEqual(redact({ token: 'x', nested: { command: 'token=abc Bearer secret-value' } }),
    { token: '[已隐藏]', nested: { command: 'token=[已隐藏] Bearer [已隐藏]' } });
});
test('remote endpoints are rejected', () => {
  assert.throws(() => apply({}, { endpoint: 'http://example.com/' }), /loopback/);
});
