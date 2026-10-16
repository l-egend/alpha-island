import {createHash, randomUUID} from 'node:crypto';
import {pathToFileURL} from 'node:url';
const [adapter,configPath]=process.argv.slice(2);
const {loadConfig,handleHook}=await import(pathToFileURL(adapter).href);
const configured=await loadConfig(configPath);const config={...configured,approvalMode:'native'};
const session_id='alpha-oneclick-'+randomUUID();const session=createHash('sha256').update(session_id).digest('hex').slice(0,24);
const input={session_id,cwd:process.cwd()};
async function ownState(){const r=await fetch(new URL('/status',config.endpoint),{signal:AbortSignal.timeout(2000)});if(!r.ok)throw new Error('StatusUnavailable');return (await r.json()).connections.find(c=>c.source==='codex'&&c.session===session)?.state;}
try {
 await handleHook({...input,hook_event_name:'UserPromptSubmit'},config);
 if(await ownState()!=='thinking')throw new Error('ThinkingNotReceived');
 await new Promise(resolve=>setTimeout(resolve,350));
 const output=await handleHook({...input,hook_event_name:'PermissionRequest',tool_name:'AlphaIslandConnectionTest',tool_input:{}},config);
 if(await ownState()!=='tool-wait'||JSON.stringify(output)!=='{}')throw new Error('WaitingStateNotReceived');
 await new Promise(resolve=>setTimeout(resolve,350));
 console.log(JSON.stringify({thinkingReceived:true,waitingReceived:true,nativeApprovalUnchanged:true,realToolExecuted:false}));
} catch {console.log(JSON.stringify({thinkingReceived:false,waitingReceived:false,error:'BridgeStatusProbeFailed'}));process.exitCode=1;}
finally {await handleHook({...input,hook_event_name:'Stop'},config).catch(()=>{});}