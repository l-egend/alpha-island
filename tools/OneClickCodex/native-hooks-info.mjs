import { spawn } from 'node:child_process';
import readline from 'node:readline';
const [exe, home, cwd] = process.argv.slice(2);
const child = spawn(exe, ['app-server', '--stdio', ...process.argv.slice(5)], {cwd, env: {...process.env, CODEX_HOME:home}, windowsHide:true, stdio:'pipe'});
child.stdin.on('error',()=>{}); child.stderr.resume(); const pending = new Map(); let sequence=0;
readline.createInterface({input:child.stdout}).on('line', line => {try {const m=JSON.parse(line);const call=pending.get(m.id);if(call){pending.delete(m.id);clearTimeout(call.timer);m.error?call.reject(new Error('Native Codex inspection failed')):call.resolve(m.result);}}catch{}});
child.on('error',()=>{for(const p of pending.values())p.reject(new Error('Codex executable unavailable'));});
function rpc(method,params){return new Promise((resolve,reject)=>{const id=++sequence;const timer=setTimeout(()=>{pending.delete(id);reject(new Error('Codex inspection timed out'));},7000);pending.set(id,{resolve,reject,timer});child.stdin.write(JSON.stringify({id,method,params})+'\n');});}
try {
 await rpc('initialize',{clientInfo:{name:'alpha_island_oneclick_check',version:'1.0.0'},capabilities:{experimentalApi:true,requestAttestation:false}});
 child.stdin.write('{"method":"initialized"}\n');
 let configurationChecked=false,hooksEnabled=null,userHooksAllowed=null;
 try {const {config}=await rpc('config/read',{includeLayers:false});configurationChecked=true;
 hooksEnabled=(config.features?.hooks??config.features?.codex_hooks??true)!==false;
 userHooksAllowed=config.allow_managed_hooks_only!==true&&config.hooks?.allow_managed_hooks_only!==true;}catch{}
 const response=await rpc('hooks/list',{cwds:[cwd]}); const handlers=[];let errors=0,warnings=0;
 for(const entry of response.data??[]){errors+=entry.errors?.length??0;warnings+=entry.warnings?.length??0;for(const h of entry.hooks??[]){if(h.statusMessage?.startsWith('Alpha Island /'))handlers.push({event:h.eventName,enabled:h.enabled===true,trust:h.trustStatus});}}
 console.log(JSON.stringify({available:true,configurationChecked,hooksEnabled,userHooksAllowed,errors,warnings,handlers}));
} catch {console.log(JSON.stringify({available:false,error:'CodexHooksInspectionUnavailable'}));process.exitCode=1;}
finally {for(const p of pending.values())clearTimeout(p.timer);child.stdin.end();child.kill();}
