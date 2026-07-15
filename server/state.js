'use strict';

/**
 * Multi-project in-memory state store + event reducer.
 * Tracks one AgentState per project (keyed by normalized cwd) so the widget
 * can show a tab per project. Mirrors the shapes consumed by the WPF widget.
 */

const MAX_EVENTS = 20;
const MAX_PROJECTS = 6;
const MAX_FILES_TOUCHED = 12;

// Tools that actually modify a file on disk — Read/Grep/Glob/Bash don't count
// as "changed", they're just observation.
const FILE_MODIFYING_TOOLS = new Set(['Edit', 'Write', 'MultiEdit', 'NotebookEdit']);

function initialProjectState() {
  return {
    sessionId: null,
    status: 'offline', // idle | thinking | running_tool | waiting_approval | offline
    currentTool: null, // { name, filePath, startedAt }
    lastPrompt: null,
    lastResponse: null,
    sessionStartedAt: null,
    memoryPressure: 'normal', // normal | high
    recentEvents: [], // [{ type, label, timestamp }] newest first
    filesTouched: [], // [filePath] most-recently-touched first, deduplicated
    projectPath: null,
    lastMemorySaveAt: null, // bumped whenever a Write/Edit targets a memory/ folder
    lastEventAt: null,
  };
}

function truncate(text, max) {
  if (!text) return '';
  const clean = String(text).replace(/\s+/g, ' ').trim();
  return clean.length > max ? clean.slice(0, max - 1) + '…' : clean;
}

function baseName(p) {
  if (!p) return null;
  const parts = String(p).split(/[\\/]/).filter(Boolean);
  return parts.length ? parts[parts.length - 1] : null;
}

/** Normalizes a cwd into a stable map key regardless of slash style/casing. */
function normalizeKey(cwd) {
  if (!cwd) return 'unknown';
  return String(cwd).replace(/\\/g, '/').replace(/\/+$/, '').toLowerCase();
}

function pushEvent(state, type, icon, label, timestamp) {
  state.recentEvents.unshift({ type, icon, label, timestamp });
  if (state.recentEvents.length > MAX_EVENTS) {
    state.recentEvents.length = MAX_EVENTS;
  }
}

/** Moves filePath to the front of filesTouched (deduped, case-insensitive), capped. */
function touchFile(state, filePath) {
  if (!filePath) return;
  const key = String(filePath).toLowerCase();
  state.filesTouched = state.filesTouched.filter((f) => f.toLowerCase() !== key);
  state.filesTouched.unshift(filePath);
  if (state.filesTouched.length > MAX_FILES_TOUCHED) {
    state.filesTouched.length = MAX_FILES_TOUCHED;
  }
}

/** Applies one event to a single project's state (pure per-project reducer). */
function reduceProject(state, ev) {
  const ts = ev.timestamp || Date.now();
  state.lastEventAt = ts;

  if (ev.cwd) {
    state.projectPath = ev.cwd;
  }

  // Bootstrap the session clock from the first observed event when hooks were
  // wired up mid-session (so no SessionStart was ever seen) — better an
  // approximate elapsed time than a frozen "--:--".
  if (state.sessionStartedAt === null && ev.type !== 'SessionEnd') {
    state.sessionStartedAt = ts;
  }

  switch (ev.type) {
    case 'SessionStart': {
      state.sessionId = ev.sessionId || null;
      state.projectPath = ev.cwd || state.projectPath;
      state.sessionStartedAt = ts;
      state.status = 'idle';
      state.memoryPressure = 'normal';
      state.currentTool = null;
      state.filesTouched = [];
      pushEvent(state, ev.type, '●', 'Session started', ts);
      break;
    }

    case 'UserPromptSubmit': {
      state.status = 'thinking';
      state.lastPrompt = ev.prompt || null;
      pushEvent(state, ev.type, '❯', truncate(ev.prompt, 60), ts);
      break;
    }

    case 'PreToolUse': {
      state.status = 'running_tool';
      state.currentTool = {
        name: ev.toolName || 'Tool',
        filePath: ev.filePath || null,
        startedAt: ts,
      };
      const target = baseName(ev.filePath);
      pushEvent(
        state,
        ev.type,
        '▶',
        target ? `${ev.toolName} · ${target}` : ev.toolName,
        ts
      );
      break;
    }

    case 'PostToolUse': {
      let durationLabel = '';
      if (state.currentTool && state.currentTool.name === (ev.toolName || 'Tool')) {
        const seconds = (ts - state.currentTool.startedAt) / 1000;
        if (seconds >= 0 && seconds < 3600) {
          durationLabel = ` · ${seconds.toFixed(1)}s`;
        }
      }
      state.currentTool = null;
      state.status = 'thinking';
      const target = baseName(ev.filePath);

      if (ev.isMemoryWrite) {
        state.lastMemorySaveAt = ts;
        pushEvent(state, ev.type, '✦', `Saved to memory · ${target || 'note'}${durationLabel}`, ts);
        break;
      }

      if (FILE_MODIFYING_TOOLS.has(ev.toolName) && ev.filePath) {
        touchFile(state, ev.filePath);
      }

      pushEvent(
        state,
        ev.type,
        '✓',
        target ? `${ev.toolName} · ${target}${durationLabel}` : `${ev.toolName}${durationLabel}`,
        ts
      );
      break;
    }

    case 'PostToolUseFailure': {
      state.currentTool = null;
      state.status = 'thinking';
      const target = baseName(ev.filePath);
      const errorSnippet = ev.error ? ` — ${truncate(ev.error, 50)}` : '';
      pushEvent(
        state,
        ev.type,
        '✗',
        target
          ? `${ev.toolName} failed · ${target}${errorSnippet}`
          : `${ev.toolName} failed${errorSnippet}`,
        ts
      );
      break;
    }

    case 'Notification': {
      state.status = 'waiting_approval';
      pushEvent(state, ev.type, '◆', truncate(ev.message || 'Attention needed', 60), ts);
      break;
    }

    case 'PreCompact': {
      state.memoryPressure = 'high';
      pushEvent(state, ev.type, '⚠', 'Context compaction imminent', ts);
      break;
    }

    case 'Stop': {
      state.status = 'idle';
      state.currentTool = null;
      state.lastResponse = ev.lastResponse || null;
      pushEvent(state, ev.type, '✓', 'Turn completed', ts);
      break;
    }

    case 'SessionEnd': {
      state.status = 'offline';
      state.currentTool = null;
      state.sessionStartedAt = null;
      pushEvent(state, ev.type, '●', 'Session ended', ts);
      break;
    }

    default: {
      pushEvent(state, ev.type || 'Unknown', '·', truncate(ev.type || 'event', 40), ts);
      break;
    }
  }

  return state;
}

/** Creates an empty multi-project store. */
function createStore() {
  return {
    projects: new Map(), // key -> project state
    activeKey: null,
  };
}

/** Ingests one event into the store, creating/evicting projects as needed. */
function ingest(store, ev) {
  const key = normalizeKey(ev.cwd);
  let project = store.projects.get(key);
  if (!project) {
    project = initialProjectState();
    store.projects.set(key, project);
  }

  reduceProject(project, ev);
  store.activeKey = key;

  if (store.projects.size > MAX_PROJECTS) {
    let oldestKey = null;
    let oldestAt = Infinity;
    for (const [k, p] of store.projects) {
      if (k === key) continue; // never evict the project we just touched
      if ((p.lastEventAt || 0) < oldestAt) {
        oldestAt = p.lastEventAt || 0;
        oldestKey = k;
      }
    }
    if (oldestKey) store.projects.delete(oldestKey);
  }

  return store;
}

/** Runs the 5-minute idle watchdog across every tracked project. */
function applyWatchdog(store, idleAfterMs) {
  const now = Date.now();
  let changed = false;
  for (const project of store.projects.values()) {
    if (
      project.lastEventAt &&
      now - project.lastEventAt > idleAfterMs &&
      project.status !== 'idle' &&
      project.status !== 'offline'
    ) {
      project.status = 'idle';
      project.currentTool = null;
      changed = true;
    }
  }
  return changed;
}

/** Snapshot broadcast to widgets: every tracked project + which one is active. */
function publicSnapshot(store) {
  const projects = [];
  for (const [key, state] of store.projects) {
    const { lastEventAt, ...pub } = state;
    projects.push({ key, ...pub });
  }
  return { projects, activeProjectKey: store.activeKey };
}

module.exports = {
  createStore,
  ingest,
  applyWatchdog,
  publicSnapshot,
  normalizeKey,
  MAX_EVENTS,
  MAX_PROJECTS,
};
