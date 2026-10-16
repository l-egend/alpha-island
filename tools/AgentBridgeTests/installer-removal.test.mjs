import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, mkdir, writeFile, readFile, rm } from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

test('purge validates and removes only registered external agent integrations',{skip:process.platform!=='win32'},async()=>{
  const root=await mkdtemp(path.join(os.tmpdir(),'alpha-agent-removal-'));
  const data=path.join(root,'alpha-island'),home=path.join(root,'harness'),hookPath=path.join(root,'hooks.json');
  const patch=path.join(home,'profiles','web','cordis.patch.yml');
  const maintenance=fileURLToPath(new URL('../../packaging/installer-maintenance.ps1',import.meta.url));
  const runner=path.join(root,'check.ps1');
  const literal=value=>"'"+value.replaceAll("'","''")+"'";
  const run=validate=>spawnSync('powershell.exe',['-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',runner,...(validate?['-ValidateOnly']:[])],{encoding:'utf8',windowsHide:true});
  const own={'alpha-island-agent-bridge':{PreInvocation:[{command:'node hook --config antigravity-hooks.json'}]},other:{enabled:false,PreInvocation:[{command:'preserve'}]}};
  const prefix='# external settings\n- id: original\n  name: preserve\n';
  const marked=prefix+"# BEGIN ALPHA ISLAND AGENT\n- insert:\n    - id: alpha-island-agent\n      name: 'fixture'\n# END ALPHA ISLAND AGENT\n";
  try {
    await mkdir(path.join(data,'config'),{recursive:true});await mkdir(path.dirname(patch),{recursive:true});
    await writeFile(path.join(data,'config','antigravity-hooks.json'),JSON.stringify({hookPath}));
    await writeFile(path.join(data,'config','deepseek-harness.json'),JSON.stringify({home,profile:'web'}));
    const claude=path.join(root,'claude-settings.json');
    await writeFile(path.join(data,'config','claude-code-hooks.json'),JSON.stringify({hookPath:claude}));
    await writeFile(claude,JSON.stringify({env:{KEEP:'fixture'},hooks:{Stop:[{hooks:[{type:'command',command:'keep',statusMessage:'Other'},{type:'command',command:'ours',statusMessage:'Alpha Island / Claude Code / Stop'}]}]}}));
    await writeFile(hookPath,JSON.stringify(own));await writeFile(patch,marked);
    await writeFile(runner,`param([switch]$ValidateOnly)\n$ErrorActionPreference='Stop'\n. ${literal(maintenance)} -InstallDirectory ${literal(root)} -FunctionsOnly\nRemove-ExternalAgentIntegrations ${literal(data)} -ValidateOnly:$ValidateOnly\n`);
    assert.equal(run(true).status,0);assert.deepEqual(JSON.parse(await readFile(hookPath,'utf8')),own);assert.equal(await readFile(patch,'utf8'),marked);
    const result=run(false);assert.equal(result.status,0,result.stderr);
    const remaining=JSON.parse(await readFile(claude,'utf8'));assert.equal(remaining.env.KEEP,'fixture');assert.equal(remaining.hooks.Stop[0].hooks.length,1);assert.equal(remaining.hooks.Stop[0].hooks[0].command,'keep');
    assert.deepEqual(JSON.parse(await readFile(hookPath,'utf8')),{other:own.other});assert.equal(await readFile(patch,'utf8'),prefix);
    assert.equal(run(false).status,0);
    await writeFile(hookPath,'{invalid');assert.notEqual(run(true).status,0);assert.equal(await readFile(hookPath,'utf8'),'{invalid');
    await writeFile(hookPath,JSON.stringify(own));await writeFile(patch,prefix+'# BEGIN ALPHA ISLAND AGENT\n');
    assert.notEqual(run(true).status,0);assert.deepEqual(JSON.parse(await readFile(hookPath,'utf8')),own);
    await writeFile(patch,marked);const collision={'alpha-island-agent-bridge':{PreInvocation:[{command:'not ours'}]}};
    await writeFile(hookPath,JSON.stringify(collision));assert.notEqual(run(true).status,0);assert.deepEqual(JSON.parse(await readFile(hookPath,'utf8')),collision);
  } finally {await rm(root,{recursive:true,force:true});}
});
