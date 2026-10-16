import { createHash } from 'node:crypto';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

export function validateConfig(raw) {
  const endpoint = new URL(raw.endpoint || 'http://127.0.0.1:8765/');
  if (endpoint.protocol !== 'http:' || !['127.0.0.1', 'localhost', '[::1]'].includes(endpoint.hostname)
      || endpoint.username || endpoint.password || endpoint.search || endpoint.hash || endpoint.pathname !== '/')
    throw new Error('Endpoint must be a loopback HTTP root URL');
  return { endpoint: endpoint.href };
}

// Only state is mirrored. User prompts, transcripts, tool arguments and replies are never forwarded.
export async function handleHook(input, rawConfig) {
  const config = validateConfig(rawConfig);
  if (!input || typeof input.session_id !== 'string' || !input.session_id || input.session_id.length > 256) return {};
  const event = input.hook_event_name;
  // A child's tool/stop events must not clear its parent's independently displayed conversation.
  if (input.agent_id || event === 'SubagentStart' || event === 'SubagentStop') return {};
  let state, detail, ttlSec = 300;
  if (event === 'SessionEnd' || event === 'Stop' || event === 'SessionStart' && input.source !== 'compact') {
    state = 'idle'; detail = ''; ttlSec = 5;
    if (event === 'Stop' && Array.isArray(input.background_tasks) && input.background_tasks.some(t => t?.status === 'running'))
      { state = 'working'; detail = 'Claude Code · 后台任务运行中'; ttlSec = 300; }
  } else if (event === 'PermissionRequest' || event === 'Elicitation'
      || event === 'PreToolUse' && input.tool_name === 'AskUserQuestion') {
    state = 'tool-wait'; detail = 'Claude Code · 请在会话中确认';
  } else if (event === 'StopFailure') {
    state = 'error'; detail = 'Claude Code · 本轮运行出错'; ttlSec = 15;
  } else if (event === 'Notification') {
    if (['permission_prompt', 'elicitation_dialog', 'elicitation_url_dialog', 'agent_needs_input'].includes(input.notification_type))
      { state = 'tool-wait'; detail = 'Claude Code · 等待输入或授权'; }
    else if (input.notification_type === 'idle_prompt') { state = 'idle'; detail = ''; ttlSec = 5; }
    else return {};
  } else if (['UserPromptSubmit', 'PreToolUse', 'PostToolUse', 'PostToolUseFailure', 'PreCompact', 'PostCompact', 'SessionStart', 'ElicitationResult'].includes(event)) {
    state = event === 'PreToolUse' ? 'working' : 'thinking';
    detail = event.includes('Compact') || event === 'SessionStart' ? 'Claude Code · 整理上下文'
      : event === 'PreToolUse' ? 'Claude Code · 执行工具' : 'Claude Code · 思考中';
  } else return {};
  const session = createHash('sha256').update(input.session_id).digest('hex').slice(0, 24);
  try {
    await fetch(new URL('status', config.endpoint), {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ source: 'claude-code', session, state, detail, ttlSec, reporterPid: process.pid }),
      signal: AbortSignal.timeout(650),
    });
  } catch { /* The native permission prompt and tools continue when the island is offline. */ }
  return {};
}

async function main() {
  const index = process.argv.indexOf('--config');
  if (index < 0 || !process.argv[index + 1]) return;
  const config = JSON.parse((await readFile(process.argv[index + 1], 'utf8')).replace(/^\uFEFF/, ''));
  let raw = '';
  for await (const chunk of process.stdin) { raw += chunk; if (raw.length > 2 * 1024 * 1024) return; }
  await handleHook(JSON.parse(raw), config);
}
if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url))
  main().catch(() => {}); // Silence preserves Claude's original output and native approval semantics.
