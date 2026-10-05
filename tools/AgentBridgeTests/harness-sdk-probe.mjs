// Uses the installed SDK and existing Harness login. Does not read or print credentials.
import { spawn } from 'node:child_process';
import { createInterface } from 'node:readline';
import { readFile, writeFile, unlink } from 'node:fs/promises';
import { randomUUID } from 'node:crypto';
import { fileURLToPath } from 'node:url';

const [entry, home, patch, reportFile, mode] = process.argv.slice(2);
if (!entry || !home || !patch || !reportFile) throw new Error('Usage: node harness-sdk-probe.mjs <dsh-bin.js> <dsh-home> <patch.yml> <report.json>');
const report = { initialized: false, promptAccepted: false, reply: '', events: [], states: [], toolCalls: 0, approvalOutcomes: [] };
const probePatch = reportFile + '.patch.yml';
const pluginPath = fileURLToPath(new URL('../../examples/DeepSeekHarnessPlugin/index.mjs', import.meta.url)).replaceAll('\\', '/');
let patchText = (await readFile(patch, 'utf8')).replace('%ALPHA_ISLAND_HARNESS_PLUGIN%', pluginPath);
if (mode === '--approval-probe') {
  const approvalPath = fileURLToPath(new URL('./approval-probe.mjs', import.meta.url)).replaceAll('\\', '/');
  patchText += `\n- insert:\n    - id: alpha-island-approval-probe\n      name: '${approvalPath}'\n`;
}
await writeFile(probePatch, patchText);
const child = spawn(process.execPath, [entry, '--profile', 'sdk', '--patch', probePatch], {
  cwd: process.cwd(), env: { ...process.env, DSH_HOME: home }, windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'],
});
const waiting = new Map();
let id = 0, turnDone;
const finished = new Promise(resolve => { turnDone = resolve; });
let stderrBytes = 0;
child.stderr.on('data', data => { stderrBytes += data.length; }); // Never print provider credentials from startup logs.
child.on('exit', code => {
  report.exitCode = code;
  for (const request of waiting.values()) request.reject(new Error('Harness process exited'));
  waiting.clear(); turnDone();
});
child.on('error', () => turnDone());
createInterface({ input: child.stdout }).on('line', line => {
  let msg;
  try { msg = JSON.parse(line); } catch { return; }
  if (msg.id && waiting.has(msg.id)) {
    const req = waiting.get(msg.id); waiting.delete(msg.id);
    if (msg.error) req.reject(new Error('Harness RPC rejected (' + msg.error.code + ')'));
    else req.resolve(msg.result);
  }
  if (msg.method === 'session.status') report.states.push(msg.params.status);
  if (msg.method !== 'session.event') return;
  const event = msg.params.event;
  if (!report.events.includes(event.type)) report.events.push(event.type);
  if (event.type === 'tool/call') report.toolCalls++;
  if (event.type === 'approval/decided') report.approvalOutcomes.push(event.data.outcome);
  if (event.type === 'assistant/message') {
    const blocks = event.data.message?.content || event.data.message?.contentBlocks || [];
    report.reply += blocks.filter(block => block.type === 'text').map(block => block.text).join('');
  }
  if (event.type === 'turn/end') { report.turnEndReason = event.data.reason; turnDone(); }
});
function rpc(method, params, timeoutMs = 45000) {
  const requestId = ++id;
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => { waiting.delete(requestId); reject(new Error('Harness RPC timed out')); }, timeoutMs);
    waiting.set(requestId, { resolve: data => { clearTimeout(timer); resolve(data); }, reject: error => { clearTimeout(timer); reject(error); } });
    child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id: requestId, method, ...(params === undefined ? {} : { params }) }) + '\n');
  });
}
try {
  const info = await rpc('initialize', { cwd: process.cwd(), provider: 'deepseek-official', model: 'deepseek-flash', maxTokens: 128 });
  report.initialized = true; report.server = info.serverInfo;
  process.stdout.write('Harness SDK initialized.\n');
  await rpc('session/prompt', { sessionId: 'alpha-island-probe-' + randomUUID(), contentBlocks: [{ type: 'text', text: '这是 Alpha Island 的连接测试。不要调用任何工具，不要读取或修改文件，不要执行命令。只回复：连接测试成功' }] });
  report.promptAccepted = true;
  process.stdout.write('Harmless connection prompt accepted.\n');
  const deadline = setTimeout(() => { report.timedOut = true; turnDone(); }, 90000);
  await finished; clearTimeout(deadline);
} catch (error) { report.error = error.message; }
finally {
  try { await rpc('shutdown', undefined, 10000); } catch { child.kill(); }
  report.stderrBytes = stderrBytes;
  await writeFile(reportFile, JSON.stringify(report, null, 2));
  await unlink(probePatch).catch(() => {});
  // The report only includes our new test response and protocol status, never prior sessions.
  process.stdout.write(JSON.stringify(report) + '\n');
  if (!report.initialized || !report.promptAccepted || report.timedOut || report.error || !report.reply.trim() || report.toolCalls
      || (mode === '--approval-probe' && !report.approvalOutcomes.length)) process.exitCode = 1;
}
