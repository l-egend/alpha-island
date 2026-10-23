import test from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import { once } from 'node:events';
import { mkdtemp, readFile, writeFile, rm } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import os from 'node:os';
import path from 'node:path';
import { handleHook as codex } from '../../examples/CodexHooks/hook.mjs';
import { handleHook as antigravity } from '../../examples/AntigravityHooks/hook.mjs';

const key = id => createHash('sha256').update(id).digest('hex').slice(0, 24);
async function fixture(t) {
  const root = await mkdtemp(path.join(os.tmpdir(), 'alpha-agent-state-'));
  const states = [];
  const server = http.createServer(async (request, response) => {
    let raw = ''; for await (const chunk of request) raw += chunk;
    if (request.url === '/status') states.push(JSON.parse(raw));
    response.setHeader('Content-Type', 'application/json'); response.end('{}');
  });
  server.listen(0, '127.0.0.1'); await once(server, 'listening');
  t.after(async () => { server.closeAllConnections(); await new Promise(resolve => server.close(resolve)); await rm(root, { recursive: true, force: true }); });
  return { root, states, config: { endpoint: `http://127.0.0.1:${server.address().port}/`, runtimeDir: root, allProjects: true, approvalMode: 'native' } };
}

test('Codex tool execution reports working instead of thinking', async t => {
  const f = await fixture(t);
  await codex({ session_id: 'task', cwd: f.root, hook_event_name: 'PreToolUse', tool_name: 'Bash' }, f.config);
  assert.equal(f.states.at(-1).state, 'working');
});

test('Antigravity tool and background execution report working', async t => {
  const f = await fixture(t); const config = { ...f.config, approvalMode: 'confirm' };
  await antigravity('PreToolUse', { conversationId: 'task', toolCall: { name: 'read_file', args: {} } }, config);
  assert.equal(f.states.at(-1).state, 'working');
  await antigravity('Stop', { conversationId: 'task', fullyIdle: false }, config);
  assert.equal(f.states.at(-1).state, 'working');
});

test('Codex transport IDs resolve to one native conversation and identify child agents', async t => {
  const f = await fixture(t);
  const transcript = path.join(f.root, 'session.jsonl');
  await writeFile(transcript, JSON.stringify({ type: 'session_meta', payload: { id: 'root-task', source: 'vscode', parent_thread_id: null } }) + '\n');
  for (const id of ['transport-a', 'transport-b'])
    await codex({ session_id: id, cwd: f.root, hook_event_name: 'UserPromptSubmit', transcript_path: transcript, prompt: 'PRIVATE_CHAT' }, f.config);
  assert.deepEqual(f.states.map(item => item.session), [key('root-task'), key('root-task')]);
  await writeFile(transcript, JSON.stringify({ type: 'session_meta', payload: { id: 'child-task', parent_thread_id: 'root-task', source: { subagent: { other: 'spawn' } } } }) + '\n');
  await codex({ session_id: 'child-transport', cwd: f.root, hook_event_name: 'PreToolUse', transcript_path: transcript, tool_name: 'Bash' }, f.config);
  assert.equal(f.states.at(-1).parentSession, key('root-task'));
  assert.equal(f.states.at(-1).session, key('child-task'));
  assert.ok(!JSON.stringify(f.states).includes('PRIVATE_CHAT'));
  assert.ok(!JSON.stringify(f.states).includes(transcript));
});

test('Codex desktop execution threads retain their frontend conversation identity', async t => {
  const f = await fixture(t); const transcript = path.join(f.root, 'desktop.jsonl');
  for (const id of ['execution-a', 'execution-b']) {
    await writeFile(transcript, JSON.stringify({ type: 'session_meta', payload: {
      id, session_id: 'frontend-chat', parent_thread_id: 'frontend-chat', source: { subagent: { other: 'task' } },
    } }) + '\n');
    await codex({ session_id: 'frontend-chat', cwd: f.root, hook_event_name: 'PreToolUse', transcript_path: transcript, tool_name: 'Bash' }, f.config);
  }
  assert.deepEqual(f.states.map(item => item.parentSession), [key('frontend-chat'), key('frontend-chat')]);
  assert.ok(f.states.every(item => item.sessionAlias === undefined));
  assert.deepEqual(f.states.map(item => item.session), [key('execution-a'), key('execution-b')]);
});

test('Codex child prompts do not restart their parent conversation notification', async t => {
  const f = await fixture(t); const transcript = path.join(f.root, 'child.jsonl');
  await writeFile(transcript, JSON.stringify({ type: 'session_meta', payload: {
    id: 'child-task', parent_thread_id: 'root-task', source: { subagent: { other: 'spawn' } },
  } }) + '\n');
  await codex({ session_id: 'child-transport', cwd: f.root, hook_event_name: 'UserPromptSubmit', transcript_path: transcript }, f.config);
  assert.equal(f.states.at(-1).parentSession, key('root-task'));
  assert.equal(f.states.at(-1).beginsActivity, undefined);
  assert.equal(f.states.at(-1).startsRootActivity, undefined);
});

test('Codex desktop frontend submissions begin a new activity even with execution metadata', async t => {
  const f = await fixture(t); const transcript = path.join(f.root, 'desktop-submit.jsonl');
  await writeFile(transcript, JSON.stringify({ type: 'session_meta', payload: {
    id: 'execution-task', session_id: 'frontend-chat', parent_thread_id: 'frontend-chat', source: { subagent: { other: 'task' } },
  } }) + '\n');
  await codex({ session_id: 'frontend-chat', cwd: f.root, hook_event_name: 'UserPromptSubmit', transcript_path: transcript }, f.config);
  assert.equal(f.states.at(-1).parentSession, key('frontend-chat'));
  assert.equal(f.states.at(-1).beginsActivity, true);
  assert.equal(f.states.at(-1).startsRootActivity, undefined);
});
test('Codex session initialization only registers readiness and does not cancel a pending task approval', async t => {
  const f = await fixture(t); const id = 'codex-00000000-0000-4000-8000-000000000000';
  const marker = path.join(f.root, key('task') + '-approval-' + id + '.json');
  await writeFile(marker, JSON.stringify({ id }));
  await codex({ session_id: 'task', cwd: f.root, hook_event_name: 'SessionStart' }, f.config);
  assert.equal(f.states.at(-1).state, 'idle');
  assert.equal(f.states.at(-1).connectOnly, true);
  assert.deepEqual(JSON.parse(await readFile(marker, 'utf8')), { id });
});

test('Codex compact readiness cannot invent a running task', async t => {
  const f = await fixture(t);
  await codex({ session_id: 'task', cwd: f.root, hook_event_name: 'SessionStart', source: 'compact' }, f.config);
  assert.equal(f.states.at(-1).state, 'idle');
  assert.equal(f.states.at(-1).connectOnly, true);
});