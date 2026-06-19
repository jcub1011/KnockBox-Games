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

  it('logPlay sends a GameLog frame with stringified metadata (queued before attach)', async () => {
    const { plugin, ws } = await makePlugin();
    plugin.logPlay({ placement: 1, result: 'win' });
    expect(ws.sent.filter((f) => f.type === 'GameLog')).toHaveLength(0); // queued, not dropped

    ws._open();
    expect(ws.sent.filter((f) => f.type === 'GameLog')).toEqual([
      { type: 'GameLog', metadata: { placement: '1', result: 'win' } },
    ]);
  });

  it('logPlay tolerates a missing argument', async () => {
    const { plugin, ws } = await makePlugin();
    ws._open();
    plugin.logPlay();
    expect(ws.sent.filter((f) => f.type === 'GameLog')).toEqual([{ type: 'GameLog', metadata: {} }]);
  });

  it('logPlay drops nullish values (no "null"/"undefined") but keeps falsy primitives', async () => {
    const { plugin, ws } = await makePlugin();
    ws._open();
    plugin.logPlay({ a: 1, b: null, c: undefined, d: 0 });
    expect(ws.sent.filter((f) => f.type === 'GameLog')).toEqual([
      { type: 'GameLog', metadata: { a: '1', d: '0' } },
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
