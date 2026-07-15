'use strict';

/**
 * Agent Live Widget — Local State Server
 *
 * - POST /event  : receives lifecycle events from Claude Code hooks
 * - GET  /state  : current multi-project snapshot (JSON)
 * - WS   /live   : realtime snapshot broadcast to widgets
 *
 * Tracks one state per project (keyed by cwd) so the widget can show a tab
 * per project instead of a single global session.
 */

const http = require('http');
const { WebSocketServer } = require('ws');
const { createStore, ingest, applyWatchdog, publicSnapshot } = require('./state');

const PORT = 4577;
const WATCHDOG_IDLE_MS = 5 * 60 * 1000; // no events for 5 min -> idle

const store = createStore();

const wss = new WebSocketServer({ noServer: true });

function broadcast() {
  const json = JSON.stringify(publicSnapshot(store));
  for (const client of wss.clients) {
    if (client.readyState === 1 /* OPEN */) {
      client.send(json);
    }
  }
}

function log(msg) {
  const t = new Date().toLocaleTimeString();
  console.log(`[${t}] ${msg}`);
}

const server = http.createServer((req, res) => {
  // CORS for potential future web dashboard (localhost only anyway)
  res.setHeader('Access-Control-Allow-Origin', '*');

  if (req.method === 'POST' && req.url === '/event') {
    let body = '';
    req.on('data', (chunk) => {
      body += chunk;
      if (body.length > 256 * 1024) req.destroy(); // sanity cap
    });
    req.on('end', () => {
      try {
        const ev = JSON.parse(body || '{}');
        ingest(store, ev);
        log(`event: ${ev.type}${ev.toolName ? ' · ' + ev.toolName : ''} [${ev.cwd || 'unknown'}]`);
        broadcast();
        res.writeHead(204).end();
      } catch (err) {
        res.writeHead(400, { 'Content-Type': 'application/json' });
        res.end(JSON.stringify({ error: 'invalid JSON' }));
      }
    });
    return;
  }

  if (req.method === 'GET' && req.url === '/state') {
    res.writeHead(200, { 'Content-Type': 'application/json' });
    res.end(JSON.stringify(publicSnapshot(store)));
    return;
  }

  if (req.method === 'GET' && req.url === '/') {
    res.writeHead(200, { 'Content-Type': 'text/plain' });
    res.end('Agent Live Widget server is running.\n');
    return;
  }

  res.writeHead(404).end();
});

server.on('upgrade', (req, socket, head) => {
  if (req.url === '/live') {
    wss.handleUpgrade(req, socket, head, (ws) => {
      wss.emit('connection', ws, req);
    });
  } else {
    socket.destroy();
  }
});

wss.on('connection', (ws) => {
  log(`widget connected (${wss.clients.size} client${wss.clients.size === 1 ? '' : 's'})`);
  ws.send(JSON.stringify(publicSnapshot(store)));
  ws.on('close', () => log(`widget disconnected (${wss.clients.size} clients)`));
  ws.on('error', () => {});
});

// Watchdog: Claude Code may exit without SessionEnd — fall back to idle,
// per project, independently.
setInterval(() => {
  if (applyWatchdog(store, WATCHDOG_IDLE_MS)) {
    log('watchdog: one or more projects idle after 5 min of silence');
    broadcast();
  }
}, 30 * 1000);

server.on('error', (err) => {
  if (err.code === 'EADDRINUSE') {
    console.log(`Port ${PORT} is already in use — another server instance is probably running.`);
    process.exit(0);
  }
  throw err;
});

server.listen(PORT, '127.0.0.1', () => {
  log(`Agent Live Widget server listening on http://localhost:${PORT}`);
  log('POST /event · GET /state · WS /live');
});
