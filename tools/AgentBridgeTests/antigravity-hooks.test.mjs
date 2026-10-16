import { test } from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import { mkdtemp, readdir, rm, readFile, writeFile, mkdir } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { handleHook, validateConfig, approvalTools, redact } from '../../examples/AntigravityHooks/hook.mjs';

const input = (conversationId = 'fixture') => ({ conversationId, stepIdx: 0, workspacePaths: [],
  toolCall: { name: 'run_command', args: { CommandLine: 'echo harmless', apiKey: 'never-forward' } } });
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));

test('Antigravity model families select their character without forwarding model names', async()=>{
  const f=await fixture();
  try {
    for(const [modelName,family] of [['Gemini 2.5 Pro','gemini'],['Claude Sonnet 4.5','claude'],['DeepSeek V3','deepseek'],['GPT-OSS 120B','codex']]) {
      await handleHook('PreInvocation',{conversationId:'model-artwork-fixture',modelName},f.config);
      const status=f.statuses.at(-1);
      assert.equal(status.artworkFamily,family,modelName+' must receive the corresponding animated character');
      assert.equal(status.source,'antigravity');
      assert.ok(!JSON.stringify(status).includes(modelName));
    }
  } finally {await f.close();}
});
async function fixture(options = {}) {
  const root = await mkdtemp(path.join(os.tmpdir(), 'alpha-antigravity-'));
  const statuses = [], requests = [], pending = new Map();
  const server = http.createServer(async (req, res) => {
    const chunks = []; for await (const chunk of req) chunks.push(chunk);
    const body = chunks.length ? JSON.parse(Buffer.concat(chunks)) : undefined;
    const send = value => { res.setHeader('Content-Type', 'application/json'); res.end(JSON.stringify(value)); };
    if(req.url === '/health') return send({ service: options.wrongService ? 'other' : 'alpha-island-agent-bridge', statusReporting: true, apiDecisions: !!options.apiDecisions });
    if(req.url === '/status') { statuses.push(body); return send({ok:true}); }
    if(req.method === 'DELETE') {
      const id = req.url.split('/').at(-1); pending.get(id)?.end(JSON.stringify({ id, approved:false, decidedBy:'client', timedOut:false }));
      requests.push({ cancel:id }); return send({ok:true});
    }
    requests.push(body);
    if(options.pending) { pending.set(body.id,res); return; }
    if(options.disconnect) { req.socket.destroy(); return; }
    send({ id: options.wrongId ? 'unrelated' : body.id, approved: options.approved ?? false,
      decidedBy: options.decidedBy ?? 'mouse', ...(options.missingTimeout ? {} : { timedOut: options.timedOut ?? false }) });
  });
  await new Promise(resolve => server.listen(0,'127.0.0.1',resolve));
  return {root,statuses,requests,config:{endpoint:`http://127.0.0.1:${server.address().port}/`,runtimeDir:root,timeoutSec:5},
    close:async()=>{server.closeAllConnections();await new Promise(resolve=>server.close(resolve));await rm(root,{recursive:true,force:true});}};
}

const eager = 'CASCADE_COMMANDS_AUTO_EXECUTION_EAGER';
const review = 'CASCADE_COMMANDS_AUTO_EXECUTION_OFF';
const fileAllow = 'AGENT_SETTING_POLICY_ALLOW';
const fileAsk = 'AGENT_SETTING_POLICY_ASK';
async function autoFixture(settings = { autoExecutionPolicy: eager }, options = {}) {
  const f = await fixture({ approved: true, ...options });
  const nativeRoot = path.join(f.root, 'native-config');
  const workspace = path.join(f.root, 'workspace');
  await mkdir(nativeRoot); await mkdir(workspace);
  Object.assign(f.config, { approvalMode: 'auto', nativeConfigPath: path.join(nativeRoot, 'config.json'), runtimeDir: path.join(f.root, 'runtime') });
  const setGlobal = settings => writeFile(f.config.nativeConfigPath, JSON.stringify({ userSettings: settings }));
  const setProject = async (id, roots, settings) => {
    await mkdir(path.join(nativeRoot, 'projects'), { recursive: true });
    await writeFile(path.join(nativeRoot, 'projects', id + '.json'), JSON.stringify({ id,
      projectResources: { resources: roots.map(root => ({ folderUri: pathToFileURL(root).href })) }, settings }));
  };
  await setGlobal(settings);
  return Object.assign(f, { workspace, nativeRoot, setGlobal, setProject,
    tool: (name = 'run_command', args = input().toolCall.args) => ({ ...input(), modelName: 'Claude Sonnet 4.5', workspacePaths: [workspace], toolCall: { name, args } }) });
}

test('automatic mode follows Always Proceed without an island prompt and re-reads changed settings', async () => {
  const f = await autoFixture();
  try {
    const result = await handleHook('PreToolUse', f.tool(), f.config);
    assert.equal(result.decision, 'allow');
    assert.equal(result.permissionOverrides, undefined);
    assert.equal(f.requests.filter(r => r.tool).length, 0);
    assert.equal(f.statuses.at(-1).state, 'working');
    assert.equal(f.statuses.at(-1).artworkFamily, 'claude');
    assert.ok(!f.statuses.some(s => s.state === 'tool-wait'));
    await f.setGlobal({ autoExecutionPolicy: review });
    assert.equal((await handleHook('PreToolUse', f.tool(), f.config)).decision, 'ask');
    assert.equal(f.requests.filter(r => r.tool).length, 1);
    await f.setGlobal({ autoExecutionPolicy: eager });
    assert.equal((await handleHook('PreToolUse', f.tool(), f.config)).decision, 'allow');
    assert.equal(f.requests.filter(r => r.tool).length, 1);
  } finally { await f.close(); }
});

test('project permission overrides beat global settings and unspecified fields inherit', async () => {
  const f = await autoFixture();
  try {
    await f.setProject('active', [f.workspace], { autoExecutionPolicy: review });
    assert.equal((await handleHook('PreToolUse', f.tool(), f.config)).decision, 'ask');
    await f.setGlobal({ autoExecutionPolicy: review });
    await f.setProject('active', [f.workspace], { autoExecutionPolicy: eager });
    assert.equal((await handleHook('PreToolUse', f.tool(), f.config)).decision, 'allow');
    await f.setProject('active', [f.workspace], { autoExecutionPolicy: 'CASCADE_COMMANDS_AUTO_EXECUTION_UNSPECIFIED' });
    assert.equal((await handleHook('PreToolUse', f.tool(), f.config)).decision, 'ask');
    await f.setGlobal({ autoExecutionPolicy: eager });
    assert.equal((await handleHook('PreToolUse', f.tool(), f.config)).decision, 'allow');
    await f.setProject('unrelated', [path.join(f.root, 'different-project')], { autoExecutionPolicy: review });
    assert.equal((await handleHook('PreToolUse', f.tool(), f.config)).decision, 'allow');
  } finally { await f.close(); }
});

test('ambiguous projects, mounted roots and unknown presets retain island confirmation', async () => {
  const f = await autoFixture();
  try {
    await f.setProject('one', [f.workspace], { autoExecutionPolicy: eager });
    await f.setProject('two', [f.workspace], { autoExecutionPolicy: review });
    assert.equal((await handleHook('PreToolUse', f.tool(), f.config)).decision, 'ask');
    await f.setProject('two', [f.workspace], {});
    const multi = { ...f.tool(), workspacePaths: [f.workspace, path.join(f.root, 'second-root')] };
    assert.equal((await handleHook('PreToolUse', multi, f.config)).decision, 'ask');
    await f.setProject('two', [f.workspace], { permissionPreset: 'AGENT_PERMISSION_PRESET_TURBO' });
    assert.equal((await handleHook('PreToolUse', f.tool(), f.config)).decision, 'ask');
    await f.setProject('two', [], { autoExecutionPolicy: review });
    assert.equal((await handleHook('PreToolUse', f.tool(), f.config)).decision, 'ask');
  } finally { await f.close(); }
});

test('workspace edits follow native permissions while external files have their own policy', async () => {
  const f = await autoFixture({ autoExecutionPolicy: review, nonWorkspaceFileAccessPolicy: fileAsk });
  try {
    for (const name of ['write_to_file', 'replace_file_content', 'multi_replace_file_content']) {
      const result = await handleHook('PreToolUse', f.tool(name, { TargetFile: path.join(f.workspace, 'new', 'file.txt') }), f.config);
      assert.equal(result.decision, 'allow', name);
    }
    assert.equal(f.requests.filter(r => r.tool).length, 0);
    for (const target of [path.join(f.workspace + '-sibling', 'file.txt'), path.join(f.workspace, '..', 'outside.txt'), 'relative.txt']) {
      assert.equal((await handleHook('PreToolUse', f.tool('write_to_file', { TargetFile: target }), f.config)).decision, 'ask');
    }
    await f.setGlobal({ autoExecutionPolicy: review, nonWorkspaceFileAccessPolicy: fileAllow });
    const outside = f.tool('write_to_file', { TargetFile: path.join(f.root, 'outside.txt') });
    assert.equal((await handleHook('PreToolUse', outside, f.config)).decision, 'allow');
    await f.setProject('active', [f.workspace], { fileAccessPolicy: fileAsk });
    assert.equal((await handleHook('PreToolUse', outside, f.config)).decision, 'ask');
    assert.equal((await handleHook('PreToolUse', f.tool(), f.config)).decision, 'ask');
  } finally { await f.close(); }
});

test('a workspace junction cannot misclassify an external edit as workspace access', { skip: process.platform !== 'win32' }, async () => {
  const f = await autoFixture({ nonWorkspaceFileAccessPolicy: fileAsk });
  try {
    const outside = path.join(f.root, 'outside'); await mkdir(outside);
    const { symlink } = await import('node:fs/promises');
    await symlink(outside, path.join(f.workspace, 'linked'), 'junction');
    assert.equal((await handleHook('PreToolUse', f.tool('write_to_file', { TargetFile: path.join(f.workspace, 'linked', 'new.txt') }), f.config)).decision, 'ask');
    const mixedCase = { ...f.tool(), workspacePaths: [f.workspace.toUpperCase()] };
    mixedCase.toolCall = { name: 'write_to_file', args: { TargetFile: path.join(f.workspace, 'new.txt') } };
    assert.equal((await handleHook('PreToolUse', mixedCase, f.config)).decision, 'allow');
  } finally { await f.close(); }
});

test('terminal Always Proceed does not approve independent tool categories or unknown terminal modes', async () => {
  const f = await autoFixture({ autoExecutionPolicy: eager, nonWorkspaceFileAccessPolicy: fileAsk });
  try {
    for (const name of ['mcp_database_execute', 'ask_permission', 'schedule', 'manage_task'])
      assert.equal((await handleHook('PreToolUse', f.tool(name, {}), f.config)).decision, 'ask', name);
    assert.equal((await handleHook('PreToolUse', f.tool('write_to_file', { TargetFile: path.join(f.root, 'outside') }), f.config)).decision, 'ask');
    for (const policy of [review, 'CASCADE_COMMANDS_AUTO_EXECUTION_AUTO', 'CASCADE_COMMANDS_AUTO_EXECUTION_PROCEED_IN_SANDBOX', 'future-policy', 3, null]) {
      await f.setGlobal({ autoExecutionPolicy: policy });
      assert.equal((await handleHook('PreToolUse', f.tool(), f.config)).decision, 'ask', String(policy));
    }
  } finally { await f.close(); }
});

test('missing, corrupt, oversized or malformed native configuration never removes confirmation', async () => {
  const f = await autoFixture();
  try {
    for (const contents of ['{broken', '[]', '{}', JSON.stringify({ userSettings: [] }), ' '.repeat(1024 * 1024 + 1)]) {
      await writeFile(f.config.nativeConfigPath, contents);
      assert.equal((await handleHook('PreToolUse', f.tool(), f.config)).decision, 'ask');
    }
    await rm(f.config.nativeConfigPath);
    assert.equal((await handleHook('PreToolUse', f.tool(), f.config)).decision, 'ask');
    await f.setGlobal({ autoExecutionPolicy: eager });
    await mkdir(path.join(f.nativeRoot, 'projects'));
    await writeFile(path.join(f.nativeRoot, 'projects', 'broken.json'), '{broken');
    assert.equal((await handleHook('PreToolUse', f.tool(), f.config)).decision, 'ask');
    assert.equal((await handleHook('PreToolUse', { ...f.tool(), workspacePaths: ['relative'] }, f.config)).decision, 'ask');
  } finally { await f.close(); }
});

test('explicit confirm and cancellation take priority over automatic native following', async () => {
  const f = await autoFixture();
  try {
    assert.equal((await handleHook('PreToolUse', f.tool(), { ...f.config, approvalMode: 'confirm' })).decision, 'ask');
    const controller = new AbortController(); controller.abort();
    const count = f.requests.filter(r => r.tool).length;
    assert.equal((await handleHook('PreToolUse', f.tool(), f.config, { signal: controller.signal })).decision, 'deny');
    assert.equal(f.requests.filter(r => r.tool).length, count);
  } finally { await f.close(); }
});

test('native following delegates rules without exporting native settings or accepting payload overrides', async () => {
  const f = await autoFixture({ autoExecutionPolicy: eager, apiKey: 'private-native-key', deniedCommands: ['echo'], askedCommands: ['echo harmless'] });
  try {
    const call = { ...f.tool(), transcriptPath: 'never-open', permissionOverrides: ['command(*)'], nativePolicy: 'auto_approve' };
    const result = await handleHook('PreToolUse', call, f.config);
    assert.equal(result.decision, 'allow'); // The next hook/native policy still decides Ask/Deny.
    assert.deepEqual(Object.keys(result).sort(), ['decision', 'reason']);
    assert.ok(!JSON.stringify({ result, statuses: f.statuses }).includes('private-native-key'));
    assert.ok(!JSON.stringify(f.statuses).includes(f.nativeRoot));
    assert.ok(!JSON.stringify(f.statuses).includes('never-open'));
    assert.equal(f.requests.length, 0);
    await f.setGlobal({ autoExecutionPolicy: review });
    assert.equal((await handleHook('PreToolUse', call, f.config)).decision, 'ask');
  } finally { await f.close(); }
});

test('a one-shot island grant returns native ask, never automatic allow', async()=>{
  const f=await fixture({approved:true});
  try {
    assert.equal((await handleHook('PreToolUse',input(),f.config)).decision,'ask');
    const req=f.requests.find(r=>r.tool);
    assert.equal(req.tool,'Antigravity · run_command'); assert.equal(req.source,'antigravity');
    assert.equal(req.args.apiKey,'[已隐藏]'); assert.ok(!JSON.stringify(f.requests).includes('never-forward'));
    assert.equal((await readdir(f.root)).length,0);
  } finally {await f.close();}
});
test('denial, timeout, unrelated IDs and non-user decisions never continue',async()=>{
  for(const options of [{approved:false},{approved:true,timedOut:true},{approved:true,decidedBy:'api'},
    {approved:true,decidedBy:'debug'},{approved:true,wrongId:true},{approved:true,missingTimeout:true},{disconnect:true}]) {
    const f=await fixture(options);
    try {assert.equal((await handleHook('PreToolUse',input(),f.config)).decision,'deny');}
    finally {await f.close();}
  }
});
test('offline or incompatible bridge returns native approval before handoff',async()=>{
  assert.equal((await handleHook('PreToolUse',input(),{endpoint:'http://127.0.0.1:1/',runtimeDir:os.tmpdir()})).decision,'ask');
  for(const options of [{apiDecisions:true},{wrongService:true}]) {
    const f=await fixture(options);try {
      assert.equal((await handleHook('PreToolUse',input(),f.config)).decision,'ask');
      assert.equal(f.requests.length,0);
    }finally{await f.close();}
  }
});
test('cancelling a waiting hook withdraws that request and removes marker',async()=>{
  const f=await fixture({pending:true});const controller=new AbortController();
  try {
    const waiting=handleHook('PreToolUse',input(),f.config,{signal:controller.signal});
    for(let i=0;!f.requests.some(r=>r.tool)&&i<100;i++)await delay(10);
    assert.ok(f.requests.some(r=>r.tool));controller.abort();
    assert.equal((await waiting).decision,'deny');assert.equal((await readdir(f.root)).length,0);
    assert.ok(f.requests.some(r=>r.cancel));
  }finally{await f.close();}
});
test('stop cancels only its own session and cannot resurrect its status',async()=>{
  const f=await fixture({pending:true});const controller=new AbortController();
  try {
    const a=handleHook('PreToolUse',input('a'),f.config);
    const b=handleHook('PreToolUse',input('b'),f.config,{signal:controller.signal});
    for(let i=0;f.requests.filter(r=>r.tool).length<2&&i<100;i++)await delay(10);
    assert.equal(f.requests.filter(r=>r.tool).length,2);
    await handleHook('Stop',{conversationId:'a',fullyIdle:true},f.config);
    assert.equal((await a).decision,'deny');assert.equal((await readdir(f.root)).length,1);
    const aSession=createHash('sha256').update('a').digest('hex').slice(0,24);
    assert.equal(f.statuses.filter(s=>s.session===aSession).at(-1).state,'idle');
    controller.abort();await b;
  }finally{await f.close();}
});
test('status hooks avoid transcripts, results and ordinary read-tool confirmation',async()=>{
  const f=await fixture();try {
    await handleHook('PreInvocation',{conversationId:'s',transcriptPath:'must-not-open',modelName:'private'},f.config);
    await handleHook('PreToolUse',{...input('s'),toolCall:{name:'view_file',args:{AbsolutePath:'private-path'}}},f.config);
    await handleHook('PostToolUse',{...input('s'),error:'private runtime content'},f.config);
    await handleHook('Stop',{conversationId:'s',fullyIdle:false},f.config);
    assert.equal(f.statuses.at(-1).state,'working');
    await handleHook('Stop',{conversationId:'s',fullyIdle:true},f.config);
    assert.equal(f.statuses.at(-1).state,'idle');assert.equal(f.requests.length,0);
    assert.ok(!JSON.stringify(f.statuses).includes('private'));
    assert.ok(f.statuses.every(s => s.reporterPid === process.pid));
    assert.deepEqual(await handleHook('Unknown',input('s'),f.config),{});
  }finally{await f.close();}
});
test('configuration and secret redaction remain bounded',()=>{
  for(const endpoint of ['https://127.0.0.1/','http://example.com/','http://user@127.0.0.1/','http://127.0.0.1/path'])
    assert.throws(()=>validateConfig({endpoint,runtimeDir:os.tmpdir()}));
  assert.throws(()=>validateConfig({runtimeDir:'relative'}));
  assert.throws(()=>validateConfig({runtimeDir:os.tmpdir(),approvalMode:'allow'}));
  assert.throws(()=>validateConfig({runtimeDir:os.tmpdir(),approvalMode:'auto',nativeConfigPath:'relative'}));
  assert.equal(validateConfig({runtimeDir:os.tmpdir(),approvalMode:'auto'}).approvalMode,'auto');
  assert.equal(validateConfig({runtimeDir:os.tmpdir(),timeoutSec:999}).timeoutSec,120);
  assert.deepEqual(redact({token:'x',nested:{command:'--api-key abc Bearer xyz'}}),{token:'[已隐藏]',nested:{command:'--api-key [已隐藏] Bearer [已隐藏]'}});
  assert.ok(approvalTools.test('write_to_file'));assert.ok(!approvalTools.test('search_web'));
});

test('Windows installer merges, repeats and removes only its own hooks',{skip:process.platform!=='win32'},async()=>{
  const root=await mkdtemp(path.join(os.tmpdir(), "alpha hooks & ' unicode-"));
  const f=await fixture();
  const hookPath=path.join(root,'hooks.json'),configDir=path.join(root,'config'),integration=path.join(root,'integration');
  const installer=fileURLToPath(new URL('../../examples/AntigravityHooks/install.ps1',import.meta.url));
  const existing={'my-linter':{enabled:false,PostToolUse:[{matcher:'*',hooks:[{command:'echo existing'}]}]}};
  const run=extra=>spawnSync('powershell.exe',['-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',installer,
    '-NodePath',process.execPath,'-HookPath',hookPath,'-ConfigDirectory',configDir,'-IntegrationDirectory',integration,
    '-RuntimeDirectory',path.join(root,'runtime'),'-Port',new URL(f.config.endpoint).port,...extra],{encoding:'utf8',windowsHide:true});
  try {
    await writeFile(hookPath,JSON.stringify(existing));
    for(let i=0;i<2;i++) {
      const result=run([]);assert.equal(result.status,0,result.stderr);
      const hooks=JSON.parse(await readFile(hookPath,'utf8'));assert.deepEqual(hooks['my-linter'],existing['my-linter']);
      assert.equal(Object.keys(hooks).length,2);
      const own=hooks['alpha-island-agent-bridge'];assert.ok(Array.isArray(own.PreInvocation));assert.ok(Array.isArray(own.PreToolUse));
      assert.equal(own.PreToolUse[0].hooks[0].timeout,130);
      const config=JSON.parse(await readFile(path.join(configDir,'antigravity-hooks.json'),'utf8'));
      assert.equal(config.approvalMode,'auto');
      assert.equal(config.nativeConfigPath,path.join(root,'config.json'));
      // Execute exactly the installed command against a status-only fixture. Tests shell/path quoting.
      const command=own.PreInvocation[0].command;
      const invoked=spawnSync('cmd.exe',['/c',command],{input:JSON.stringify({conversationId:'quote-fixture'}),encoding:'utf8',windowsHide:true});
      assert.equal(invoked.status,0,invoked.stderr);assert.deepEqual(JSON.parse(invoked.stdout.trim()),{});
    }
    assert.equal(run(['-ApprovalMode','confirm']).status,0);
    assert.equal(JSON.parse(await readFile(path.join(configDir,'antigravity-hooks.json'),'utf8')).approvalMode,'confirm');
    assert.equal(run(['-Remove']).status,0);assert.deepEqual(JSON.parse(await readFile(hookPath,'utf8')),existing);
    assert.equal(run(['-Remove']).status,0);assert.deepEqual(JSON.parse(await readFile(hookPath,'utf8')),existing);
    await writeFile(hookPath,'{invalid');assert.notEqual(run([]).status,0);assert.equal(await readFile(hookPath,'utf8'),'{invalid');
    const collision={'alpha-island-agent-bridge':{PreInvocation:[{command:'echo unknown'}]}};
    await writeFile(hookPath,JSON.stringify(collision));assert.notEqual(run([]).status,0);
    assert.deepEqual(JSON.parse(await readFile(hookPath,'utf8')),collision);
  }finally{await f.close();await rm(root,{recursive:true,force:true});}
});
