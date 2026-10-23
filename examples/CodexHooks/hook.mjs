import { createHash, randomUUID } from 'node:crypto';
import { mkdir, readFile, writeFile, unlink, readdir, appendFile, stat, open, rename } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const ownPath = fileURLToPath(import.meta.url);
const idleEvents = new Set(['SessionStart', 'SessionEnd', 'Stop', 'Interrupt']);
const activeEvents = new Set(['UserPromptSubmit', 'PreToolUse', 'PostToolUse', 'PreCompact', 'PostCompact']);

export function validateConfig(config) {
  const endpoint = new URL(config.endpoint || 'http://127.0.0.1:8765/');
  if (endpoint.protocol !== 'http:' || !['127.0.0.1', 'localhost', '[::1]'].includes(endpoint.hostname)
      || endpoint.username || endpoint.password || endpoint.search || endpoint.hash || endpoint.pathname !== '/')
    throw new Error('Endpoint must be a loopback HTTP root URL');
  if (config.allProjects !== undefined && typeof config.allProjects !== 'boolean') throw new Error('Invalid project scope');
  if (typeof config.runtimeDir !== 'string' || !path.isAbsolute(config.runtimeDir)
      || (config.allProjects !== true && (typeof config.projectRoot !== 'string' || !path.isAbsolute(config.projectRoot))))
    throw new Error('Absolute paths required');
  const approvalMode = config.approvalMode ?? 'native';
  if (!['native', 'island'].includes(approvalMode)) throw new Error('Approval mode must be native or island');
  return { ...config, endpoint: endpoint.href, approvalMode, timeoutSec: Math.min(120, Math.max(5, Number(config.timeoutSec) || 120)) };
}

// 兼容旧版已信任的 --config 路径，只跟随一层本地绝对路径指针。
export async function loadConfig(configPath) {
  let config = JSON.parse((await readFile(configPath, 'utf8')).replace(/^\uFEFF/, ''));
  if (typeof config.configPath === 'string') {
    if (!path.isAbsolute(config.configPath) || path.resolve(config.configPath) === path.resolve(configPath))
      throw new Error('Invalid configuration pointer');
    config = JSON.parse((await readFile(config.configPath, 'utf8')).replace(/^\uFEFF/, ''));
    if (config.configPath !== undefined) throw new Error('Nested configuration pointers are not supported');
  }
  return validateConfig(config);
}

export function inScope(cwd, projectRoot) {
  if (typeof cwd !== 'string' || !path.isAbsolute(cwd)) return false;
  const normalize = value => process.platform === 'win32' && value.startsWith('\\\\?\\')
    ? (value.slice(4, 8).toUpperCase() === 'UNC\\' ? '\\\\' + value.slice(8) : value.slice(4)) : value;
  cwd = normalize(cwd);
  projectRoot = normalize(projectRoot);
  const relative = path.relative(projectRoot, cwd);
  return relative === '' || (!relative.startsWith('..' + path.sep) && relative !== '..' && !path.isAbsolute(relative));
}

export function isInScope(cwd, config) {
  return typeof cwd === 'string' && path.isAbsolute(cwd) && (config.allProjects === true || inScope(cwd, config.projectRoot));
}

export function redact(value, depth = 0) {
  if (depth > 8) return '[省略]';
  if (Array.isArray(value)) return value.slice(0, 32).map(item => redact(item, depth + 1));
  if (value && typeof value === 'object') return Object.fromEntries(Object.entries(value).slice(0, 64).map(([key, item]) =>
    [key, /key|token|secret|password|authorization|cookie/i.test(key) ? '[已隐藏]' : redact(item, depth + 1)]));
  if (typeof value !== 'string') return value;
  return value.replace(/Bearer\s+[^\s"']+/gi, 'Bearer [已隐藏]')
    .replace(/((?:api[_-]?key|token|password|secret|authorization)\s*[=:]\s*)(?:"[^"]*"|'[^']*'|[^\s,;]+)/gi, '$1[已隐藏]')
    .replace(/(--?(?:api[_-]?key|token|password|secret)\s+)(?:"[^"]*"|'[^']*'|[^\s;]+)/gi, '$1[已隐藏]')
    .replace(/Bearer\s+[^\s"']+/gi, 'Bearer [已隐藏]').slice(0, 2000);
}

const sessionKey = input => createHash('sha256').update(input.session_id).digest('hex').slice(0, 24);
async function conversationIdentity(input) {
  const fallback = sessionKey(input);
  if (typeof input.transcript_path !== 'string' || !path.isAbsolute(input.transcript_path)) return { session: fallback, nativeThreadId: input.session_id };
  let file;
  try {
    file = await open(input.transcript_path, 'r');
    const buffer = Buffer.alloc(512 * 1024);
    const { bytesRead } = await file.read(buffer, 0, buffer.length, 0);
    // Only native metadata is inspected. Message records and the path never leave this process.
    const records = buffer.subarray(0, bytesRead).toString('utf8').replace(/^\uFEFF/, '').split('\n').slice(0, 4);
    for (const line of records) {
      let record; try { record = JSON.parse(line); } catch { continue; }
      if (record.type !== 'session_meta') continue;
      const meta = record.payload;
      if (typeof meta?.id !== 'string' || !/^[a-zA-Z0-9_.:-]{1,256}$/.test(meta.id)) break;
      const hash = value => createHash('sha256').update(value).digest('hex').slice(0, 24);
      const frontend = typeof meta.session_id === 'string' && /^[a-zA-Z0-9_.:-]{1,256}$/.test(meta.session_id) ? meta.session_id : '';
      const child = meta.source === 'subagent' || (meta.source && typeof meta.source === 'object' && 'subagent' in meta.source);
      // Keep execution lifetimes separate, then group them under the stable frontend.
      // One finished worker must not clear another worker in the same conversation.
      const id = !frontend && child && meta.parent_thread_id === input.session_id ? input.session_id : meta.id;
      const session = hash(id);
      const parentId = frontend && frontend !== id ? frontend : child && typeof meta.parent_thread_id === 'string'
        && /^[a-zA-Z0-9_.:-]{1,256}$/.test(meta.parent_thread_id) && meta.parent_thread_id !== id ? meta.parent_thread_id : undefined;
      const parent = parentId ? hash(parentId) : undefined;
      return { session, nativeThreadId: meta.id, ...(session !== fallback && parent !== fallback ? { sessionAlias: fallback } : {}), ...(parent ? { parentSession: parent } : {}), startsRootActivity: !parent || Boolean(frontend) };
    }
  } catch { /* Missing/partial metadata uses the existing bounded transport identity. */ }
  finally { await file?.close().catch(() => {}); }
  return { session: fallback, nativeThreadId: input.session_id };
}
const decision = (behavior, message) => ({ hookSpecificOutput: { hookEventName: 'PermissionRequest', decision: { behavior, ...(message ? { message } : {}) } } });

async function request(config, route, body, timeoutMs = 1000, method = 'POST', signal) {
  const response = await fetch(new URL(route, config.endpoint), {
    method, headers: body === undefined ? {} : { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
    signal: signal ? AbortSignal.any([signal, AbortSignal.timeout(timeoutMs)]) : AbortSignal.timeout(timeoutMs),
  });
  if (!response.ok) throw new Error('Bridge request failed');
  return response.json();
}

async function report(config, key, state, detail, configPath, background) {
  const payload = { source: 'codex', session: key, state, detail, ttlSec: state === 'idle' ? 5 : 300, reporterPid: process.pid, statusEventId: randomUUID(), reportedAtUnixMs: Date.now(), ...(config.statusIdentity || {}) };
  // A missed loopback delivery must not drop the only start/stop signal. The
  // island drains this bounded, metadata-only outbox on its existing status tick.
  let pending;
  try {
    const directory = path.join(config.runtimeDir, 'status-events');
    await mkdir(directory, { recursive: true });
    const names = (await readdir(directory)).filter(name => /^\d{13}-[a-f0-9-]{36}\.(?:json|tmp)$/.test(name)).sort();
    await Promise.all(names.slice(0, Math.max(0, names.length - 126)).map(name => unlink(path.join(directory, name)).catch(() => {})));
    const name = Date.now() + '-' + randomUUID();
    const temporary = path.join(directory, name + '.tmp');
    pending = path.join(directory, name + '.json');
    await writeFile(temporary, JSON.stringify({ version: 1, reportedAtUnixMs: Date.now(), payload }));
    await rename(temporary, pending);
  } catch { /* Reporting still works if the optional outbox is not writable. */ }
  try {
    await request(config, '/status', payload, 650);
    if (pending) await unlink(pending).catch(() => {});
  } catch { /* A failed delivery stays available for local recovery. */ }
}

async function cancelSession(config, key) {
  const files = await readdir(config.runtimeDir).catch(() => []);
  await Promise.all(files.filter(file => file.startsWith(key + '-approval-') && file.endsWith('.json')).slice(0, 128).map(async file => {
    try {
      const { id } = JSON.parse(await readFile(path.join(config.runtimeDir, file), 'utf8'));
      if (/^codex-[a-f0-9-]{36}$/.test(id)) await request(config, '/approval/' + id, undefined, 650, 'DELETE').catch(() => {});
    } finally { await unlink(path.join(config.runtimeDir, file)).catch(() => {}); }
  }));
}

export async function handleHook(input, rawConfig, { configPath, background = false, signal } = {}) {
  const config = validateConfig(rawConfig);
  if (!input || typeof input.session_id !== 'string' || !input.session_id || !isInScope(input.cwd, config)) return {};
  const { startsRootActivity = true, ...identity } = await conversationIdentity(input);
  const event = input.hook_event_name;
  const key = identity.session;
  config.statusIdentity = { ...identity,
    ...(typeof input.turn_id === 'string' && /^[a-zA-Z0-9_.:-]{1,64}$/.test(input.turn_id) ? { nativeTurnId: input.turn_id } : {}), ...(event === 'UserPromptSubmit' && startsRootActivity ? { beginsActivity: true } : {}),
    ...(event === 'SessionStart' ? { connectOnly: true } : {}) };
  if (idleEvents.has(event)) {
    if (event !== 'SessionStart') await cancelSession(config, key);
    await report(config, key, 'idle', '', configPath, background);
    return {};
  }
  if (activeEvents.has(event)) {
    const detail = event === 'PreToolUse' ? 'Codex · 执行工具' : event === 'PreCompact' ? 'Codex · 整理上下文' : 'Codex · 思考中';
    await report(config, key, event === 'PreToolUse' ? 'working' : 'thinking', detail, configPath, background);
    return {};
  }
  if (event !== 'PermissionRequest' || typeof input.tool_name !== 'string' || !input.tool_name) return {};
  // Return no decision so Codex can display its own approval, including to Remote clients.
  // Only status is mirrored: no island approval queue, tool arguments, or background waiter.
  if (config.approvalMode === 'native') {
    if (!signal?.aborted)
      await report(config, key, 'tool-wait', 'Codex · 请在对话中审批', configPath, background);
    return {};
  }
  // Declining to decide preserves Codex's normal approval prompt when the island is offline.
  try {
    const health = await request(config, '/health', undefined, 650, 'GET', signal);
    if (health.service !== 'alpha-island-agent-bridge' || health.statusReporting !== true || health.apiDecisions !== false) return {};
  } catch { return {}; }
  const id = 'codex-' + randomUUID();
  const marker = path.join(config.runtimeDir, key + '-approval-' + id + '.json');
  const controller = new AbortController();
  const cancel = () => {
    controller.abort();
    void request(config, '/approval/' + id, undefined, 650, 'DELETE').catch(() => {});
  };
  signal?.addEventListener('abort', cancel, { once: true });
  let allowed = false;
  try {
    if (signal?.aborted) return decision('deny', 'Codex 请求已取消');
    await mkdir(config.runtimeDir, { recursive: true });
    await writeFile(marker, JSON.stringify({ id }));
    await report(config, key, 'tool-wait', 'Codex · 等待授权', configPath, background);
    const args = redact(input.tool_input ?? {});
    const result = await request(config, '/approval', {
      id, tool: 'Codex · ' + input.tool_name.slice(0, 80), args,
      reason: typeof args?.description === 'string' ? args.description.slice(0, 160) : 'Codex 请求本次工具权限', timeoutSec: config.timeoutSec,
    }, (config.timeoutSec + 3) * 1000, 'POST', controller.signal);
    if (result.id === id && result.approved === true && result.timedOut === false && ['mouse', 'key'].includes(result.decidedBy))
      { allowed = true; return decision('allow'); }
    return decision('deny', result.timedOut ? '岛端审批超时' : '岛端未允许本次操作');
  } catch { return decision('deny', '岛端审批已取消或连接中断'); }
  finally {
    signal?.removeEventListener('abort', cancel);
    const stillActive = await readFile(marker, 'utf8').then(() => true).catch(() => false);
    await request(config, '/approval/' + id, undefined, 650, 'DELETE').catch(() => {});
    await unlink(marker).catch(() => {});
    if (stillActive && !signal?.aborted) await report(config, key, allowed ? 'working' : 'thinking', allowed ? 'Codex · 执行工具' : 'Codex · 思考中', configPath, background).catch(() => {});
  }
}

async function main() {
  const configPath = process.argv[process.argv.indexOf('--config') + 1];
  if (!configPath || !process.argv.includes('--config')) { process.stdout.write('{}\n'); return; }
  const config = await loadConfig(configPath);
  let raw = '';
  for await (const chunk of process.stdin) { raw += chunk; if (raw.length > 2 * 1024 * 1024) throw new Error('Hook input too large'); }
  const controller = new AbortController();
  const cancel = () => controller.abort();
  process.once('SIGINT', cancel); process.once('SIGTERM', cancel);
  const input = JSON.parse(raw);
  if (config.trace === true) {
    await mkdir(config.runtimeDir, { recursive: true });
    const tracePath = path.join(config.runtimeDir, 'hook-trace.jsonl');
    if ((await stat(tracePath).catch(() => ({ size: 0 }))).size < 32768)
      await appendFile(tracePath, JSON.stringify({ event: input.hook_event_name, keys: Object.keys(input),
        scoped: isInScope(input.cwd, config), devicePath: String(input.cwd).startsWith('\\\\?\\') }) + '\n');
  }
  const output = await handleHook(input, config, { configPath, background: true, signal: controller.signal });
  process.stdout.write(JSON.stringify(output) + '\n');
}

if (process.argv[1] && path.resolve(process.argv[1]) === ownPath)
  main().catch(() => { process.stdout.write('{}\n'); });
