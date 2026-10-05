import { createHash, randomUUID } from 'node:crypto';
import { mkdir, readFile, writeFile, unlink, readdir, appendFile, stat } from 'node:fs/promises';
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
  if (!path.isAbsolute(config.projectRoot) || !path.isAbsolute(config.runtimeDir)) throw new Error('Absolute paths required');
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
  const payload = { source: 'codex', session: key, state, detail, ttlSec: state === 'idle' ? 5 : 300 };
  await request(config, '/status', payload, 650).catch(() => {});
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
  if (!input || typeof input.session_id !== 'string' || !input.session_id || !inScope(input.cwd, config.projectRoot)) return {};
  const key = sessionKey(input);
  const event = input.hook_event_name;
  if (event === 'SessionStart' && input.source === 'compact') {
    await report(config, key, 'thinking', 'Codex · 整理上下文', configPath, background);
    return {};
  }
  if (idleEvents.has(event)) {
    await cancelSession(config, key);
    await report(config, key, 'idle', '', configPath, background);
    return {};
  }
  if (activeEvents.has(event)) {
    const detail = event === 'PreToolUse' ? 'Codex · 执行工具' : event === 'PreCompact' ? 'Codex · 整理上下文' : 'Codex · 思考中';
    await report(config, key, 'thinking', detail, configPath, background);
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
      return decision('allow');
    return decision('deny', result.timedOut ? '岛端审批超时' : '岛端未允许本次操作');
  } catch { return decision('deny', '岛端审批已取消或连接中断'); }
  finally {
    signal?.removeEventListener('abort', cancel);
    const stillActive = await readFile(marker, 'utf8').then(() => true).catch(() => false);
    await request(config, '/approval/' + id, undefined, 650, 'DELETE').catch(() => {});
    await unlink(marker).catch(() => {});
    if (stillActive && !signal?.aborted) await report(config, key, 'thinking', 'Codex · 思考中', configPath, background).catch(() => {});
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
        scoped: inScope(input.cwd, config.projectRoot), devicePath: String(input.cwd).startsWith('\\\\?\\') }) + '\n');
  }
  const output = await handleHook(input, config, { configPath, background: true, signal: controller.signal });
  process.stdout.write(JSON.stringify(output) + '\n');
}

if (process.argv[1] && path.resolve(process.argv[1]) === ownPath)
  main().catch(() => { process.stdout.write('{}\n'); });
