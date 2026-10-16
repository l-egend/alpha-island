import test from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import { once } from 'node:events';
import { mkdtemp, readFile, writeFile, rm } from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const repo = fileURLToPath(new URL('../../', import.meta.url));

test('Installed Codex command reports a new task before any tool event and preserves its trusted launcher',
  { skip: process.platform !== 'win32', timeout: 20000 }, async t => {
    const root = await mkdtemp(path.join(os.tmpdir(), 'alpha startup 空格 & () '));
    const settings = path.join(root, 'config');
    const events = []; let started = 0;
    const server = http.createServer(async (request, response) => {
      let raw = ''; for await (const chunk of request) raw += chunk;
      events.push({ milliseconds: performance.now() - started, body: JSON.parse(raw) });
      response.setHeader('Content-Type', 'application/json'); response.end('{}');
    });
    server.listen(0, '127.0.0.1'); await once(server, 'listening');
    t.after(async () => {
      server.closeAllConnections(); await new Promise(resolve => server.close(resolve));
      assert.equal(path.dirname(path.resolve(root)), path.resolve(os.tmpdir()));
      assert.ok(path.basename(root).startsWith("alpha startup "));
      await rm(root, { recursive: true, force: true });
    });
    const install = spawn('powershell.exe', ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File',
      path.join(repo, 'examples/CodexHooks/install.ps1'), '-NodePath', process.execPath,
      '-CodexDirectory', root, '-ConfigDirectory', settings, '-AllProjects', '-Port', String(server.address().port)],
      { windowsHide: true, stdio: 'pipe' });
    let errors = ''; install.stderr.on('data', data => errors += data); install.stdout.resume();
    assert.equal((await once(install, 'exit'))[0], 0, errors);
    const hooks = JSON.parse((await readFile(path.join(root, 'hooks.json'), 'utf8')).replace(/^\uFEFF/, ''));
    const command = hooks.hooks.UserPromptSubmit[0].hooks[0].command;
    const latencies = [];
    for (let index = 0; index < 3; index++) {
      const before = events.length; started = performance.now();
      const child = spawn('cmd.exe', ['/d', '/s', '/c', command],
        { windowsHide: true, windowsVerbatimArguments: true, stdio: 'pipe' });
      let output = ''; let stderr = '';
      child.stdout.on('data', data => output += data); child.stderr.on('data', data => stderr += data);
      child.stdin.end(JSON.stringify({ session_id: `latency-${index}`, cwd: root, hook_event_name: 'UserPromptSubmit', prompt: 'PRIVATE_STARTUP_FIXTURE' }));
      assert.equal((await once(child, 'exit'))[0], 0, stderr);
      assert.deepEqual(JSON.parse(output.trim()), {});
      assert.equal(events.length, before + 1);
      assert.equal(events.at(-1).body.state, 'thinking');
      assert.equal(events.at(-1).body.beginsActivity, true);
      assert.ok(!JSON.stringify(events).includes('PRIVATE_STARTUP_FIXTURE'));
      latencies.push(events.at(-1).milliseconds);
    }
    console.log('TIMING: installed-codex-new-task-ms=' + latencies.map(value => value.toFixed(1)).join(','));
  });
