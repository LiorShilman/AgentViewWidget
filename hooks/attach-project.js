#!/usr/bin/env node
'use strict';

/**
 * Attaches the Agent Live Widget's hooks to a target project's
 * .claude/settings.json — merging with any existing hooks (never
 * overwriting them) and skipping entries that are already present, so it's
 * safe to run more than once.
 *
 * Usage:
 *   node hooks/attach-project.js <path-to-project>
 */

const fs = require('fs');
const path = require('path');

const HOOK_SCRIPT = path.join(__dirname, 'send-event.js').replace(/\\/g, '/');

// matcher: needs "matcher": "*" (tool-related events only).
// async: fire-and-forget so the hook never adds latency to a tool call.
const EVENTS = [
  { name: 'SessionStart', matcher: false, async: false },
  { name: 'UserPromptSubmit', matcher: false, async: false },
  { name: 'PreToolUse', matcher: true, async: true },
  { name: 'PostToolUse', matcher: true, async: true },
  { name: 'PostToolUseFailure', matcher: true, async: true },
  { name: 'Notification', matcher: false, async: false },
  { name: 'PreCompact', matcher: false, async: false },
  { name: 'Stop', matcher: false, async: false },
  { name: 'SessionEnd', matcher: false, async: false },
];

const targetProject = process.argv[2];
if (!targetProject) {
  console.error('Usage: node attach-project.js <path-to-project>');
  process.exit(1);
}

const settingsDir = path.join(targetProject, '.claude');
const settingsPath = path.join(settingsDir, 'settings.json');
const settingsFileIsNew = !fs.existsSync(settingsPath);

fs.mkdirSync(settingsDir, { recursive: true });

let settings = { hooks: {} };
if (fs.existsSync(settingsPath)) {
  try {
    settings = JSON.parse(fs.readFileSync(settingsPath, 'utf8'));
  } catch (err) {
    console.error(`Could not parse existing ${settingsPath}: ${err.message}`);
    console.error('Fix or remove the file by hand, then re-run.');
    process.exit(1);
  }
}
if (!settings.hooks) settings.hooks = {};

let added = 0;
let alreadyPresent = 0;

for (const ev of EVENTS) {
  const command = `node "${HOOK_SCRIPT}" ${ev.name}`;
  if (!settings.hooks[ev.name]) settings.hooks[ev.name] = [];

  const exists = settings.hooks[ev.name].some((group) =>
    (group.hooks || []).some((h) => h.command === command)
  );

  if (exists) {
    alreadyPresent++;
    continue;
  }

  const hookEntry = { type: 'command', command };
  if (ev.async) hookEntry.async = true;

  const group = ev.matcher ? { matcher: '*', hooks: [hookEntry] } : { hooks: [hookEntry] };

  settings.hooks[ev.name].push(group);
  added++;
}

fs.writeFileSync(settingsPath, JSON.stringify(settings, null, 2) + '\n', 'utf8');

console.log(settingsPath);
console.log(`  ${added} hook(s) added, ${alreadyPresent} already present.`);

if (settingsFileIsNew) {
  // Claude Code's file-watcher reload is only documented for *edits* to an
  // existing settings.json. A brand-new file (this project had none before)
  // isn't covered by that guarantee if a session is already open here.
  console.log('This settings.json did not exist before — if a Claude Code session is');
  console.log('already open in this project, restart it so the new file is picked up.');
  console.log('(Editing an existing settings.json does NOT need a restart — only this');
  console.log('first-time-creation case does.)');
} else {
  console.log('Claude Code watches settings.json and reloads hooks automatically — no restart needed.');
}
