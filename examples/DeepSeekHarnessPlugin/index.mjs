import { createHash, randomUUID } from 'node:crypto';

export const name = 'alpha-island-agent';
export const inject = ['agents', 'approval'];

export function redact(value) {
  if (Array.isArray(value)) return value.map(redact);
  if (value && typeof value === 'object') return Object.fromEntries(Object.entries(value).map(([key, item]) =>
    [key, /key|token|secret|password|authorization|cookie/i.test(key) ? '[已隐藏]' : redact(item)]));
  if (typeof value !== 'string') return value;
  return value.replace(/((?:api[_-]?key|token|password|secret|authorization)\s*[=:]\s*)[^\s,;]+/gi, '$1[已隐藏]')
    .replace(/Bearer\s+[^\s"']+/gi, 'Bearer [已隐藏]');
}

// No chat text, credentials, or model configuration is forwarded to the island.
export function apply(ctx, config = {}) {
  const endpoint = new URL(config.endpoint || 'http://127.0.0.1:8765/');
  if (endpoint.protocol !== 'http:' || !['127.0.0.1', 'localhost', '[::1]'].includes(endpoint.hostname)
      || endpoint.username || endpoint.password || endpoint.search || endpoint.hash || endpoint.pathname !== '/')
    throw new Error('Alpha Island endpoint must be a loopback HTTP root URL');
  const timeoutSec = Math.min(600, Math.max(5, Number(config.timeoutSec) || 120));
  const source = 'deepseek-harness';
  const active = new Map();
  const calls = new Map();
  const pending = new Map();
  const approvals = new Map();
  const processSession = `p${process.pid}-connection`;
  let disposed = false;
  let worker;
  const sessionId = agent => `p${process.pid}-${createHash('sha256').update(String(agent.id)).digest('hex').slice(0, 20)}`;
  async function request(path, body, signal, timeoutMs = 1800, method = 'POST') {
    const response = await fetch(new URL(path, endpoint), {
      method, headers: body === undefined ? {} : { 'Content-Type': 'application/json' },
      body: body === undefined ? undefined : JSON.stringify(body),
      signal: signal ? AbortSignal.any([signal, AbortSignal.timeout(timeoutMs)]) : AbortSignal.timeout(timeoutMs),
    });
    if (!response.ok) throw new Error(`Island HTTP ${response.status}`);
    return response.json();
  }
  function schedule(session, state, detail) {
    if (disposed) return;
    pending.set(session, { source, session, state, detail, ttlSec: state === 'idle' && session !== processSession ? 5 : 30 });
    if (worker) return;
    worker = (async () => {
      while (pending.size) {
        const [key, payload] = pending.entries().next().value;
        pending.delete(key);
        try { await request('/status', payload); } catch { /* Heartbeat retries while the island is unavailable. */ }
      }
    })().finally(() => { worker = undefined; });
  }
  function set(agent, state, detail) {
    const id = sessionId(agent);
    if (state === 'idle') active.delete(id);
    else active.set(id, { state, detail });
    schedule(id, state, detail);
  }
  function heartbeat() {
    schedule(processSession, 'idle', 'DeepSeek Harness 已连接');
    for (const [id, value] of active) schedule(id, value.state, value.detail);
  }
  ctx.on('agent/status', ({ agent, status }) => set(agent,
    status === 'running' ? 'thinking' : 'idle', status === 'running' ? 'DeepSeek Harness · 思考中' : ''));
  ctx.on('agent/disposed', ({ agent }) => set(agent, 'idle', ''));
  ctx.on('agent/assistant-stream', ({ agent, frame }) => {
    if (frame.type === 'chunk' && active.get(sessionId(agent))?.state === 'thinking')
      set(agent, 'streaming', 'DeepSeek Harness · 输出中');
  });
  ctx.on('session/event', (session, event) => {
    const agent = { id: session.id };
    if (event.type === 'step/start') set(agent, 'thinking', 'DeepSeek Harness · 思考中');
    if (event.type === 'tool/call') {
      if (calls.size >= 128) calls.delete(calls.keys().next().value);
      calls.set(`${session.id}/${event.data.callId}`, event.data.arguments);
      set(agent, 'thinking', 'DeepSeek Harness · 执行工具');
    }
    if (event.type === 'tool/result') calls.delete(`${session.id}/${event.data.callId}`);
    if (event.type === 'turn/end') set(agent, 'idle', '');
  });
  ctx.on('approval/request', async (req, next) => {
    if (disposed || req.signal?.aborted) return 'cancelled';
    // Offline islands leave the original Harness approval UI available.
    try {
      const health = await request('/health', undefined, req.signal, 1000, 'GET');
      if (health.service !== 'alpha-island-agent-bridge' || !health.statusReporting || health.apiDecisions)
        return next();
    } catch { return req.signal?.aborted ? 'cancelled' : next(); }
    const id = `dsh-${randomUUID()}`;
    const controller = new AbortController();
    approvals.set(id, controller);
    const cancel = () => {
      controller.abort();
      void request(`/approval/${id}`, undefined, undefined, 1800, 'DELETE').catch(() => {});
    };
    req.signal?.addEventListener('abort', cancel, { once: true });
    if (req.signal?.aborted) cancel();
    set(req.agent, 'tool-wait', `DeepSeek Harness · 等待授权：${req.toolName}`);
    let args = calls.get(`${req.agent.id}/${req.callId}`) || { callId: req.callId || '' };
    try { if (typeof args === 'string') args = JSON.parse(args); } catch { /* Keep malformed model arguments visible. */ }
    try {
      const result = await request('/approval', { id, tool: req.toolName,
        args: redact(args), reason: redact(req.reason || 'DeepSeek Harness 工具授权'), timeoutSec },
      controller.signal, (timeoutSec + 3) * 1000);
      if (req.signal?.aborted || disposed) return 'cancelled';
      return result.approved === true && result.timedOut !== true && ['mouse', 'key'].includes(result.decidedBy) ? 'allowed-once' : 'rejected';
    } catch {
      cancel();
      return req.signal?.aborted || disposed ? 'cancelled' : 'unavailable';
    } finally {
      approvals.delete(id);
      req.signal?.removeEventListener('abort', cancel);
      if (!disposed) set(req.agent, req.agent.status === 'idle' ? 'idle' : 'thinking', 'DeepSeek Harness · 思考中');
    }
  }, true);
  for (const agent of ctx.agents.list())
    if (agent.status === 'running') set(agent, 'thinking', 'DeepSeek Harness · 执行中');
  heartbeat();
  const timer = setInterval(heartbeat, 5000);
  timer.unref?.();
  ctx.effect(() => async () => {
    disposed = true;
    clearInterval(timer);
    pending.clear();
    const cleanup = [];
    for (const [id, controller] of approvals) {
      controller.abort();
      cleanup.push(request(`/approval/${id}`, undefined, undefined, 1800, 'DELETE').catch(() => {}));
    }
    if (worker) await worker;
    for (const id of [...active.keys(), processSession])
      cleanup.push(request('/status', { source, session: id, state: 'idle', detail: '', ttlSec: 5 }).catch(() => {}));
    await Promise.allSettled(cleanup);
    active.clear();
    calls.clear();
  });
}
