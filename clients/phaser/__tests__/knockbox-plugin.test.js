import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';

// Coverage for the Phaser networking plugin (knockbox-plugin.js). The plugin needs a host Phaser
// (BasePlugin to subclass + an EventEmitter) and a WebSocket; both are stubbed here so the test runs
// under Node with no browser. Credentials are injected via the plugin's `data`/setLaunchParams path,
// so location/jsdom are never needed. The UMD module reads globalThis.Phaser at import, so Phaser is
// stubbed before each fresh import.

// ── Minimal Phaser host stub ────────────────────────────────────────────────────────────────────
// ES5 function constructors (not classes): the plugin subclasses via `BasePlugin.call(this, ...)`
// and `Object.create(BasePlugin.prototype)`, and a class constructor can't be invoked without `new`.
function FakeEmitter() { this._handlers = {}; this.destroyed = false; }
FakeEmitter.prototype.on = function (event, fn) { (this._handlers[event] = this._handlers[event] || []).push(fn); return this; };
FakeEmitter.prototype.emit = function (event, arg) { (this._handlers[event] || []).forEach((fn) => fn(arg)); return this; };
FakeEmitter.prototype.destroy = function () { this._handlers = {}; this.destroyed = true; };

function FakeBasePlugin(pluginManager) { this.pluginManager = pluginManager; }
FakeBasePlugin.prototype.destroy = function () { this.destroyed = true; };

const PhaserStub = { Plugins: { BasePlugin: FakeBasePlugin }, Events: { EventEmitter: FakeEmitter } };

// ── Fake WebSocket ───────────────────────────────────────────────────────────────────────────────
let sockets;
class FakeWebSocket {
  constructor(url) {
    this.url = url; this.readyState = 0; this.sent = [];
    this.onopen = this.onmessage = this.onerror = this.onclose = null;
    sockets.push(this);
  }
  send(data) { this.sent.push(JSON.parse(data)); }
  close() { this.readyState = 3; }
  _open() { this.readyState = 1; if (this.onopen) this.onopen(); }
  _recv(obj) { if (this.onmessage) this.onmessage({ data: JSON.stringify(obj) }); }
  _close(code = 1006, reason = '') { this.readyState = 3; if (this.onclose) this.onclose({ code, reason }); }
}

const lastSocket = () => sockets[sockets.length - 1];
const gameFrames = (ws) => ws.sent.filter((f) => f.type !== 'Attach');
const record = (plugin, event) => { const out = []; plugin.events.on(event, (a) => out.push(a)); return out; };

// Fresh import with Phaser/WebSocket stubbed; then init + start so the socket is created.
async function makePlugin({ ticket = 'tkt', endpoint = 'ws://srv/ws', skipStart = false } = {}) {
  const mod = await import('../knockbox-plugin.js');
  const Plugin = mod.default;
  const plugin = new Plugin({ /* pluginManager */ });
  plugin.init({ ticket, endpoint });
  if (!skipStart) plugin.start();
  return { plugin, ws: lastSocket() };
}

beforeEach(() => {
  vi.resetModules();
  sockets = [];
  vi.stubGlobal('Phaser', PhaserStub);
  vi.stubGlobal('WebSocket', FakeWebSocket);
});

afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('load guards', () => {
  it('throws if Phaser is not loaded', async () => {
    vi.stubGlobal('Phaser', undefined);
    await expect(import('../knockbox-plugin.js')).rejects.toThrow(/Phaser/);
  });
});

describe('connect & attach', () => {
  it('reads credentials from init data and sends Attach (with proto) on open', async () => {
    const { ws } = await makePlugin({ ticket: 'abc', endpoint: 'ws://host/ws' });
    expect(ws.url).toBe('ws://host/ws');
    ws._open();
    expect(ws.sent[0]).toEqual({ type: 'Attach', ticket: 'abc', proto: 1 });
  });

  it('logs an error and does not connect without a ticket', async () => {
    const err = vi.spyOn(console, 'error').mockImplementation(() => {});
    const mod = await import('../knockbox-plugin.js');
    const plugin = new mod.default({});
    plugin.start(); // no ticket supplied, no location to read from
    expect(sockets).toHaveLength(0);
    expect(err).toHaveBeenCalled();
  });

  it('setLaunchParams supplies credentials before connect', async () => {
    const { plugin } = await makePlugin({ skipStart: true, ticket: undefined });
    plugin.setLaunchParams('late', 'ws://late/ws');
    plugin.start();
    expect(lastSocket().url).toBe('ws://late/ws');
  });
});

describe('Ready & reconnected flag', () => {
  it('sets state and emits ready; flags a reconnect on the second Ready', async () => {
    const { plugin, ws } = await makePlugin();
    const ready = record(plugin, 'ready');
    const resumed = record(plugin, 'resumed');
    ws._open();

    ws._recv({ type: 'Ready', playerId: 'me', players: [{ id: 'me' }], isHost: true });
    expect(plugin.playerId).toBe('me');
    expect(plugin.isHost).toBe(true);
    expect(plugin.reconnected).toBe(false);
    expect(ready).toHaveLength(1);
    expect(resumed).toHaveLength(0);

    ws._recv({ type: 'Ready', playerId: 'me', players: [{ id: 'me' }], isHost: true });
    expect(plugin.reconnected).toBe(true);
    expect(resumed).toHaveLength(1);
  });

  it('routes Game frames and roster events', async () => {
    const { plugin, ws } = await makePlugin();
    const msgs = record(plugin, 'message');
    const joined = record(plugin, 'player-joined');
    const left = record(plugin, 'player-left');
    ws._open();
    ws._recv({ type: 'Ready', playerId: 'me', players: [{ id: 'me' }], isHost: true });

    ws._recv({ type: 'Game', from: 'p2', payload: { k: 1 } });
    ws._recv({ type: 'GamePlayerJoined', player: { id: 'p2' } });
    ws._recv({ type: 'GamePlayerLeft', playerId: 'p2' });

    expect(msgs).toEqual([{ from: 'p2', payload: { k: 1 } }]);
    expect(joined.map((p) => p.id)).toEqual(['p2']);
    expect(left).toEqual(['p2']);
    expect(plugin.players.map((p) => p.id)).toEqual(['me']);
  });

  it('emits disconnect/connect events without mutating the roster', async () => {
    const { plugin, ws } = await makePlugin();
    const disconnected = record(plugin, 'player-disconnected');
    const connected = record(plugin, 'player-connected');
    ws._open();
    ws._recv({ type: 'Ready', playerId: 'me', players: [{ id: 'me' }, { id: 'p2' }], isHost: true });

    ws._recv({ type: 'GamePlayerDisconnected', playerId: 'p2' });
    ws._recv({ type: 'GamePlayerConnected', playerId: 'p2' });

    expect(disconnected).toEqual(['p2']);
    expect(connected).toEqual(['p2']);
    expect(plugin.players.map((p) => p.id)).toEqual(['me', 'p2']); // roster unchanged
  });

  it('ignores malformed JSON and unknown frame types', async () => {
    const { ws } = await makePlugin();
    ws._open();
    expect(() => ws.onmessage({ data: '{not json' })).not.toThrow();
    expect(() => ws._recv({ type: 'SomethingNew' })).not.toThrow();
  });

  it('surfaces server-authority Ready fields on the plugin and in the ready payload', async () => {
    const { plugin, ws } = await makePlugin();
    const ready = record(plugin, 'ready');
    ws._open();
    ws._recv({
      type: 'Ready', playerId: 'me', players: [{ id: 'me' }, { id: 'owner' }],
      isHost: false, authority: 'server', ownerId: 'owner',
    });

    expect(plugin.isHost).toBe(false); // no client is ever host in server mode
    expect(plugin.authority).toBe('server');
    expect(plugin.ownerId).toBe('owner');
    expect(plugin.isOwner).toBe(false);
    expect(ready[0]).toMatchObject({ authority: 'server', ownerId: 'owner', isOwner: false });
  });

  it('falls back to host-mode defaults on an old-server Ready', async () => {
    const { plugin, ws } = await makePlugin();
    ws._open();
    ws._recv({ type: 'Ready', playerId: 'me', players: [{ id: 'me' }], isHost: true }); // no new fields

    expect(plugin.authority).toBe('host');
    expect(plugin.ownerId).toBe('me'); // the host IS the owner on an old server
    expect(plugin.isOwner).toBe(true);
  });

  it('updates ownerId/isOwner and emits owner-changed on GameOwnerChanged', async () => {
    const { plugin, ws } = await makePlugin();
    const seen = [];
    plugin.events.on('owner-changed', (id) => seen.push({ id, isOwner: plugin.isOwner }));
    ws._open();
    ws._recv({
      type: 'Ready', playerId: 'me', players: [{ id: 'me' }, { id: 'other' }],
      isHost: false, authority: 'server', ownerId: 'other',
    });

    ws._recv({ type: 'GameOwnerChanged', ownerId: 'me' });    // promoted
    ws._recv({ type: 'GameOwnerChanged', ownerId: 'other' }); // demoted again

    expect(seen).toEqual([{ id: 'me', isOwner: true }, { id: 'other', isOwner: false }]);
    expect(plugin.ownerId).toBe('other');
    expect(plugin.isOwner).toBe(false);
  });
});

describe('send API & queueing', () => {
  it('queues frames issued before attach and flushes them on open', async () => {
    const { plugin, ws } = await makePlugin();
    plugin.sendToAll({ early: 1 }); // before open — must be queued, not dropped
    expect(gameFrames(ws)).toHaveLength(0);

    ws._open();
    expect(gameFrames(ws)).toEqual([{ type: 'Game', to: 'all', payload: { early: 1 } }]);
  });

  it('emits the right frames for each send helper when open', async () => {
    const { plugin, ws } = await makePlugin();
    ws._open();
    plugin.sendToHost({ a: 1 });
    plugin.sendTo('p3', { c: 3 });
    plugin.setLobbyOpen(true);
    plugin.kickPlayer('p3');

    expect(gameFrames(ws)).toEqual([
      { type: 'Game', to: 'host', payload: { a: 1 } },
      { type: 'Game', to: 'p3', payload: { c: 3 } },
      { type: 'SetLobbyOpen', open: true },
      { type: 'KickPlayer', targetPlayerId: 'p3' },
    ]);
  });

  it('bounds the pending-log queue (drop-oldest at 100) and flushes on open', async () => {
    const { plugin, ws } = await makePlugin();
    for (let i = 0; i < 101; i++) plugin.log.info('m' + i);
    ws._open();
    const logs = ws.sent.filter((f) => f.type === 'Log');
    expect(logs).toHaveLength(100);
    expect(logs[0].message).toBe('m1'); // m0 dropped
  });

  it('logPlay sends a PlayLog frame with stringified metadata (queued before attach)', async () => {
    const { plugin, ws } = await makePlugin();
    plugin.logPlay({ placement: 1, result: 'win' });
    expect(ws.sent.filter((f) => f.type === 'PlayLog')).toHaveLength(0); // queued, not dropped

    ws._open();
    expect(ws.sent.filter((f) => f.type === 'PlayLog')).toEqual([
      { type: 'PlayLog', metadata: { placement: '1', result: 'win' } },
    ]);
  });

  it('logPlay tolerates a missing argument', async () => {
    const { plugin, ws } = await makePlugin();
    ws._open();
    plugin.logPlay();
    expect(ws.sent.filter((f) => f.type === 'PlayLog')).toEqual([{ type: 'PlayLog', metadata: {} }]);
  });

  it('logPlay drops nullish values (no "null"/"undefined") but keeps falsy primitives', async () => {
    const { plugin, ws } = await makePlugin();
    ws._open();
    plugin.logPlay({ a: 1, b: null, c: undefined, d: 0 });
    expect(ws.sent.filter((f) => f.type === 'PlayLog')).toEqual([
      { type: 'PlayLog', metadata: { a: '1', d: '0' } },
    ]);
  });
});

describe('close handling & teardown', () => {
  it('emits closed{terminal:true} and does not reconnect on 1008', async () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => {});
    const { plugin, ws } = await makePlugin();
    const closed = record(plugin, 'closed');
    ws._open();
    vi.useFakeTimers();
    ws._close(1008, 'bad ticket');

    expect(closed).toEqual([{ terminal: true }]);
    vi.advanceTimersByTime(60000);
    expect(sockets).toHaveLength(1); // never reconnected
    expect(warn).toHaveBeenCalled();
  });

  it('emits closed{terminal:false} and reconnects after a transient close', async () => {
    const { plugin, ws } = await makePlugin();
    const closed = record(plugin, 'closed');
    ws._open();
    vi.useFakeTimers();
    ws._close(1006);

    expect(closed).toEqual([{ terminal: false }]);
    vi.advanceTimersByTime(1000); // reconnectDelay(0)
    expect(sockets).toHaveLength(2);
  });

  it('stop() tears down the socket and cancels any pending reconnect', async () => {
    const { plugin, ws } = await makePlugin();
    ws._open();
    vi.useFakeTimers();
    ws._close(1006);      // schedules a reconnect
    plugin.stop();        // ...which stop() must cancel
    vi.advanceTimersByTime(5000);
    expect(sockets).toHaveLength(1); // no reconnect fired
  });

  it('destroy() tears down and destroys the event emitter', async () => {
    const { plugin } = await makePlugin();
    const emitter = plugin.events;
    expect(() => plugin.destroy()).not.toThrow();
    expect(emitter.destroyed).toBe(true);
    expect(plugin.events).toBeNull();
  });
});

describe('blob sharing', () => {
  it('rejects invalid arguments to registerBlob', async () => {
    const { plugin } = await makePlugin();
    await expect(plugin.registerBlob('', new Blob(['test']))).rejects.toThrow(TypeError);
    await expect(plugin.registerBlob('   ', new Blob(['test']))).rejects.toThrow(TypeError);
    await expect(plugin.registerBlob(123, new Blob(['test']))).rejects.toThrow(TypeError);
    await expect(plugin.registerBlob('map', null)).rejects.toThrow(TypeError);
    await expect(plugin.registerBlob('map', 'not a blob')).rejects.toThrow(TypeError);
  });

  it('rejects registerBlob if ticket is missing', async () => {
    vi.spyOn(console, 'error').mockImplementation(() => {});
    const { plugin } = await makePlugin({ skipStart: true, ticket: '' });
    await expect(plugin.registerBlob('map', new Blob(['test']))).rejects.toThrow('missing ticket');
  });

  it('probes HEAD, uploads PUT on 404, registers POST, and tracks blobUrl', async () => {
    const { plugin } = await makePlugin({ ticket: 'secret-ticket', endpoint: 'ws://test-host:8080/ws' });
    const blob = new Blob(['hello-world'], { type: 'text/plain' });

    const requests = [];
    vi.stubGlobal('fetch', vi.fn(async (url, init) => {
      requests.push({ url, method: init.method, headers: init.headers, body: init.body });
      if (init.method === 'HEAD') return { status: 404, ok: false, statusText: 'Not Found' };
      if (init.method === 'PUT') return { status: 200, ok: true, statusText: 'OK' };
      if (init.method === 'POST') return { status: 200, ok: true, json: async () => ({ ok: true, url: '/blob/abc.txt' }) };
      return { status: 500, ok: false };
    }));

    expect(plugin.blobUrl('map')).toBeNull();
    const url = await plugin.registerBlob('map', blob);
    expect(url).toBe('/blob/abc.txt');
    expect(plugin.blobUrl('map')).toBe('/blob/abc.txt');

    expect(requests).toHaveLength(3);
    expect(requests[0].method).toBe('HEAD');
    expect(requests[0].url).toContain('http://test-host:8080/blob/');
    expect(requests[1].method).toBe('PUT');
    expect(requests[1].url).toContain('http://test-host:8080/blob/');
    expect(requests[2].method).toBe('POST');
    expect(requests[2].url).toBe('http://test-host:8080/blob/register');
  });

  it('skips PUT upload when HEAD probe returns 200 OK', async () => {
    const { plugin } = await makePlugin({ ticket: 'secret-ticket', endpoint: 'ws://test-host:8080/ws' });
    const blob = new Blob(['cached-content']);

    const methods = [];
    vi.stubGlobal('fetch', vi.fn(async (url, init) => {
      methods.push(init.method);
      if (init.method === 'HEAD') return { status: 200, ok: true };
      if (init.method === 'POST') return { status: 200, ok: true, json: async () => ({ ok: true, url: '/blob/cached.dat' }) };
      return { status: 500, ok: false };
    }));

    const url = await plugin.registerBlob('map', blob);
    expect(url).toBe('/blob/cached.dat');
    expect(methods).toEqual(['HEAD', 'POST']);
  });

  it('unregisters a blob by logicalId and removes from blobUrl', async () => {
    const { plugin } = await makePlugin({ ticket: 'secret-ticket', endpoint: 'ws://srv/ws' });
    const blob = new Blob(['temp']);

    vi.stubGlobal('fetch', vi.fn(async (url, init) => {
      if (init.method === 'HEAD') return { status: 200, ok: true };
      if (init.method === 'POST') return { status: 200, ok: true, json: async () => ({ ok: true, url: '/blob/1.dat' }) };
      if (init.method === 'DELETE') return { status: 200, ok: true };
      return { status: 500, ok: false };
    }));

    await plugin.registerBlob('temp-map', blob);
    expect(plugin.blobUrl('temp-map')).toBe('/blob/1.dat');

    await plugin.unregisterBlob('temp-map');
    expect(plugin.blobUrl('temp-map')).toBeNull();
  });

  it('throws when probe, upload, or register fails', async () => {
    const { plugin } = await makePlugin({ ticket: 'secret-ticket', endpoint: 'ws://srv/ws' });
    const blob = new Blob(['fail-test']);

    // Probe failure (e.g. 500)
    vi.stubGlobal('fetch', vi.fn(async () => ({ status: 500, ok: false, statusText: 'Server Error' })));
    await expect(plugin.registerBlob('map', blob)).rejects.toThrow('Failed to probe blob: 500');

    // Upload failure (e.g. 413)
    vi.stubGlobal('fetch', vi.fn(async (url, init) => {
      if (init.method === 'HEAD') return { status: 404, ok: false };
      return { status: 413, ok: false, statusText: 'Payload Too Large' };
    }));
    await expect(plugin.registerBlob('map', blob)).rejects.toThrow('Failed to upload blob: 413');

    // Register failure (surfacing server JSON error)
    vi.stubGlobal('fetch', vi.fn(async (url, init) => {
      if (init.method === 'HEAD') return { status: 200, ok: true };
      if (init.method === 'PUT') return { status: 200, ok: true };
      return { status: 507, ok: false, statusText: 'Insufficient Storage', json: async () => ({ ok: false, error: 'LobbyQuotaExceeded' }) };
    }));
    await expect(plugin.registerBlob('map', blob)).rejects.toThrow('Failed to register blob: LobbyQuotaExceeded');
  });

  it('retries upload on 409 Conflict during register and succeeds', async () => {
    const { plugin } = await makePlugin({ ticket: 'secret-ticket', endpoint: 'ws://srv/ws' });
    const blob = new Blob(['evicted-content']);

    const methods = [];
    vi.stubGlobal('fetch', vi.fn(async (url, init) => {
      methods.push(init.method);
      if (init.method === 'HEAD') return { status: 200, ok: true };
      if (init.method === 'PUT') return { status: 200, ok: true };
      if (init.method === 'POST') {
        if (methods.filter(m => m === 'POST').length === 1) {
          return { status: 409, ok: false, statusText: 'Conflict', json: async () => ({ ok: false, error: 'UnknownHash' }) };
        }
        return { status: 200, ok: true, json: async () => ({ ok: true, url: '/blob/recovered.dat' }) };
      }
      return { status: 500, ok: false };
    }));

    const url = await plugin.registerBlob('map', blob);
    expect(url).toBe('/blob/recovered.dat');
    expect(methods).toEqual(['HEAD', 'POST', 'PUT', 'POST']);
  });

  it('throws when unregister fails with surfaced error', async () => {
    const { plugin } = await makePlugin({ ticket: 'secret-ticket', endpoint: 'ws://srv/ws' });
    vi.stubGlobal('fetch', vi.fn(async () => ({
      status: 403,
      ok: false,
      statusText: 'Forbidden',
      json: async () => ({ ok: false, error: 'Forbidden' }),
    })));

    await expect(plugin.unregisterBlob('temp-map')).rejects.toThrow('Failed to unregister blob: Forbidden');
  });

  it('clears registered blob URLs on terminal socket close and destroy', async () => {
    vi.spyOn(console, 'warn').mockImplementation(() => {});
    const { plugin, ws } = await makePlugin();
    const blob = new Blob(['data']);

    vi.stubGlobal('fetch', vi.fn(async (url, init) => {
      if (init.method === 'HEAD') return { status: 200, ok: true };
      return { status: 200, ok: true, json: async () => ({ ok: true, url: '/blob/test.dat' }) };
    }));

    await plugin.registerBlob('item', blob);
    expect(plugin.blobUrl('item')).toBe('/blob/test.dat');

    ws._open();
    ws._close(1008, 'terminal');
    expect(plugin.blobUrl('item')).toBeNull();

    await plugin.registerBlob('item2', blob);
    expect(plugin.blobUrl('item2')).toBe('/blob/test.dat');
    plugin.destroy();
    expect(plugin.blobUrl('item2')).toBeNull();
  });
});
