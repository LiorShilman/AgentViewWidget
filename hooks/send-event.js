#!/usr/bin/env node
'use strict';

/**
 * Universal Claude Code hook — reads the hook JSON from stdin and forwards a
 * minimal, privacy-safe payload to the local state server.
 *
 * Usage (in .claude/settings.json):
 *   node <path>/hooks/send-event.js <EventType>
 *
 * Observability only: never blocks, never fails, always exits 0.
 */

const http = require('http');

const EVENT_TYPE = process.argv[2] || 'Unknown';
const PORT = 4577;

function extractFilePath(toolInput) {
  if (!toolInput || typeof toolInput !== 'object') return null;
  return (
    toolInput.file_path ||
    toolInput.notebook_path ||
    toolInput.path ||
    toolInput.pattern ||
    null
  );
}

let input = '';
process.stdin.setEncoding('utf8');
process.stdin.on('data', (chunk) => (input += chunk));
process.stdin.on('end', () => {
  let event = {};
  try {
    event = JSON.parse(input || '{}');
  } catch {
    /* malformed input — send the event type alone */
  }

  const payload = {
    type: EVENT_TYPE,
    sessionId: event.session_id || null,
    cwd: event.cwd || null,
    timestamp: Date.now(),
  };

  switch (EVENT_TYPE) {
    case 'UserPromptSubmit':
      payload.prompt = String(event.prompt || '').slice(0, 300);
      break;
    case 'PreToolUse':
    case 'PostToolUse':
      payload.toolName = event.tool_name || null;
      // Privacy: only file/target names — never file contents or full commands.
      payload.filePath =
        extractFilePath(event.tool_input) ||
        (event.tool_input && event.tool_input.description
          ? String(event.tool_input.description).slice(0, 80)
          : null);
      // Flag writes into a persistent-memory folder so the widget can show a
      // distinct "saved to memory" indicator instead of a generic file edit.
      payload.isMemoryWrite =
        (event.tool_name === 'Write' || event.tool_name === 'Edit') &&
        typeof payload.filePath === 'string' &&
        /[\\/]memory[\\/]/i.test(payload.filePath);
      break;
    case 'PostToolUseFailure':
      // PostToolUse only fires on success — a failed tool call raises this
      // event instead, so failure detection needs its own hook, not a flag.
      payload.toolName = event.tool_name || null;
      payload.filePath =
        extractFilePath(event.tool_input) ||
        (event.tool_input && event.tool_input.description
          ? String(event.tool_input.description).slice(0, 80)
          : null);
      payload.error = String(event.error || '').slice(0, 200);
      break;
    case 'Notification':
      payload.message = String(event.message || '').slice(0, 200);
      break;
    case 'PreCompact':
      payload.trigger = event.trigger || null;
      break;
    case 'Stop':
      // Same privacy treatment as the prompt: truncated preview only, never
      // the full turn (which may contain code or other sensitive content).
      payload.lastResponse = String(event.last_assistant_message || '').slice(0, 300);
      break;
    case 'SessionEnd':
      payload.reason = event.reason || null;
      break;
  }

  const body = JSON.stringify(payload);
  const req = http.request(
    {
      hostname: '127.0.0.1',
      port: PORT,
      path: '/event',
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'Content-Length': Buffer.byteLength(body),
      },
      timeout: 400, // never stall Claude Code if the server is down
    },
    (res) => res.resume()
  );

  req.on('timeout', () => req.destroy());
  req.on('error', () => {}); // silent failure — the widget is optional
  req.write(body);
  req.end();
});

// Absolute safety net: this hook must never block Claude Code.
setTimeout(() => process.exit(0), 600).unref();
process.on('exit', () => {});
