import { test } from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import { mkdtemp, mkdir, writeFile, readFile, rm } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { handleHook, validateConfig } from '../../examples/ClaudeCodeHooks/hook.mjs';

async function fixture() {
  const received = [];
  const server = http.createServer(async (req, res) => {
    let body = ''; for await (const chunk of req) body += chunk;
    received.push({ route: req.url, body: JSON.parse(body) });
    res.writeHead(200, { 'Content-Type': 'application/json' }); res.end('{"ok":true}');
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  return { received, config: { endpoint: `http://127.0.0.1:${server.address().port}/` },
    close: () => new Promise(resolve => { server.close(resolve); server.closeAllConnections(); }) };
}
const input = (event, session = 'session-one', extra = {}) => ({ session_id: session, hook_event_name: event, ...extra });
function run(file, args, stdin = '', env = {}) {
  return new Promise((resolve, reject) => {
    const child = spawn(file, args, { windowsHide: true, env: { ...process.env, ...env } });
    let stdout = '', stderr = '';
    const timeout = setTimeout(() => { child.kill(); reject(new Error('Child timed out')); }, 15000);
    child.stdout.on('data', b => stdout += b); child.stderr.on('data', b => stderr += b);
    child.on('error', reject); child.on('close', code => { clearTimeout(timeout); resolve({ code, stdout, stderr }); });
    child.stdin.end(stdin);
  });
}

test('conversation lifecycle and compaction remain independent across simultaneous sessions', async () => {
  const f = await fixture(); try {
    for (const [event, session, extra] of [['UserPromptSubmit','a'],['UserPromptSubmit','b'],['PermissionRequest','a'],['PostToolUse','b'],['Stop','a'],['SessionStart','b',{source:'compact'}]])
      assert.deepEqual(await handleHook(input(event, session, extra), f.config), {});
    assert.deepEqual(f.received.map(x => x.body.state), ['thinking','thinking','tool-wait','thinking','idle','thinking']);
    assert.equal(f.received[0].body.session, f.received[4].body.session);
    assert.notEqual(f.received[0].body.session, f.received[1].body.session);
    assert.equal(f.received[5].body.session, f.received[1].body.session);
  } finally { await f.close(); }
});
test('native permission is not approved or denied, and private input never reaches the bridge', async () => {
  const f = await fixture(); try {
    const result = await handleHook(input('PermissionRequest','private-session',{tool_name:'Bash',tool_input:{command:'private-token'},prompt:'private-prompt',transcript_path:'must-not-read'}), f.config);
    assert.deepEqual(result, {}); assert.equal(f.received.length, 1); assert.equal(f.received[0].route, '/status');
    assert.equal(f.received[0].body.state, 'tool-wait'); assert.ok(!JSON.stringify(f.received).includes('private'));
  } finally { await f.close(); }
});
test('child agent events and unknown notifications cannot clear or replace the parent', async () => {
  const f = await fixture(); try {
    for (const event of ['PreToolUse','PostToolUse','Stop','SubagentStop']) await handleHook(input(event, 'a', {agent_id:'child'}), f.config);
    await handleHook(input('Notification','a',{notification_type:'auth_success'}), f.config);
    await handleHook(input('Unknown'), f.config);
    assert.equal(f.received.length, 0);
  } finally { await f.close(); }
});
test('waiting for input, idle notification, API failure, and continued background task are represented accurately', async () => {
  const f = await fixture(); try {
    await handleHook(input('PreToolUse','a',{tool_name:'AskUserQuestion'}),f.config);
    await handleHook(input('Notification','a',{notification_type:'idle_prompt'}),f.config);
    await handleHook(input('StopFailure','a',{error_details:'private'}),f.config);
    await handleHook(input('Stop','b',{background_tasks:[{status:'running'}]}),f.config);
    assert.deepEqual(f.received.map(x => x.body.state),['tool-wait','idle','error','working']);
    assert.equal(f.received[2].body.ttlSec,15);
  } finally { await f.close(); }
});
test('invalid session metadata is ignored and endpoints cannot leave loopback', async () => {
  const f = await fixture(); try {
    await handleHook(input('UserPromptSubmit',''),f.config); await handleHook(input('UserPromptSubmit','a'.repeat(257)),f.config);
    assert.equal(f.received.length,0);
    for (const endpoint of ['https://example.com/','http://127.0.0.1:8765/other','http://user@localhost/']) assert.throws(()=>validateConfig({endpoint}));
  } finally { await f.close(); }
});
test('offline bridge leaves the native workflow intact within the bounded request time', async () => {
  const f = await fixture(); await f.close(); const start = Date.now();
  assert.deepEqual(await handleHook(input('PermissionRequest'),f.config),{});
  assert.ok(Date.now()-start<1500);
});
test('installed command survives Chinese, spaces, apostrophes and shell metacharacters; repair/remove preserve settings', {skip:process.platform!=='win32'}, async () => {
  const f = await fixture(); const prefix = path.join(os.tmpdir(), 'alpha-claude-中文-&-');
  const root = await mkdtemp(prefix + "' ");
  const home = path.join(root,'claude'), config = path.join(root,'config'), integration = path.join(root,'adapter');
  try {
    await mkdir(home,{recursive:true});
    const settings = path.join(home,'settings.json');
    const other = {env:{KEEP:'fixture'},statusLine:{type:'command',command:'keep-status'},hooks:{Stop:[{matcher:'',hooks:[{type:'command',command:'keep-hook',statusMessage:'Other'}]}]}};
    await writeFile(settings,JSON.stringify(other));
    const installer=fileURLToPath(new URL('../../examples/ClaudeCodeHooks/install.ps1',import.meta.url));
    const args=['-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',installer,'-NodePath',process.execPath,'-ClaudeDirectory',home,'-ConfigDirectory',config,'-IntegrationDirectory',integration,'-NativeVersion','2.1.139','-Port',String(new URL(f.config.endpoint).port)];
    let result=await run('powershell.exe',args); assert.equal(result.code,0,result.stderr);
    result=await run('powershell.exe',args); assert.equal(result.code,0,result.stderr);
    let value=JSON.parse(await readFile(settings,'utf8')); assert.deepEqual(value.env,other.env);assert.deepEqual(value.statusLine,other.statusLine);
    assert.equal(value.hooks.Stop.length,2);assert.equal(Object.keys(value.hooks).length,14);assert.equal(value.hooks.MessageDisplay,undefined);
    const command=value.hooks.PermissionRequest.at(-1).hooks[0].command;
    const payload=JSON.stringify(input('PermissionRequest','execution-check',{tool_name:'Bash'}));
    result=await run('cmd.exe',['/d','/s','/c',command],payload); assert.equal(result.code,0,result.stderr);assert.equal(result.stdout,'');
    assert.equal(f.received.at(-1).body.state,'tool-wait');
    result=await run('powershell.exe',[...args,'-Remove']);assert.equal(result.code,0,result.stderr);
    value=JSON.parse(await readFile(settings,'utf8'));assert.deepEqual(value,other);
    await writeFile(settings,'{corrupt');result=await run('powershell.exe',args);assert.notEqual(result.code,0);assert.equal(await readFile(settings,'utf8'),'{corrupt');
  } finally { await f.close(); assert.ok(path.resolve(root).startsWith(path.resolve(prefix))); await rm(root,{recursive:true,force:true}); }
});
