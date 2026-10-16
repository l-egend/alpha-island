import assert from 'node:assert/strict';
import http from 'node:http';
import { mkdtemp, mkdir, rm } from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const executable = process.argv[2];
assert.ok(executable && path.isAbsolute(executable), 'Supply the installed Claude executable');
const prefix = path.join(os.tmpdir(), 'alpha-claude-native-');
const root = await mkdtemp(prefix);
const home = path.join(root, 'config');
const reports = [];
const server = http.createServer(async (req,res) => {
  let body=''; for await (const chunk of req) body+=chunk;
  reports.push({route:req.url,...JSON.parse(body)});res.writeHead(200,{'Content-Type':'application/json'});res.end('{"ok":true}');
});
function run(file,args,env={}) {
  return new Promise((resolve,reject)=>{
    const child=spawn(file,args,{cwd:root,env:{...process.env,...env},windowsHide:true});
    let output='',error='';child.stdout.on('data',b=>output+=b);child.stderr.on('data',b=>error+=b);child.stdin.end();
    const timer=setTimeout(()=>{child.kill();reject(new Error('Native lifecycle probe timed out'));},20000);
    child.on('error',reject);child.on('close',code=>{clearTimeout(timer);resolve({code,output,error});});
  });
}
try {
  await mkdir(home);await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
  const version=await run(executable,['--version']);assert.equal(version.code,0);const nativeVersion=version.output.match(/\d+\.\d+\.\d+/)?.[0];
  const setup=fileURLToPath(new URL('../../examples/ClaudeCodeHooks/install.ps1',import.meta.url));
  const installed=await run('powershell.exe',['-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',setup,
    '-NodePath',process.execPath,'-ClaudeDirectory',home,'-ConfigDirectory',path.join(root,'island'),'-IntegrationDirectory',path.join(root,'adapter'),
    '-NativeVersion',nativeVersion,'-Port',String(server.address().port)]);
  assert.equal(installed.code,0,installed.error);
  // --init-only performs lifecycle initialization and exits without a model request or a submitted prompt.
  const result=await run(executable,['--init-only','--setting-sources','user'],{CLAUDE_CONFIG_DIR:home,DISABLE_AUTOUPDATER:'1',CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC:'1'});
  assert.equal(result.code,0,result.error);
  assert.ok(reports.some(r=>r.source==='claude-code'&&r.state==='idle'), 'Native CLI must actually execute the installed SessionStart hook');
  console.log(JSON.stringify({nativeVersion,nativeExit:result.code,events:reports.map(({route,source,state})=>({route,source,state})),modelRequests:0},null,2));
} finally {
  await new Promise(resolve=>{server.close(resolve);server.closeAllConnections();});
  assert.ok(path.resolve(root).startsWith(path.resolve(prefix)));await rm(root,{recursive:true,force:true});
}
