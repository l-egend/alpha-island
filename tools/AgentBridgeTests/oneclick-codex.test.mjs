import test from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import {once} from 'node:events';
import {spawn} from 'node:child_process';
import {mkdtemp,mkdir,writeFile,readFile,rm,cp} from 'node:fs/promises';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
const repo=fileURLToPath(new URL('../../',import.meta.url));
const pack=path.join(repo,'bin','Codex-OneClick-Connect');
const base=path.join(repo,'bin','OneClickCodexTests');
async function run(exe,args,stdin=''){const child=spawn(exe,args,{windowsHide:true,cwd:pack,stdio:'pipe'});let out='',err='';child.stdout.on('data',v=>out+=v.toString('utf8'));child.stderr.on('data',v=>err+=v.toString('utf8'));child.stdin.end(stdin);const timer=setTimeout(()=>child.kill(),45000);try{const [code]=await once(child,'exit');return {code,out,err};}finally{clearTimeout(timer)}}
async function fixture(t){
 await mkdir(base,{recursive:true});const root=await mkdtemp(path.join(base,"中文 & user's "));
 const user=path.join(root,'user'),local=path.join(root,'local'),data=path.join(root,'data'),codex=path.join(user,'.codex');
 for(const p of [codex,local,path.join(data,'config')])await mkdir(p,{recursive:true});
 const calls=[],states=new Map();let offline=false;
 const server=http.createServer(async(req,res)=>{let raw='';for await(const chunk of req)raw+=chunk;const body=raw?JSON.parse(raw):null;calls.push({url:req.url,body});res.setHeader('Content-Type','application/json');if(req.url==='/health')res.end(JSON.stringify({service:offline?'wrong-service':'alpha-island-agent-bridge',statusReporting:true,apiDecisions:false}));else if(req.url==='/status'&&req.method==='POST'){states.set(body.session,body);res.end('{}');}else if(req.url==='/status')res.end(JSON.stringify({connections:[...states.values()]}));else{res.statusCode=400;res.end('{}')}});
 server.listen(0,'127.0.0.1');await once(server,'listening');const port=server.address().port;
 const options={AccentHex:'#123456',Extras:{AgentBridgePort:port,AgentBridgeEnabled:false,AgentStatusEnabled:false,AgentApprovalNotifyEnabled:false,NotificationQuietMode:false,'plugin/custom/enabled':false,unrelatedSecret:'PRESERVE_FAKE_SECRET'}};
 const opts=path.join(data,'config','options.json'),hooks=path.join(codex,'hooks.json');
 await writeFile(opts,JSON.stringify(options));await writeFile(hooks,JSON.stringify({hooks:{SessionStart:[{hooks:[{type:'command',command:'preserve-other-hook',statusMessage:'other'}]}]}}));
 await writeFile(path.join(data,'config','codex-hooks.json'),JSON.stringify({projectRoot:'Z:\\copied-missing-project',allProjects:false,approvalMode:'island'}));
 await writeFile(path.join(data,'config','integrations.json'),JSON.stringify({version:1,providers:{antigravity:{enabled:false,target:'preserve-target',stamp:'preserve-stamp'}}}));
 const args=['-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',path.join(pack,'Repair-CodexIsland.ps1'),'-CodexDirectory',codex,'-DataRoot',data,'-UserProfileDirectory',user,'-LocalDataDirectory',local,'-Isolated','-NoRestart'];
 t.after(async()=>{server.closeAllConnections();await new Promise(r=>server.close(r));const full=path.resolve(root);assert.ok(full.startsWith(path.resolve(base)+path.sep));await rm(full,{recursive:true,force:true,maxRetries:10,retryDelay:100});});
 return {root,user,local,data,codex,opts,hooks,port,calls,states,options,args,run:()=>run('powershell.exe',args),offline:()=>offline=true};
}
test('one-click repairs copied scope and enables only the three requested switches; real state probe preserves approval policy',async t=>{
 const f=await fixture(t);const r=await f.run();assert.equal(r.code,0,r.out+'\n'+r.err);
 const options=JSON.parse(await readFile(f.opts,'utf8'));assert.equal(options.AccentHex,f.options.AccentHex);assert.equal(options.Extras['plugin/custom/enabled'],false);assert.equal(options.Extras.unrelatedSecret,'PRESERVE_FAKE_SECRET');for(const k of ['AgentBridgeEnabled','AgentStatusEnabled','AgentApprovalNotifyEnabled'])assert.equal(options.Extras[k],true);
 assert.deepEqual(JSON.parse(await readFile(f.opts+'.bak','utf8')),f.options);
 const config=JSON.parse(await readFile(path.join(f.data,'config','codex-hooks.json'),'utf8'));assert.equal(config.allProjects,true);assert.equal(config.projectRoot,'');assert.equal(config.approvalMode,'island');assert.equal(config.endpoint,`http://127.0.0.1:${f.port}/`);
 const report=JSON.parse(await readFile(path.join(f.data,'logs','oneclick-codex-result.json'),'utf8'));assert.equal(report.Configured,true);assert.equal(report.BridgeReady,true);assert.equal(report.Probe.waitingReceived,true);assert.equal(report.CodexReviewRequired,true);assert.equal(report.ToolsApprovalPolicyChanged,false);
 assert.equal(f.calls.filter(c=>c.url.startsWith('/approval')).length,0);assert.ok(f.calls.some(c=>c.body?.state==='thinking'));assert.ok(f.calls.some(c=>c.body?.state==='tool-wait'));assert.equal([...f.states.values()].at(-1).state,'idle');assert.ok(!r.out.includes('PRESERVE_FAKE_SECRET'));
 const ledger=JSON.parse(await readFile(path.join(f.data,'config','integrations.json'),'utf8'));assert.deepEqual(ledger.providers.antigravity,{enabled:false,target:'preserve-target',stamp:'preserve-stamp'});
});
test('repeat installation preserves other hooks and installs ten handlers without duplicates',async t=>{
 const f=await fixture(t);for(let i=0;i<2;i++){const r=await f.run();assert.equal(r.code,0,r.out+'\n'+r.err)}
 const bytes=await readFile(f.hooks);assert.notEqual(bytes.subarray(0,3).toString('hex'),'efbbbf');const hooks=JSON.parse(bytes);const handlers=Object.values(hooks.hooks).flatMap(groups=>groups.flatMap(g=>g.hooks));assert.equal(handlers.filter(h=>h.statusMessage?.startsWith('Alpha Island /')).length,10);assert.equal(handlers.find(h=>h.statusMessage==='other').command,'preserve-other-hook');
});
test('installed PowerShell hook command delivers from paths with spaces, Chinese, apostrophe and ampersand',async t=>{
 const f=await fixture(t);const r=await f.run();assert.equal(r.code,0,r.out+'\n'+r.err);const hooks=JSON.parse(await readFile(f.hooks,'utf8'));const command=hooks.hooks.UserPromptSubmit.flatMap(g=>g.hooks).find(h=>h.statusMessage==='Alpha Island / UserPromptSubmit').command;
 const match=command.match(/^powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "(.*)"$/);assert.ok(match);
 const before=f.calls.length;const launch=await run('powershell.exe',['-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-Command',match[1]],JSON.stringify({session_id:'installed-command-test',cwd:pack,hook_event_name:'UserPromptSubmit'}));assert.equal(launch.code,0,launch.err);assert.ok(f.calls.slice(before).some(c=>c.url==='/status'&&c.body?.state==='thinking'));
});
test('malformed original options stop repair and remain byte-identical',async t=>{
 const f=await fixture(t);const raw='{broken-secret';await writeFile(f.opts,raw);const hookBefore=await readFile(f.hooks);const r=await f.run();assert.notEqual(r.code,0);assert.equal(await readFile(f.opts,'utf8'),raw);assert.deepEqual(await readFile(f.hooks),hookBefore);assert.ok(!r.out.includes(raw));assert.equal(f.calls.length,0);
});
test('missing Codex configuration stops before changing settings',async t=>{
 const f=await fixture(t);const optionsBefore=await readFile(f.opts);const args=[...f.args];args[args.indexOf('-CodexDirectory')+1]=path.join(f.root,'missing-codex');const r=await run('powershell.exe',args);assert.notEqual(r.code,0);assert.deepEqual(await readFile(f.opts),optionsBefore);assert.equal(f.calls.length,0);
});
test('unreachable or wrong bridge cannot be reported as fully connected',async t=>{
 const f=await fixture(t);f.offline();const r=await f.run();assert.notEqual(r.code,0);const result=JSON.parse(await readFile(path.join(f.data,'logs','oneclick-codex-result.json'),'utf8'));assert.equal(result.Configured,true);assert.equal(result.BridgeReady,false);assert.equal(result.Probe,null);assert.ok(result.Error);
});
test('tampered runtime stops before changing Codex hooks or island options',async t=>{
 const f=await fixture(t);const bad=path.join(f.root,'bad-package');await mkdir(path.join(bad,'payload','agent-integrations','runtime'),{recursive:true});
 await cp(path.join(pack,'Repair-CodexIsland.ps1'),path.join(bad,'Repair-CodexIsland.ps1'));await cp(path.join(pack,'payload','Collect-CodexIslandDiagnostics.ps1'),path.join(bad,'payload','Collect-CodexIslandDiagnostics.ps1'));
 await writeFile(path.join(bad,'payload','agent-integrations','setup.ps1'),'# fixture, never executed');await writeFile(path.join(bad,'payload','agent-integrations','runtime','node.exe'),'invalid runtime, never executed');
 const beforeOptions=await readFile(f.opts),beforeHooks=await readFile(f.hooks);const args=[...f.args];args[args.indexOf('-File')+1]=path.join(bad,'Repair-CodexIsland.ps1');const r=await run('powershell.exe',args);
 assert.notEqual(r.code,0);assert.deepEqual(await readFile(f.opts),beforeOptions);assert.deepEqual(await readFile(f.hooks),beforeHooks);assert.equal(f.calls.length,0);
});
