import { spawn } from 'node:child_process';
import readline from 'node:readline';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { writeFile, readFile, readdir } from 'node:fs/promises';
import os from 'node:os';
import { loadConfig } from '../../examples/CodexHooks/hook.mjs';

const exe = process.argv[2];
if (!exe) throw new Error('Pass the Codex executable path');
const server = spawn(exe, ['app-server', '--stdio'], { cwd: process.cwd(), windowsHide: true, stdio: 'pipe' });
server.stderr.resume();
const pending = new Map();
let sequence = 0;
let completed;
let completedResolve;
const observedHooks = [];
let reply = '';
let toolCount = 0;
let connectionKey;
const observedStates = new Set();
const approvalProbe = process.argv.includes('--approval-probe');
const approvalIds = new Set();
const approvalResults = new Map();
const commandResults = [];
const nativeApprovalRequests = [];
let bridgeConfig;
let polling = false;
let pollTimer;
const complete = new Promise(resolve => completedResolve = resolve);
readline.createInterface({ input: server.stdout }).on('line', line => {
  try {
    const message = JSON.parse(line);
    if (message.method === 'hook/completed') {
      observedHooks.push(message.params.run);
      console.log(JSON.stringify({ hook: message.params.run.eventName, status: message.params.run.status,
        entries: message.params.run.entries }));
    }
    if (message.method === 'turn/completed') { completed = message.params.turn; completedResolve(); }
    if (message.method === 'item/completed' && message.params.item.type === 'agentMessage') reply = message.params.item.text;
    if (message.method === 'item/started' && ['commandExecution', 'fileChange', 'mcpToolCall', 'dynamicToolCall'].includes(message.params.item.type)) toolCount++;
    if (message.method === 'item/completed' && message.params.item.type === 'commandExecution')
      commandResults.push({ status: message.params.item.status, exitCode: message.params.item.exitCode });
    if (message.id !== undefined && message.method) {
      // The probe never grants a request that falls back to the native approval UI.
      const decline = () => server.stdin.write(JSON.stringify({ id: message.id, result: { decision: 'decline' } }) + '\n');
      if (approvalProbe && bridgeConfig?.approvalMode === 'native' && message.method.endsWith('/requestApproval')) {
        nativeApprovalRequests.push(message.method);
        // Observe the island while the real native request is still pending, then reject it.
        void fetch(new URL('/status', bridgeConfig.endpoint), { signal: AbortSignal.timeout(700) })
          .then(r => r.json()).then(status => {
            const own = status.connections.find(c => c.source === 'codex' && c.session === connectionKey);
            if (own) observedStates.add(own.state);
          }).catch(() => {}).finally(decline);
      } else decline();
    }
    if (message.id !== undefined && pending.has(message.id)) {
      const { resolve, reject, timer } = pending.get(message.id); pending.delete(message.id); clearTimeout(timer);
      if (message.error) reject(new Error(message.error.message)); else resolve(message.result);
    }
  } catch { }
});
function rpc(method, params) {
  return new Promise((resolve, reject) => {
    const id = ++sequence;
    const timer = setTimeout(() => { pending.delete(id); reject(new Error('RPC timeout: ' + method)); }, 15000);
    pending.set(id, { resolve, reject, timer });
    server.stdin.write(JSON.stringify({ id, method, params }) + '\n');
  });
}
try {
  await rpc('initialize', { clientInfo: { name: 'alpha_island_hook_check', version: '0.1.0' },
    capabilities: { experimentalApi: true, requestAttestation: false } });
  server.stdin.write('{"method":"initialized"}\n');
  const response = await rpc('hooks/list', { cwds: [process.cwd()] });
  // Print only this adapter's metadata, never configuration values, credentials or messages.
  for (const entry of response.data) {
    const own = entry.hooks.filter(h => h.statusMessage?.startsWith('Alpha Island /'));
    console.log(JSON.stringify({ cwd: entry.cwd, errors: entry.errors.length, warnings: entry.warnings, hooks: own.map(h => ({
      event: h.eventName, enabled: h.enabled, trust: h.trustStatus, source: path.basename(h.sourcePath), timeout: h.timeoutSec,
    })) }));
  }
  if (process.argv.includes('--probe') || approvalProbe) {
    const configPath = path.join(process.env.CODEX_HOME || path.join(os.homedir(), '.codex'), 'alpha-island/config.json');
    bridgeConfig = await loadConfig(configPath);
    const { thread } = await rpc('thread/start', { cwd: process.cwd(), ephemeral: true, sandbox: 'read-only',
      approvalPolicy: approvalProbe ? 'on-request' : 'never', ...(approvalProbe ? { approvalsReviewer: 'user' } : {}) });
    connectionKey = createHash('sha256').update(thread.id).digest('hex').slice(0, 24);
    const poll = async () => {
      if (polling) return; polling = true;
      try {
        const response = await fetch(new URL('/status', bridgeConfig.endpoint), { signal: AbortSignal.timeout(700) });
        const status = await response.json();
        const own = status.connections.find(c => c.source === 'codex' && c.session === connectionKey);
        if (own) observedStates.add(own.state);
        if (approvalProbe) {
          const files = await readdir(bridgeConfig.runtimeDir).catch(() => []);
          for (const file of files.filter(f => f.startsWith(connectionKey + '-approval-') && f.endsWith('.json'))) {
            const marker = await readFile(path.join(bridgeConfig.runtimeDir, file), 'utf8').then(JSON.parse).catch(() => null);
            if (marker) approvalIds.add(marker.id);
          }
          for (const id of approvalIds) {
            const result = await fetch(new URL('/approval/' + id, bridgeConfig.endpoint), { signal: AbortSignal.timeout(700) });
            if (result.status === 200) {
              const value = await result.json();
              approvalResults.set(id, { approved: value.approved, timedOut: value.timedOut, decidedBy: value.decidedBy });
            }
          }
        }
      } catch { } finally { polling = false; }
    };
    pollTimer = setInterval(poll, 100);
    const prompt = approvalProbe
      ? "用 exec_command 执行一次 Write-Output 'ALPHA_ISLAND_APPROVAL_PROBE'，显式传入 sandbox_permissions='require_escalated' 和 justification='岛端权限连接测试，只输出固定文字'。只申请一次，不重试。最后只回复权限测试完成。"
      : '只回复连接测试成功，不要调用工具或读写文件。';
    await rpc('turn/start', { threadId: thread.id, input: [{ type: 'text', text: prompt, text_elements: [] }] });
    await Promise.race([complete, new Promise((_, reject) => { const timer = setTimeout(() => reject(new Error('Turn timeout')), 60000); timer.unref(); })]);
    await poll();
    const summary = { turnStatus: completed.status, hooks: observedHooks.map(h => ({ event: h.eventName, status: h.status })),
      states: [...observedStates], reply: reply.slice(0, 200), toolCount,
      ...(approvalProbe ? { approvalMode: bridgeConfig.approvalMode ?? 'native', nativeApprovalRequests,
        approvals: [...approvalResults.values()], commandResults } : {}) };
    const nativeApproval = approvalProbe && (bridgeConfig.approvalMode ?? 'native') === 'native';
    await writeFile(path.join(process.cwd(), nativeApproval ? 'bin/codex-native-approval-probe.json'
      : approvalProbe ? 'bin/codex-approval-probe.json' : 'bin/codex-live-probe.json'), JSON.stringify(summary, null, 2));
    console.log(JSON.stringify(summary));
    if (summary.hooks.some(h => h.status !== 'completed' && !(approvalProbe && h.event === 'permissionRequest' && h.status === 'blocked'))
      || !observedStates.has('thinking') || !observedStates.has('idle')
      || (approvalProbe ? !summary.hooks.some(h => h.event === 'permissionRequest')
        || (nativeApproval ? !nativeApprovalRequests.length || !observedStates.has('tool-wait')
          || approvalIds.size > 0 || commandResults.some(c => c.exitCode === 0) : !approvalResults.size) : toolCount))
      process.exitCode = 1;
  }
} finally { clearInterval(pollTimer); server.stdin.end(); server.kill(); }
