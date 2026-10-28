import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, mkdir, writeFile, readFile, readdir, cp, rm } from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const repo=fileURLToPath(new URL('../../',import.meta.url));
const setup=path.join(repo,'packaging','agent-setup.ps1');
async function fixture(apps=true){
  const root=await mkdtemp(path.join(os.tmpdir(),"alpha-portable-中文-' "));
  const install=path.join(root,'installation'),data=path.join(root,'alpha-island'),user=path.join(root,'user'),local=path.join(root,'local'),project=path.join(root,'project');
  const codex=path.join(user,'.codex'),harness=path.join(user,'.dsh'),ag=path.join(user,'.gemini','config','hooks.json');
  for(const dir of [path.join(install,'agent-integrations','runtime'),path.join(data,'config'),user,local,project])await mkdir(dir,{recursive:true});
  for(const adapter of ['CodexHooks','AntigravityHooks','DeepSeekHarnessPlugin','ClaudeCodeHooks'])await cp(path.join(repo,'examples',adapter),path.join(install,'agent-integrations',adapter),{recursive:true});
  for(const file of ['node.exe','LICENSE','SHA256.txt'])await writeFile(path.join(install,'agent-integrations','runtime',file),'fixture runtime, never executed');
  if(apps){
    await mkdir(path.join(user,'.claude'),{recursive:true});
    await writeFile(path.join(user,'.claude','settings.json'),JSON.stringify({statusLine:{type:'command',command:'preserve-statusline'},env:{KEEP:'fixture'}}));
    await mkdir(codex,{recursive:true});await mkdir(path.dirname(ag),{recursive:true});await mkdir(path.join(harness,'profiles','web'),{recursive:true});
    await writeFile(path.join(codex,'hooks.json'),JSON.stringify({hooks:{SessionStart:[{hooks:[{type:'command',command:'preserve',statusMessage:'other'}]}]}}));
    await writeFile(ag,JSON.stringify({linter:{enabled:true,PreInvocation:[{command:'preserve'}]}}));
    await writeFile(path.join(harness,'profiles','web','cordis.patch.yml'),'# user settings\n- id: original\n  name: preserve\n');
    await writeFile(path.join(data,'config','codex-hooks.json'),JSON.stringify({projectRoot:project}));
  }
  function run(mode='Detect',provider='all',extra=[],environment={},hostDiscovery=false){
    const result=spawnSync('powershell.exe',['-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',setup,'-Mode',mode,'-Provider',provider,
      '-InstallDirectory',install,'-DataRoot',data,'-UserProfileDirectory',user,'-LocalDataDirectory',local,...(hostDiscovery?['-CodexHome',codex]:['-Isolated']),'-Port','9123',...extra],
      {encoding:'utf8',windowsHide:true,env:{...process.env,...environment},timeout:30000});
    assert.equal(result.status,0,result.stderr+'\n'+result.stdout);
    return JSON.parse(result.stdout.trim());
  }
  return {root,install,data,user,local,project,codex,harness,ag,run,cleanup:()=>rm(root,{recursive:true,force:true})};
}

test('fresh device with no optional apps stays usable without creating app configurations',{skip:process.platform!=='win32'},async()=>{
  const f=await fixture(false);try{
    const report=f.run('Auto');assert.equal(report.providers.length,4);assert.ok(report.providers.every(p=>p.status==='not-installed'));
    assert.deepEqual(await readdir(f.user),[]);assert.deepEqual(await readdir(path.join(f.data,'config')),[]);
    await assert.rejects(readdir(path.join(f.data,'integrations')));
  }finally{await f.cleanup();}
});

test('installed Codex without a user profile reports initialization required and leaves the profile untouched',{skip:process.platform!=='win32'},async()=>{
  const f=await fixture(false);try{
    await mkdir(path.join(f.local,'OpenAI','Codex'),{recursive:true});
    const report=f.run('Auto','codex',[],{},true);
    const codex=report.providers.find(p=>p.id==='codex');
    assert.equal(codex.detected,true);
    assert.equal(codex.status,'profile-required');
    await assert.rejects(readdir(f.codex));
  }finally{await f.cleanup();}
});

test('fresh Codex installation automatically covers projects on different drives without a project directory',{skip:process.platform!=='win32'},async()=>{
  const f=await fixture();try{
    await rm(path.join(f.data,'config','codex-hooks.json'));
    const report=f.run('Auto');assert.equal(report.providers.find(p=>p.id==='codex').status,'configured');
    const config=JSON.parse(await readFile(path.join(f.data,'config','codex-hooks.json'),'utf8'));
    assert.equal(config.allProjects,true);assert.equal(report.providers.find(p=>p.id==='codex').allProjects,true);
    assert.equal(f.run().providers.find(p=>p.id==='codex').allProjects,true);
    f.run('Install','codex',['-ProjectRoot',f.project]);
    assert.equal(JSON.parse(await readFile(path.join(f.data,'config','codex-hooks.json'),'utf8')).allProjects,false);
    f.run('Install','codex',['-AllProjects']);
    assert.equal(JSON.parse(await readFile(path.join(f.data,'config','codex-hooks.json'),'utf8')).allProjects,true);
    f.run('Remove','codex');f.run('Auto');assert.equal(f.run().providers.find(p=>p.id==='codex').status,'disabled');
  }finally{await f.cleanup();}
});

test('an agent installed after the first launch is picked up on the next startup',{skip:process.platform!=='win32'},async()=>{
  const f=await fixture(false);try{
    f.run('Auto');await mkdir(f.codex,{recursive:true});
    assert.equal(f.run('Auto').providers.find(p=>p.id==='codex').status,'configured');
    const config=JSON.parse(await readFile(path.join(f.data,'config','codex-hooks.json'),'utf8'));
    assert.equal(config.allProjects,true);assert.equal(config.approvalMode,'native');
    assert.ok(config.runtimeDir.startsWith(f.data));
  }finally{await f.cleanup();}
});

test('automatic setup preserves other hooks, scope, custom port and user disable decisions',{skip:process.platform!=='win32'},async()=>{
  const f=await fixture();try{
    assert.ok(f.run('Detect').providers.every(p=>p.status==='available'));
    const first=f.run('Auto');assert.ok(first.providers.every(p=>p.status==='configured'),JSON.stringify(first));
    const ag=JSON.parse(await readFile(f.ag,'utf8'));assert.equal(ag.linter.PreInvocation[0].command,'preserve');
    const codex=JSON.parse(await readFile(path.join(f.codex,'hooks.json'),'utf8'));assert.equal(codex.hooks.SessionStart[0].hooks[0].command,'preserve');
    const config=JSON.parse(await readFile(path.join(f.data,'config','codex-hooks.json'),'utf8'));assert.equal(config.projectRoot,f.project);assert.equal(config.endpoint,'http://127.0.0.1:9123/');assert.equal(config.allProjects,false);
    assert.ok(config.runtimeDir.startsWith(f.data));assert.equal(config.hookPath,path.join(f.codex,'hooks.json'));
    const command=codex.hooks.SessionStart[1].hooks[0].command;assert.ok(command.includes('integrations'));assert.ok(command.includes('codex'));assert.ok(!command.includes(path.join(repo,'examples')));
    const before=await readdir(path.join(f.data,'config','backups'));f.run('Auto');assert.deepEqual(await readdir(path.join(f.data,'config','backups')),before);
    f.run('Remove','antigravity');assert.deepEqual(JSON.parse(await readFile(f.ag,'utf8')),{linter:ag.linter});
    f.run('Auto');assert.equal(f.run().providers.find(p=>p.id==='antigravity').status,'disabled');assert.equal(JSON.parse(await readFile(f.ag,'utf8'))['alpha-island-agent-bridge'],undefined);
    f.run('Install','antigravity');assert.equal(f.run().providers.find(p=>p.id==='antigravity').status,'configured');
    const nativeDisabled=JSON.parse(await readFile(f.ag,'utf8'));nativeDisabled['alpha-island-agent-bridge'].enabled=false;await writeFile(f.ag,JSON.stringify(nativeDisabled));
    await writeFile(path.join(f.install,'agent-integrations','AntigravityHooks','hook.mjs'),'// new adapter version');
    f.run('Auto');assert.equal(JSON.parse(await readFile(f.ag,'utf8'))['alpha-island-agent-bridge'].enabled,false);
    f.run('Install','antigravity');assert.equal(JSON.parse(await readFile(f.ag,'utf8'))['alpha-island-agent-bridge'].enabled,true);
    // External removal is also respected on startup, rather than silently resurrected.
    await writeFile(f.ag,JSON.stringify({linter:ag.linter}));f.run('Auto');assert.equal(f.run().providers.find(p=>p.id==='antigravity').status,'removed');
  }finally{await f.cleanup();}
});

test('DSH_HOME environment takes priority over default home without a manually supplied directory',{skip:process.platform!=='win32'},async()=>{
  const f=await fixture();try{
    const custom=path.join(f.root,'DSH-custom-home');await cp(f.harness,custom,{recursive:true});
    const report=f.run('Auto','deepseek-harness',[],{DSH_HOME:custom},true);
    assert.equal(report.providers.find(p=>p.id==='deepseek-harness').status,'configured');
    assert.equal(JSON.parse(await readFile(path.join(f.data,'config','deepseek-harness.json'),'utf8')).home,custom);
    assert.equal(await readFile(path.join(f.harness,'profiles','web','cordis.patch.yml'),'utf8'),'# user settings\n- id: original\n  name: preserve\n');
  }finally{await f.cleanup();}
});

test('custom configuration roots and imported invalid scope are detected without broadening scope',{skip:process.platform!=='win32'},async()=>{
  const f=await fixture();try{
    const custom=path.join(f.root,'another-drive-harness');await cp(f.harness,custom,{recursive:true});
    await writeFile(path.join(f.data,'config','codex-hooks.json'),JSON.stringify({projectRoot:path.join(f.root,'old-machine-missing-project')}));
    const report=f.run('Auto','all',['-HarnessHome',custom]);assert.equal(report.providers.find(p=>p.id==='codex').status,'scope-required');
    assert.equal(report.providers.find(p=>p.id==='deepseek-harness').status,'configured');
    assert.equal(JSON.parse(await readFile(path.join(f.data,'config','deepseek-harness.json'),'utf8')).home,custom);
    assert.equal(JSON.parse(await readFile(path.join(f.codex,'hooks.json'),'utf8')).hooks.SessionStart.length,1);
    f.run('Install','codex',['-ProjectRoot',f.project]);assert.equal(f.run().providers.find(p=>p.id==='codex').status,'configured');
  }finally{await f.cleanup();}
});

test('corrupt preferences and unowned collisions are preserved and reported',{skip:process.platform!=='win32'},async()=>{
  const f=await fixture();try{
    const collision={'alpha-island-agent-bridge':{PreInvocation:[{command:'unknown owner'}]}};await writeFile(f.ag,JSON.stringify(collision));
    assert.equal(f.run('Auto').providers.find(p=>p.id==='antigravity').status,'error');assert.deepEqual(JSON.parse(await readFile(f.ag,'utf8')),collision);
    const ledger=path.join(f.data,'config','integrations.json');await writeFile(ledger,'{corrupt');
    assert.throws(()=>f.run('Detect'),/corrupt/);assert.equal(await readFile(ledger,'utf8'),'{corrupt');
  }finally{await f.cleanup();}
});

test('imported preferences configure the new device while preserving disabled providers',{skip:process.platform!=='win32'},async()=>{
  const old=await fixture(),next=await fixture();try{
    old.run('Auto');old.run('Remove','deepseek-harness');
    await cp(path.join(old.data,'config','integrations.json'),path.join(next.data,'config','integrations.json'));
    const report=next.run('Auto');assert.equal(report.providers.find(p=>p.id==='antigravity').status,'configured');
    assert.equal(report.providers.find(p=>p.id==='deepseek-harness').status,'disabled');
    const ledger=JSON.parse(await readFile(path.join(next.data,'config','integrations.json'),'utf8'));
    assert.equal(ledger.providers.antigravity.target,next.ag);
    const agConfig=JSON.parse(await readFile(path.join(next.data,'config','antigravity-hooks.json'),'utf8'));
    assert.equal(agConfig.approvalMode,'auto');
    assert.equal(agConfig.nativeConfigPath,path.join(next.user,'.gemini','config','config.json'));
    assert.ok(!agConfig.nativeConfigPath.startsWith(old.user));
    assert.notEqual(ledger.device,JSON.parse(await readFile(path.join(old.data,'config','integrations.json'),'utf8')).device);
    assert.equal(report.providers.find(p=>p.id==='claude-code').status,'configured');
    const claude=JSON.parse(await readFile(path.join(next.user,'.claude','settings.json'),'utf8'));
    assert.equal(claude.statusLine.command,'preserve-statusline');assert.equal(claude.env.KEEP,'fixture');
    const encoded=claude.hooks.UserPromptSubmit[0].hooks[0].command.split('-EncodedCommand ')[1];
    const decoded=Buffer.from(encoded,'base64').toString('utf16le');
    assert.ok(decoded.includes(next.data.replaceAll("'","''")));assert.ok(!decoded.includes(old.data.replaceAll("'","''")));
    assert.equal(await readFile(path.join(next.harness,'profiles','web','cordis.patch.yml'),'utf8'),'# user settings\n- id: original\n  name: preserve\n');
  }finally{await old.cleanup();await next.cleanup();}
});

test('Antigravity upgrades legacy fixed confirmation and preserves an explicit new-mode choice', { skip: process.platform !== 'win32' }, async () => {
  const f = await fixture();
  try {
    const registration = path.join(f.data, 'config', 'antigravity-hooks.json');
    const nativePath = path.join(f.user, '.gemini', 'config', 'config.json');
    const native = JSON.stringify({ userSettings: { autoExecutionPolicy: 'CASCADE_COMMANDS_AUTO_EXECUTION_EAGER', themeMode: 'fixture' } });
    await writeFile(nativePath, native);
    await writeFile(registration, JSON.stringify({ approvalMode: 'confirm', hookPath: f.ag }));
    f.run('Auto');
    let config = JSON.parse(await readFile(registration, 'utf8'));
    assert.equal(config.approvalMode, 'auto');
    assert.equal(config.policySchemaVersion, 1);
    config.approvalMode = 'confirm';
    await writeFile(registration, JSON.stringify(config));
    f.run('Auto');
    config = JSON.parse(await readFile(registration, 'utf8'));
    assert.equal(config.approvalMode, 'confirm');
    assert.equal(await readFile(nativePath, 'utf8'), native);
  } finally { await f.cleanup(); }
});

test('Claude custom home, global hooks disable, repair and removal preserve the users settings',{skip:process.platform!=='win32'},async()=>{
  const f=await fixture();try{
    const custom=path.join(f.root,'claude-custom');await mkdir(custom);
    const settings=path.join(custom,'settings.json');
    const original={disableAllHooks:true,statusLine:{type:'command',command:'keep'}};
    await writeFile(settings,JSON.stringify(original));
    let report=f.run('Auto','claude-code',['-ClaudeHome',custom]);assert.equal(report.providers.find(p=>p.id==='claude-code').status,'disabled');
    assert.deepEqual(JSON.parse(await readFile(settings,'utf8')),original);
    original.disableAllHooks=false;await writeFile(settings,JSON.stringify(original));
    report=f.run('Auto','claude-code',['-ClaudeHome',custom]);assert.equal(report.providers.find(p=>p.id==='claude-code').status,'configured');
    const configured=JSON.parse(await readFile(settings,'utf8'));assert.equal(Object.keys(configured.hooks).length,10);
    f.run('Remove','claude-code',['-ClaudeHome',custom]);
    const clean=JSON.parse(await readFile(settings,'utf8'));delete clean.hooks;assert.deepEqual(clean,original);
    assert.equal(f.run('Auto','claude-code',['-ClaudeHome',custom]).providers.find(p=>p.id==='claude-code').status,'disabled');
  }finally{await f.cleanup();}
});
