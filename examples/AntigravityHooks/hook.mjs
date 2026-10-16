import { createHash, randomUUID } from 'node:crypto';
import { mkdir, readFile, writeFile, unlink, readdir, open, realpath } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const ownPath = fileURLToPath(import.meta.url);
export const approvalTools = /^(run_command|write_to_file|replace_file_content|multi_replace_file_content|ask_permission|manage_task|schedule|mcp_.+)$/;

export function validateConfig(raw) {
  const endpoint = new URL(raw.endpoint || 'http://127.0.0.1:8765/');
  if (endpoint.protocol !== 'http:' || !['127.0.0.1', 'localhost', '[::1]'].includes(endpoint.hostname)
      || endpoint.username || endpoint.password || endpoint.search || endpoint.hash || endpoint.pathname !== '/')
    throw new Error('Endpoint must be a loopback HTTP root URL');
  if (!path.isAbsolute(raw.runtimeDir)) throw new Error('Absolute runtimeDir required');
  if (raw.approvalMode !== undefined && !['auto', 'confirm'].includes(raw.approvalMode)) throw new Error('Unsupported approval mode');
  if (raw.nativeConfigPath !== undefined && !path.isAbsolute(raw.nativeConfigPath)) throw new Error('Absolute nativeConfigPath required');
  if (raw.hookPath !== undefined && !path.isAbsolute(raw.hookPath)) throw new Error('Absolute hookPath required');
  return { endpoint: endpoint.href, runtimeDir: raw.runtimeDir, timeoutSec: Math.min(120, Math.max(5, Number(raw.timeoutSec) || 120)),
    // Older manually configured adapters retain confirmation; the installer explicitly selects auto.
    approvalMode: raw.approvalMode ?? 'confirm',
    nativeConfigPath: raw.nativeConfigPath ?? path.join(raw.hookPath ? path.dirname(raw.hookPath) : path.join(os.homedir(), '.gemini', 'config'), 'config.json') };
}

const object = value => value !== null && typeof value === 'object' && !Array.isArray(value);
const inheritsTerminal = value => value === undefined || value === 'CASCADE_COMMANDS_AUTO_EXECUTION_UNSPECIFIED';
const inheritsFiles = value => value === undefined || value === 'AGENT_SETTING_POLICY_UNSPECIFIED';
const inheritsPreset = value => value === undefined || value === 'AGENT_PERMISSION_PRESET_UNSPECIFIED';
const fileTools = new Set(['write_to_file', 'replace_file_content', 'multi_replace_file_content']);

async function readPolicyJson(file, limit) {
  const handle = await open(file, 'r');
  try {
    const stat = await handle.stat();
    if (!stat.isFile() || stat.size > limit) throw new Error('Unsupported native settings');
    // Bound the read itself too: a file may grow after stat while Antigravity saves settings.
    const buffer = Buffer.alloc(limit + 1);
    let used = 0;
    while (used < buffer.length) {
      const { bytesRead } = await handle.read(buffer, used, buffer.length - used, null);
      if (!bytesRead) break;
      used += bytesRead;
    }
    if (used > limit) throw new Error('Native settings too large');
    const result = JSON.parse(buffer.subarray(0, used).toString('utf8').replace(/^\uFEFF/, ''));
    if (!object(result)) throw new Error('Invalid native settings');
    return result;
  } finally { await handle.close(); }
}

function normalizeFolder(value) {
  if (typeof value !== 'string' || !value || value.length > 32768 || value.includes('\0')) return null;
  try {
    if (/^file:/i.test(value)) {
      const uri = new URL(value);
      if (uri.search || uri.hash || uri.username || uri.password) return null;
      value = fileURLToPath(uri);
    }
    if (!path.isAbsolute(value)) return null;
    // Reject drive-relative and device paths rather than guessing their workspace scope.
    if (process.platform === 'win32' && (!/^(?:[a-z]:[\\/]|\\\\[^\\]+\\[^\\]+)/i.test(value) || /^\\\\[?.]\\/.test(value))) return null;
    let normalized = path.normalize(value);
    while (normalized.length > path.parse(normalized).root.length && normalized.endsWith(path.sep)) normalized = normalized.slice(0, -1);
    return process.platform === 'win32' ? normalized.toLowerCase() : normalized;
  } catch { return null; }
}

function within(folder, target) {
  const relative = path.relative(folder, target);
  return relative === '' || (relative !== '..' && !relative.startsWith('..' + path.sep) && !path.isAbsolute(relative));
}

async function projectSettings(directory) {
  const files = [], pending = [{ directory, depth: 0 }];
  let entryCount = 0;
  while (pending.length) {
    const next = pending.pop();
    let entries;
    try { entries = await readdir(next.directory, { withFileTypes: true }); }
    catch (error) { if (error.code === 'ENOENT' && next.depth === 0) return []; throw error; }
    entryCount += entries.length;
    if (entryCount > 256) throw new Error('Too many native project settings');
    for (const entry of entries) {
      const file = path.join(next.directory, entry.name);
      if (entry.isDirectory()) {
        if (next.depth >= 3) throw new Error('Unsupported native project layout');
        pending.push({ directory: file, depth: next.depth + 1 });
      } else if (/\.json$/i.test(entry.name)) {
        if (!entry.isFile() || files.length >= 128) throw new Error('Unsupported native project settings');
        files.push(file);
      }
    }
  }
  return Promise.all(files.map(file => readPolicyJson(file, 64 * 1024)));
}

async function nativePolicies(config, input) {
  if (!Array.isArray(input.workspacePaths) || input.workspacePaths.length > 32) return null;
  const roots = input.workspacePaths.map(normalizeFolder);
  if (roots.some(root => root === null)) return null;
  const global = await readPolicyJson(config.nativeConfigPath, 1024 * 1024);
  if (!object(global.userSettings) || !inheritsPreset(global.userSettings.permissionPreset)) return null;
  const base = { terminal: global.userSettings.autoExecutionPolicy, files: global.userSettings.nonWorkspaceFileAccessPolicy };
  const policies = [];
  for (const project of await projectSettings(path.join(path.dirname(config.nativeConfigPath), 'projects'))) {
    if (typeof project.id !== 'string' || (project.settings !== undefined && !object(project.settings))) return null;
    const settings = project.settings ?? {};
    if (inheritsTerminal(settings.autoExecutionPolicy) && inheritsFiles(settings.fileAccessPolicy) && inheritsPreset(settings.permissionPreset)) continue;
    const resources = project.projectResources?.resources;
    if (!Array.isArray(resources) || resources.length === 0 || resources.length > 32) return null;
    const folders = resources.map(resource => normalizeFolder(resource?.folderUri));
    if (folders.some(folder => folder === null)) return null;
    const exact = folders.every(folder => roots.includes(folder)) && roots.every(root => folders.includes(root));
    if (!exact) {
      // A partial multi-root match cannot identify the active project reliably from this payload.
      if (!roots.length || folders.some(folder => roots.some(root => within(folder, root) || within(root, folder)))) return null;
      continue;
    }
    if (!inheritsPreset(settings.permissionPreset)) return null;
    policies.push({ terminal: inheritsTerminal(settings.autoExecutionPolicy) ? base.terminal : settings.autoExecutionPolicy,
      files: inheritsFiles(settings.fileAccessPolicy) ? base.files : settings.fileAccessPolicy });
  }
  return { roots, policies: policies.length ? policies : [base] };
}

async function resolveNewFile(target) {
  const suffix = [];
  for (let depth = 0; depth < 32; depth++) {
    try { return normalizeFolder(path.join(await realpath(target), ...suffix)); }
    catch (error) { if (error.code !== 'ENOENT') return null; }
    const parent = path.dirname(target);
    if (parent === target) return null;
    suffix.unshift(path.basename(target)); target = parent;
  }
  return null;
}

async function followsNativePolicy(config, input) {
  if (config.approvalMode !== 'auto') return false;
  const name = input.toolCall.name;
  if (name !== 'run_command' && !fileTools.has(name)) return false;
  try {
    const snapshot = await nativePolicies(config, input);
    if (!snapshot) return false;
    if (name === 'run_command') return snapshot.policies.every(policy => policy.terminal === 'CASCADE_COMMANDS_AUTO_EXECUTION_EAGER');
    const target = normalizeFolder(input.toolCall.args?.TargetFile);
    if (!target) return false;
    if (snapshot.policies.every(policy => policy.files === 'AGENT_SETTING_POLICY_ALLOW')) return true;
    const resolved = await resolveNewFile(target);
    if (!resolved) return false;
    for (const root of snapshot.roots) {
      const canonicalRoot = await realpath(root).then(normalizeFolder).catch(() => null);
      if (canonicalRoot && within(canonicalRoot, resolved)) return true;
    }
  } catch { /* Missing, changing, unknown or corrupt native settings retain the extra confirmation. */ }
  return false;
}

export function redact(value, depth = 0) {
  if (depth > 8) return '[省略]';
  if (Array.isArray(value)) return value.slice(0, 32).map(item => redact(item, depth + 1));
  if (value && typeof value === 'object') return Object.fromEntries(Object.entries(value).slice(0, 64).map(([key, item]) =>
    [key, /key|token|secret|password|authorization|cookie/i.test(key) ? '[已隐藏]' : redact(item, depth + 1)]));
  if (typeof value !== 'string') return value;
  return value.replace(/Bearer\s+[^\s"']+/gi, 'Bearer [已隐藏]')
    .replace(/((?:api[_-]?key|token|password|secret|authorization)\s*[=:]\s*)(?:"[^"]*"|'[^']*'|[^\s,;]+)/gi, '$1[已隐藏]')
    .replace(/(--?(?:api[_-]?key|token|password|secret)\s+)(?:"[^"]*"|'[^']*'|[^\s;]+)/gi, '$1[已隐藏]').slice(0, 2000);
}

async function request(config, route, body, timeoutMs = 650, method = 'POST', signal) {
  const response = await fetch(new URL(route, config.endpoint), {
    method, headers: body === undefined ? {} : { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
    signal: signal ? AbortSignal.any([signal, AbortSignal.timeout(timeoutMs)]) : AbortSignal.timeout(timeoutMs),
  });
  if (!response.ok) throw new Error('Bridge unavailable');
  return response.json();
}

async function report(config, session, state, detail) {
  await request(config, '/status', { source: 'antigravity', session, state, detail, ttlSec: state === 'idle' ? 5 : 300, reporterPid: process.pid,
    ...(config.artworkFamily ? { artworkFamily: config.artworkFamily } : {}) }).catch(() => {});
}

async function cancelSession(config, session) {
  const files = await readdir(config.runtimeDir).catch(() => []);
  await Promise.all(files.filter(file => file.startsWith(session + '-approval-') && file.endsWith('.json')).slice(0, 128).map(async file => {
    const marker = path.join(config.runtimeDir, file);
    try {
      const { id } = JSON.parse(await readFile(marker, 'utf8'));
      // Remove first: the waiting process must not restore 'thinking' after Stop clears it.
      await unlink(marker).catch(() => {});
      if (/^antigravity-[a-f0-9-]{36}$/.test(id)) await request(config, '/approval/' + id, undefined, 650, 'DELETE').catch(() => {});
    } finally { await unlink(marker).catch(() => {}); }
  }));
}

const native = reason => ({ decision: 'ask', reason });
const denied = reason => ({ decision: 'deny', reason });

export async function handleHook(event, input, rawConfig, { signal } = {}) {
  const config = validateConfig(rawConfig);
  if (!input || typeof input.conversationId !== 'string' || !input.conversationId || input.conversationId.length > 256) return {};
  // Forward a family tag only; never disclose model names, transcript paths or message text.
  const model = typeof input.modelName === 'string' ? input.modelName : typeof input.model === 'string' ? input.model : '';
  config.artworkFamily = /gemini/i.test(model) ? 'gemini' : /claude/i.test(model) ? 'claude'
    : /deepseek/i.test(model) ? 'deepseek' : /\b(gpt|codex|openai)\b/i.test(model) ? 'codex' : undefined;
  const session = createHash('sha256').update(input.conversationId).digest('hex').slice(0, 24);
  if (event === 'Stop') {
    await cancelSession(config, session);
    await report(config, session, input.fullyIdle === false ? 'working' : 'idle', input.fullyIdle === false ? 'Antigravity · 后台任务运行中' : '');
    return {};
  }
  if (event === 'PreInvocation' || event === 'PostInvocation' || event === 'PostToolUse') {
    await report(config, session, event === 'PostInvocation' ? 'streaming' : 'thinking', event === 'PostToolUse' ? 'Antigravity · 思考中' : event === 'PostInvocation' ? 'Antigravity · 模型响应' : 'Antigravity · 思考中');
    return {};
  }
  if (event !== 'PreToolUse' || typeof input.toolCall?.name !== 'string') return {};
  if (!approvalTools.test(input.toolCall.name)) {
    await report(config, session, 'working', 'Antigravity · 执行工具');
    return {};
  }
  if (signal?.aborted) return denied('请求已取消');
  if (await followsNativePolicy(config, input)) {
    if (signal?.aborted) return denied('请求已取消');
    await report(config, session, 'working', 'Antigravity · 执行工具');
    if (signal?.aborted) return denied('请求已取消');
    // "allow" delegates to the next hook/default policy. Never auto_approve or override native rules.
    return { decision: 'allow', reason: 'Alpha Island 跟随 Antigravity 设置；由原生权限规则继续判断' };
  }
  // The payload has no native permission-needed flag. This is an additional tool confirmation.
  try {
    const health = await request(config, '/health', undefined, 650, 'GET', signal);
    if (health.service !== 'alpha-island-agent-bridge' || health.statusReporting !== true || health.apiDecisions !== false)
      return native('Alpha Island 未接管，继续原生审批');
  } catch { return signal?.aborted ? denied('请求已取消') : native('Alpha Island 离线，继续原生审批'); }
  const id = 'antigravity-' + randomUUID();
  const marker = path.join(config.runtimeDir, session + '-approval-' + id + '.json');
  const controller = new AbortController();
  const cancel = () => { controller.abort(); void request(config, '/approval/' + id, undefined, 650, 'DELETE').catch(() => {}); };
  signal?.addEventListener('abort', cancel, { once: true });
  let submitted = false, allowed = false;
  try {
    if (signal?.aborted) return denied('请求已取消');
    await mkdir(config.runtimeDir, { recursive: true });
    await writeFile(marker, JSON.stringify({ id }));
    await report(config, session, 'tool-wait', 'Antigravity · 等待工具确认');
    if (signal?.aborted) return denied('请求已取消');
    submitted = true;
    const result = await request(config, '/approval', {
      id, source: 'antigravity', session, tool: 'Antigravity · ' + input.toolCall.name.slice(0, 80),
      args: redact(input.toolCall.args || {}), reason: '本次工具执行确认；允许后继续 Antigravity 原生权限检查', timeoutSec: config.timeoutSec,
    }, (config.timeoutSec + 3) * 1000, 'POST', controller.signal);
    if (signal?.aborted) return denied('请求已取消');
    if (result.id === id && result.approved === true && result.timedOut === false && ['mouse', 'key'].includes(result.decidedBy))
      { allowed = true; return native('用户已在 Alpha Island 确认本次操作；继续原生权限检查'); }
    return denied(result.timedOut ? 'Alpha Island 确认超时' : 'Alpha Island 未允许本次操作');
  } catch { return denied('Alpha Island 确认已取消或连接中断'); }
  finally {
    signal?.removeEventListener('abort', cancel);
    const stillActive = await readFile(marker, 'utf8').then(() => true).catch(() => false);
    if (submitted) await request(config, '/approval/' + id, undefined, 650, 'DELETE').catch(() => {});
    await unlink(marker).catch(() => {});
    if (stillActive && !signal?.aborted) await report(config, session, allowed ? 'working' : 'thinking', allowed ? 'Antigravity · 执行工具' : 'Antigravity · 思考中');
  }
}

async function main() {
  const configPath = process.argv[process.argv.indexOf('--config') + 1];
  const event = process.argv[process.argv.indexOf('--event') + 1];
  if (!process.argv.includes('--config') || !process.argv.includes('--event')) throw new Error('Hook arguments required');
  const config = JSON.parse((await readFile(configPath, 'utf8')).replace(/^\uFEFF/, ''));
  let raw = '';
  for await (const chunk of process.stdin) { raw += chunk; if (raw.length > 1024 * 1024) throw new Error('Input too large'); }
  const controller = new AbortController();
  process.once('SIGINT', () => controller.abort()); process.once('SIGTERM', () => controller.abort());
  process.stdout.write(JSON.stringify(await handleHook(event, JSON.parse(raw), config, { signal: controller.signal })) + '\n');
}

if (process.argv[1] && path.resolve(process.argv[1]) === ownPath)
  main().catch(() => { process.stdout.write(JSON.stringify(process.argv.includes('PreToolUse') ? denied('Alpha Island Hook 配置或输入无效') : {}) + '\n'); });
