// Shared scaffolding for the jsdom-environment suites (shell.test.js, knockbox.test.js).
//
// shell.js and knockbox.js are side-effecting modules: they read the DOM / location and wire up a
// WebSocket at import time. So a test must (1) put the real markup in place, (2) install a fake
// WebSocket, and (3) import the module fresh (vi.resetModules) so module-level state is clean. These
// helpers package those steps.
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { vi } from 'vitest';

// Load the ACTUAL web/index.html into the jsdom document so the element IDs shell.js queries stay in
// sync with production markup (a hand-maintained fixture would silently drift). We replace the whole
// document element so <body>'s ids resolve; the <script type=module> tag is inert under jsdom (it
// won't fetch/run), which is what we want — the test imports shell.js itself.
// (Resolve from the package cwd — vitest runs from web/ — because under the jsdom environment
// `import.meta.url` is an http URL, which readFileSync can't consume.)
const INDEX_HTML = readFileSync(resolve(process.cwd(), 'index.html'), 'utf8');

export function loadShellDom() {
  document.documentElement.innerHTML = INDEX_HTML
    .replace(/^[\s\S]*?<html[^>]*>/i, '')
    .replace(/<\/html>\s*$/i, '');
}

// A drop-in WebSocket stand-in. It never opens a real connection; the test drives the lifecycle via
// the _open/_recv/_close helpers and inspects `sent` (parsed JSON frames). Construction pushes onto
// `instances` so a test can grab the socket the module just created.
export class FakeWebSocket {
  static instances = [];
  static CONNECTING = 0;
  static OPEN = 1;
  static CLOSING = 2;
  static CLOSED = 3;

  constructor(url) {
    this.url = url;
    this.readyState = FakeWebSocket.CONNECTING;
    this.sent = [];
    this.onopen = null;
    this.onclose = null;
    this.onmessage = null;
    this.onerror = null;
    FakeWebSocket.instances.push(this);
  }

  send(data) { this.sent.push(JSON.parse(data)); }

  close(code = 1000, reason = '') {
    this.readyState = FakeWebSocket.CLOSED;
    if (this.onclose) this.onclose({ code, reason });
  }

  // ── test drivers ──
  _open() {
    this.readyState = FakeWebSocket.OPEN;
    if (this.onopen) this.onopen();
  }

  _recv(obj) {
    if (this.onmessage) this.onmessage({ data: JSON.stringify(obj) });
  }

  _close(code = 1006, reason = '') {
    this.readyState = FakeWebSocket.CLOSED;
    if (this.onclose) this.onclose({ code, reason });
  }

  // The single most-recently-sent frame — handy for reading the cid of a request to reply to.
  get lastSent() { return this.sent[this.sent.length - 1]; }
}

// Install the fake as the global WebSocket and reset its instance registry. Returns a getter for the
// latest-created socket so a test reads `ws()` after triggering a connect.
export function installFakeWebSocket() {
  FakeWebSocket.instances = [];
  vi.stubGlobal('WebSocket', FakeWebSocket);
  return () => FakeWebSocket.instances[FakeWebSocket.instances.length - 1];
}

// Same trick for the admin portal's markup. It is a separate page served at the ADMIN origin's root, so
// it has its own file and its own element ids — but the reason for loading the real thing is identical: a
// hand-maintained fixture drifts, and admin.js is one long list of getElementById calls.
const ADMIN_HTML = readFileSync(resolve(process.cwd(), 'admin/index.html'), 'utf8');

export function loadAdminDom() {
  document.documentElement.innerHTML = ADMIN_HTML
    .replace(/^[\s\S]*?<html[^>]*>/i, '')
    .replace(/<\/html>\s*$/i, '');
}

/**
 * Installs a fake `fetch` that answers from a route table, and records what was asked for.
 *
 * The admin portal is the first part of this codebase to talk HTTP rather than WebSocket, so this is the
 * suite's first fetch stub. Routes are matched by "METHOD /path" (query string ignored) with a `*` method
 * wildcard; anything unrouted answers 404 rather than hanging, so a missing route fails a test loudly
 * instead of timing out.
 */
export function installFakeFetch(routes = {}) {
  const calls = [];
  const fetchMock = vi.fn(async (url, init = {}) => {
    const method = (init.method || 'GET').toUpperCase();
    const path = String(url).split('?')[0];
    calls.push({ method, url: String(url), path, body: init.body ? JSON.parse(init.body) : null, init });

    const route = routes[`${method} ${path}`] ?? routes[`* ${path}`];
    const resolved = typeof route === 'function' ? await route(calls[calls.length - 1]) : route;
    const { status = 200, body = {} } = resolved ?? { status: 404, body: { success: false, error: 'no route' } };
    return {
      ok: status >= 200 && status < 300,
      status,
      json: async () => body,
    };
  });
  vi.stubGlobal('fetch', fetchMock);
  return { calls, fetchMock, routes };
}

// navigator.clipboard.writeText spy that resolves (success) or rejects (failure) on demand.
export function stubClipboard({ fail = false } = {}) {
  const writeText = fail
    ? vi.fn(() => Promise.reject(new Error('denied')))
    : vi.fn(() => Promise.resolve());
  vi.stubGlobal('navigator', { ...globalThis.navigator, clipboard: { writeText } });
  return writeText;
}

// Lets a microtask/await chain settle (request→reply resolution, themeHeader's awaited sampling).
export const tick = () => new Promise((resolve) => setTimeout(resolve, 0));
