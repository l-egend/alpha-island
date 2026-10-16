import test from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import { once } from 'node:events';
import { mkdtemp, writeFile, readFile, readdir, rm } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { handleHook, validateConfig, loadConfig, redact, inScope } from '../../examples/CodexHooks/hook.mjs';

const repo = fileURLToPath(new URL('../../', import.meta.url));
const adapter = path.join(repo, 'examples/CodexHooks/hook.mjs');
const hook = (event = 'PermissionRequest', extra = {}) => ({ session_id: 'codex-test', cwd: repo,
  hook_event_name: event, tool_name: 'Bash', tool_input: { command: 'echo connection-test' }, ...extra });

async function bridge(t, response) {
  const root = await mkdtemp(path.join(os.tmpdir(), 'alpha-codex-'));
  const calls = [];
  const server = http.createServer(async (req, res) => {
    let raw = ''; for await (const chunk of req) raw += chunk;
    const body = raw ? JSON.parse(raw) : undefined; calls.push({ url: req.url, method: req.method, body });
    res.setHeader('Content-Type', 'application/json');
    if (req.url === '/health') res.end(JSON.stringify({ service: 'alpha-island-agent-bridge', statusReporting: true, apiDecisions: false }));
    else if (req.url === '/approval') {
      const value = typeof response === 'function' ? await response(body, req) : response;
      res.end(JSON.stringify({ id: body.id, ...value }));
    } else res.end('{}');
  });
  server.listen(0, '127.0.0.1'); await once(server, 'listening');
  const config = { endpoint: `http://127.0.0.1:${server.address().port}/`, projectRoot: repo, runtimeDir: root, timeoutSec: 5, approvalMode: 'island' };
  t.after(async () => { server.closeAllConnections(); await new Promise(resolve => server.close(resolve)); await rm(root, { recursive: true, force: true }); });
  return { config, calls, root, server };
}

test('all-project status works across drive roots, while scoped mode and native approval remain intact', async t => {
  const {config,calls}=await bridge(t,{});
  const global={...config,projectRoot:'',allProjects:true,approvalMode:'native'};
  for(const cwd of process.platform==='win32'?['C:\\another-project','Z:\\项目\\work','\\\\?\\E:\\workspace']:['/another-project','/mnt/project']) {
    await handleHook(hook('UserPromptSubmit',{cwd}),global);
    await handleHook(hook('Stop',{cwd}),global);
    assert.deepEqual(await handleHook(hook('PermissionRequest',{cwd}),global),{});
  }
  assert.ok(calls.some(c=>c.url==='/status'&&c.body.state==='thinking'));
  assert.equal(calls.filter(c=>c.url==='/approval').length,0);
  const count=calls.length;
  await handleHook(hook('UserPromptSubmit',{cwd:'relative'}),global);
  await handleHook(hook('UserPromptSubmit',{cwd:path.parse(repo).root}),config);
  assert.equal(calls.length,count);
  assert.throws(()=>validateConfig({...global,allProjects:'true'}));
});

for (const decidedBy of ['mouse', 'key']) test(`Codex native PermissionRequest accepts ${decidedBy} once`, async t => {
  const { config, calls, root } = await bridge(t, { approved: true, timedOut: false, decidedBy });
  assert.equal((await handleHook(hook(), config)).hookSpecificOutput.decision.behavior, 'allow');
  assert.equal(calls.filter(c => c.url === '/approval').length, 1);
  assert.equal(calls.find(c => c.url === '/approval').body.tool, 'Codex · Bash');
  assert.equal(calls.find(c => c.url === '/status').body.state, 'tool-wait');
  assert.equal((await readdir(root)).filter(p => p.includes('approval-')).length, 0);
});

for (const value of [
  { approved: false, timedOut: false, decidedBy: 'mouse' },
  { approved: true, timedOut: true, decidedBy: 'key' },
  { approved: true, timedOut: false, decidedBy: 'api' },
  { approved: true, timedOut: false, decidedBy: 'debug-auto' },
  { id: 'another-request', approved: true, timedOut: false, decidedBy: 'mouse' },
]) test(`Reject untrusted decision ${JSON.stringify(value)}`, async t => {
  const { config } = await bridge(t, value);
  assert.equal((await handleHook(hook(), config)).hookSpecificOutput.decision.behavior, 'deny');
});

test('Offline island preserves native Codex approval', async t => {
  const { config, server } = await bridge(t, {});
  await new Promise(resolve => server.close(resolve));
  assert.deepEqual(await handleHook(hook(), config), {});
});

test('Native approval mirrors waiting status without submitting or deciding an island request', async t => {
  const { config, calls, root } = await bridge(t, { approved: true, timedOut: false, decidedBy: 'mouse' });
  config.approvalMode = 'native';
  const output = await handleHook(hook('PermissionRequest', {
    tool_input: { token: 'PRIVATE_NATIVE_TOKEN', command: 'PRIVATE_NATIVE_COMMAND' },
  }), config);
  assert.deepEqual(output, {});
  assert.deepEqual(calls.map(c => c.url), ['/status']);
  assert.equal(calls[0].body.state, 'tool-wait');
  assert.equal(calls[0].body.detail, 'Codex · 请在对话中审批');
  assert.ok(!JSON.stringify(calls).includes('PRIVATE_NATIVE'));
  assert.deepEqual((await readdir(root)).filter(name => name !== 'status-events'), []);
  assert.deepEqual(await readdir(path.join(root, 'status-events')), [], 'acknowledged events leave no pending payloads');
  await handleHook(hook('PostToolUse'), config);
  assert.equal(calls.at(-1).body.state, 'thinking');
  await handleHook(hook('Stop'), config);
  assert.equal(calls.at(-1).body.state, 'idle');
});

test('Native approval still reaches Codex when the island is unavailable', async t => {
  const { config, server } = await bridge(t, {});
  config.approvalMode = 'native';
  await new Promise(resolve => server.close(resolve));
  assert.deepEqual(await handleHook(hook(), config), {});
});

test('Native approval is the default and invalid modes are rejected', () => {
  const config = { projectRoot: repo, runtimeDir: repo };
  assert.equal(validateConfig(config).approvalMode, 'native');
  assert.throws(() => validateConfig({ ...config, approvalMode: 'both' }));
});

test('Windows extended paths retain project scope without accepting sibling projects', { skip: process.platform !== 'win32' }, () => {
  assert.equal(inScope('\\\\?\\X:\\FixtureWorkspace\\alpha-island', 'X:\\FixtureWorkspace'), true);
  assert.equal(inScope('X:\\FixtureWorkspace\\alpha-island', '\\\\?\\X:\\FixtureWorkspace'), true);
  assert.equal(inScope('\\\\?\\X:\\FixtureWorkspace-other', 'X:\\FixtureWorkspace'), false);
  assert.equal(inScope('\\\\?\\UNC\\server\\share\\project\\src', '\\\\server\\share\\project'), true);
});

test('State hooks ignore prompts, transcripts and tool responses; project scope is enforced', async t => {
  const { config, calls } = await bridge(t, {});
  for (const event of ['SessionStart', 'UserPromptSubmit', 'PreToolUse', 'PostToolUse', 'PreCompact', 'PostCompact', 'Stop', 'Interrupt', 'SessionEnd']) {
    assert.deepEqual(await handleHook(hook(event, { prompt: 'PRIVATE_CHAT', last_assistant_message: 'PRIVATE_REPLY', tool_response: 'PRIVATE_OUTPUT', transcript_path: 'PRIVATE_TRANSCRIPT' }), config), {});
  }
  assert.deepEqual(calls.filter(c => c.url === '/status').map(c => c.body.state),
    ['idle', 'thinking', 'working', 'thinking', 'thinking', 'thinking', 'idle', 'idle', 'idle']);
  assert.ok(!JSON.stringify(calls).includes('PRIVATE_'));
  assert.ok(calls.filter(c => c.url === '/status').every(c => c.body.reporterPid === process.pid));
  const count = calls.length;
  assert.deepEqual(await handleHook(hook('PermissionRequest', { cwd: path.resolve(repo, '..', 'unrelated-project') }), config), {});
  assert.equal(calls.length, count);
});

test('Approval arguments redact credential fields and shell flags', async t => {
  const { config, calls } = await bridge(t, { approved: false });
  await handleHook(hook('PermissionRequest', { tool_input: { token: 'PRIVATE_A', nested: { apiKey: 'PRIVATE_B' },
    command: 'cmd --password "PRIVATE_C SPACE" --token PRIVATE_D secret=PRIVATE_E Authorization=Bearer PRIVATE_F' } }), config);
  const text = JSON.stringify(calls);
  for (const secret of ['PRIVATE_A', 'PRIVATE_B', 'PRIVATE_C', 'PRIVATE_D', 'PRIVATE_E', 'PRIVATE_F']) assert.ok(!text.includes(secret), secret);
  assert.ok(text.includes('已隐藏'));
  assert.ok(JSON.stringify(redact({ deep: Array(40).fill('a') })).length < 250);
});

test('Interrupt cancels the session approval and clears only its state', async t => {
  let received; const ready = new Promise(resolve => received = resolve);
  let finish; const result = new Promise(resolve => finish = resolve);
  const { config, calls } = await bridge(t, async body => { received(body.id); return result; });
  const pending = handleHook(hook(), config);
  const id = await ready;
  await handleHook(hook('Interrupt'), config);
  assert.ok(calls.some(c => c.method === 'DELETE' && c.url === '/approval/' + id));
  finish({ approved: false, timedOut: false, decidedBy: 'client' });
  assert.equal((await pending).hookSpecificOutput.decision.behavior, 'deny');
  assert.equal(calls.filter(c => c.url === '/status').at(-1).body.state, 'idle');
});

test('Loopback validation blocks remote and embedded credentials', () => {
  for (const endpoint of ['https://127.0.0.1/', 'http://example.com/', 'http://u:p@localhost/', 'http://127.0.0.1/path', 'http://127.0.0.1/?token=abc'])
    assert.throws(() => validateConfig({ endpoint, projectRoot: repo, runtimeDir: repo }));
});

test('Event status has a bounded lease and Stop clears it without a background process', async t => {
  const { config, calls, root } = await bridge(t, {});
  await handleHook(hook('UserPromptSubmit'), config, { background: true });
  assert.equal(calls.at(-1).body.state, 'thinking');
  assert.equal(calls.at(-1).body.ttlSec, 300);
  await handleHook(hook('SessionStart', { source: 'compact' }), config);
  assert.equal(calls.at(-1).body.state, 'thinking');
  await handleHook(hook('Stop'), config, { background: true });
  assert.equal(calls.at(-1).body.state, 'idle');
  assert.deepEqual((await readdir(root)).filter(name => name !== 'status-events'), []);
  assert.deepEqual(await readdir(path.join(root, 'status-events')), [], 'acknowledged events leave no pending payloads');
});

test('Configuration pointer preserves existing trusted hook commands and rejects chains', async t => {
  const { root, config, calls } = await bridge(t, {});
  const central = path.join(root, 'codex-hooks.json');
  const legacy = path.join(root, 'legacy-config.json');
  await writeFile(central, JSON.stringify(config));
  await writeFile(legacy, JSON.stringify({ configPath: central }));
  assert.deepEqual(await loadConfig(legacy), validateConfig(config));
  const child = spawn(process.execPath, [adapter, '--config', legacy], { windowsHide: true, stdio: 'pipe' });
  let result = ''; child.stdout.on('data', c => result += c); child.stderr.resume();
  child.stdin.end(JSON.stringify(hook('Stop')));
  const [code] = await once(child, 'exit'); assert.equal(code, 0); assert.deepEqual(JSON.parse(result), {});
  assert.equal(calls.at(-1).body.state, 'idle');
  await writeFile(legacy, JSON.stringify({ configPath: 'relative.json' }));
  await assert.rejects(loadConfig(legacy));
  await writeFile(legacy, JSON.stringify({ configPath: legacy }));
  await assert.rejects(loadConfig(legacy));
  await writeFile(legacy, JSON.stringify({ configPath: central }));
  await writeFile(central, JSON.stringify({ configPath: legacy }));
  await assert.rejects(loadConfig(legacy));
});

test('Installer is repeatable and preserves other Codex hook definitions', { timeout: 20000 }, async t => {
  const { root, config, calls } = await bridge(t, {});
  const hooksPath = path.join(root, 'hooks.json');
  const settingsDirectory = path.join(root, 'island-config');
  const settingsPath = path.join(settingsDirectory, 'codex-hooks.json');
  await writeFile(hooksPath, JSON.stringify({ description: 'Keep this', hooks: {
    Stop: [{ matcher: 'old', hooks: [{ type: 'command', command: 'echo existing', timeout: 3 }] }],
  } }));
  const install = async (remove, mode = 'native') => {
    const child = spawn('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', path.join(repo, 'examples/CodexHooks/install.ps1'),
      '-NodePath', process.execPath, '-CodexDirectory', root, '-ConfigDirectory', settingsDirectory, '-ProjectRoot', repo, '-ApprovalMode', mode, ...(remove ? ['-Remove'] : [])], { windowsHide: true, stdio: 'pipe' });
    let stderr = ''; child.stderr.on('data', c => stderr += c); child.stdout.resume();
    const [code] = await once(child, 'exit'); assert.equal(code, 0, stderr);
  };
  await install(false); await install(false);
  const installed = JSON.parse((await readFile(hooksPath, 'utf8')).replace(/^\uFEFF/, ''));
  assert.ok(!(await readFile(hooksPath, 'utf8')).startsWith('\uFEFF'), 'Codex requires BOM-free hooks JSON');
  assert.equal(installed.description, 'Keep this');
  assert.equal(installed.hooks.Stop.length, 2);
  assert.equal(installed.hooks.PermissionRequest.length, 1);
  assert.equal(installed.hooks.PermissionRequest[0].hooks[0].timeout, 5);
  assert.equal((await loadConfig(settingsPath)).approvalMode, 'native');
  assert.ok(installed.hooks.PermissionRequest[0].hooks[0].command.includes(settingsPath));
  assert.ok(!JSON.stringify(installed).includes('bypass-hook-trust'));
  await writeFile(settingsPath, JSON.stringify(config));
  function startHook(event) {
    const handler = installed.hooks[event].flatMap(group => group.hooks).find(h => h.statusMessage?.startsWith('Alpha Island /'));
    const child = spawn('cmd.exe', ['/d', '/s', '/c', handler.command],
      { windowsHide: true, windowsVerbatimArguments: true, stdio: 'pipe' });
    let output = ''; let error = ''; child.stdout.on('data', c => output += c); child.stderr.on('data', c => error += c);
    child.stdin.end(JSON.stringify(hook(event)));
    const done = once(child, 'exit').then(([code]) => { assert.equal(code, 0, error); assert.deepEqual(JSON.parse(output.trim()), {}); });
    return { child, done };
  }
  await startHook('SessionStart').done;
  assert.ok(calls.some(c => c.url === '/status' && c.body.source === 'codex'));
  await startHook('UserPromptSubmit').done;
  assert.ok(calls.some(c => c.url === '/status' && c.body.state === 'thinking'), 'The real hook command must receive its stdin event');
  await startHook('Stop').done;
  assert.equal(calls.at(-1).body.state, 'idle');
  await install(false, 'island');
  const islandInstalled = JSON.parse((await readFile(hooksPath, 'utf8')).replace(/^\uFEFF/, ''));
  assert.equal(islandInstalled.hooks.PermissionRequest[0].hooks[0].timeout, 130);
  assert.equal((await loadConfig(settingsPath)).approvalMode, 'island');
  await install(true);
  const remaining = JSON.parse((await readFile(hooksPath, 'utf8')).replace(/^\uFEFF/, ''));
  assert.deepEqual(remaining.hooks, { Stop: [{ matcher: 'old', hooks: [{ type: 'command', command: 'echo existing', timeout: 3 }] }] });
});

test('native turn IDs follow execution metadata without forwarding transcript or content', async t => {
  const {config,calls,root}=await bridge(t,{});
  const transcript=path.join(root,'native-history.jsonl');
  await writeFile(transcript,JSON.stringify({type:'session_meta',payload:{id:'native-thread',session_id:'frontend-thread'}})+'\n'+
    JSON.stringify({type:'message',payload:{text:'private-message-sentinel'}}));
  const input=hook('UserPromptSubmit',{session_id:'frontend-thread',turn_id:'native-turn',transcript_path:transcript});
  await handleHook(input,config);
  let body=calls.find(c=>c.url==='/status').body;
  assert.equal(body.nativeThreadId,'native-thread'); assert.equal(body.nativeTurnId,'native-turn'); assert.equal(body.beginsActivity,true);
  assert.ok(!JSON.stringify(body).includes('private-message-sentinel')); assert.ok(!JSON.stringify(body).includes(transcript));
  await handleHook({...input,hook_event_name:'PreToolUse'},config);
  body=calls.filter(c=>c.url==='/status').at(-1).body;
  assert.equal(body.state,'working'); assert.equal(body.nativeTurnId,'native-turn');
  await handleHook(hook('UserPromptSubmit',{session_id:'fallback-thread',turn_id:'fallback-turn'}),config);
  body=calls.filter(c=>c.url==='/status').at(-1).body;
  assert.equal(body.nativeThreadId,'fallback-thread'); assert.equal(body.nativeTurnId,'fallback-turn');
  await handleHook(hook('UserPromptSubmit',{turn_id:'invalid/task-id'}),config);
  assert.equal(calls.filter(c=>c.url==='/status').at(-1).body.nativeTurnId,undefined);
});
test('lost lifecycle deliveries remain recoverable without recording prompts or tool content', async t => {
  const {config,root,server}=await bridge(t,{});
  await new Promise(resolve=>server.close(resolve));
  await handleHook(hook('UserPromptSubmit',{turn_id:'recover-turn',prompt:'private-recovery-sentinel'}),config);
  const directory=path.join(root,'status-events');
  const files=await readdir(directory).catch(()=>[]);
  assert.equal(files.filter(name=>name.endsWith('.json')).length,1,'a lost task-start must have a durable local recovery event');
  const text=await readFile(path.join(directory,files[0]),'utf8');
  const event=JSON.parse(text);
  assert.equal(event.payload.state,'thinking');
  assert.equal(event.payload.nativeTurnId,'recover-turn');
  assert.ok(!text.includes('private-recovery-sentinel'));
  assert.ok(!text.includes('tool_input'));
  await handleHook(hook('Stop',{turn_id:'recover-turn'}),config);
  const pending=await Promise.all((await readdir(directory)).filter(n=>n.endsWith('.json')).map(async n=>JSON.parse(await readFile(path.join(directory,n),'utf8'))));
  assert.ok(pending.some(e=>e.payload.state==='idle'),'a lost completion must be recoverable too');
});
test('offline status recovery storage is bounded and keeps the newest task', async t => {
  const {config,root,server}=await bridge(t,{});
  await new Promise(resolve=>server.close(resolve));
  for(let i=0;i<132;i++) await handleHook(hook('UserPromptSubmit',{turn_id:'bounded-turn-'+i}),config);
  const directory=path.join(root,'status-events');
  const files=(await readdir(directory)).filter(name=>name.endsWith('.json'));
  assert.ok(files.length<=128,'offline hooks cannot accumulate unbounded disk cache');
  const events=await Promise.all(files.map(async name=>JSON.parse(await readFile(path.join(directory,name),'utf8'))));
  assert.ok(events.some(e=>e.payload.nativeTurnId==='bounded-turn-131'));
});