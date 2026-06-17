// @vitest-environment jsdom
//
// Coverage for the game-facing SDK web/knockbox.js. It is an IIFE that reads its ticket/endpoint from
// location.hash and opens its own data socket at import. Each test stubs location, installs a
// FakeWebSocket, imports a fresh copy, and drives the socket lifecycle. The public surface is read off
// window.KnockBox.
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { FakeWebSocket, installFakeWebSocket } from './helpers.js';

let getWs;

// Stub location (so the IIFE reads our ticket) and import a fresh SDK. Returns the live FakeWebSocket
// the SDK created (null when it declined to connect) plus the window.KnockBox API.
async function importSdk({ hash = '#kbTicket=abc&kbEndpoint=ws://srv/ws', pathname = '/g/', search = '' } = {}) {
  vi.stubGlobal('location', { hash, protocol: 'http:', host: 'localhost', pathname, search });
  await import('../knockbox.js');
  return { kb: window.KnockBox, ws: getWs() };
}

beforeEach(() => {
  vi.resetModules();
  getWs = installFakeWebSocket();
});

afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('attach handshake', () => {
  it('does not connect and logs an error when the ticket is missing', async () => {
    const err = vi.spyOn(console, 'error').mockImplementation(() => {});
    await importSdk({ hash: '' });
    expect(FakeWebSocket.instances).toHaveLength(0);
    expect(err).toHaveBeenCalled();
  });

  it('sends Attach (with proto) on open and scrubs the fragment', async () => {
    const replace = vi.spyOn(window.history, 'replaceState');
    const { ws } = await importSdk();
    expect(ws.url).toBe('ws://srv/ws');
    ws._open();
    expect(ws.sent[0]).toEqual({ type: 'Attach', ticket: 'abc', proto: 1 });
    expect(replace).toHaveBeenCalled(); // credentials wiped from the address bar
  });

  it('falls back to the page-derived endpoint when the fragment omits one', async () => {
    const { ws } = await importSdk({ hash: '#kbTicket=abc' });
    expect(ws.url).toBe('ws://localhost/ws');
  });
});

describe('Ready & roster', () => {
  it('populates state and fires onReady', async () => {
    const { kb, ws } = await importSdk();
    const seen = [];
    kb.onReady((s) => seen.push(s));
    ws._open();
    ws._recv({ type: 'Ready', playerId: 'me', players: [{ id: 'me' }], isHost: true });

    expect(kb.playerId).toBe('me');
    expect(kb.isHost).toBe(true);
    expect(kb.players.map((p) => p.id)).toEqual(['me']);
    expect(seen).toHaveLength(1);
    expect(seen[0]).toMatchObject({ playerId: 'me', isHost: true });
  });

  it('gives a late onReady subscriber an immediate snapshot', async () => {
    const { kb, ws } = await importSdk();
    ws._open();
    ws._recv({ type: 'Ready', playerId: 'me', players: [], isHost: false });
    const seen = [];
    kb.onReady((s) => seen.push(s)); // subscribed AFTER ready
    expect(seen).toHaveLength(1);
    expect(seen[0].playerId).toBe('me');
  });

  it('routes Game frames to onMessage and updates the roster on join/leave', async () => {
    const { kb, ws } = await importSdk();
    const msgs = [], joined = [], left = [];
    kb.onMessage((m) => msgs.push(m));
    kb.onPlayerJoined((p) => joined.push(p));
    kb.onPlayerLeft((id) => left.push(id));
    ws._open();
    ws._recv({ type: 'Ready', playerId: 'me', players: [{ id: 'me' }], isHost: true });

    ws._recv({ type: 'Game', from: 'p2', payload: { kind: 'move' } });
    ws._recv({ type: 'GamePlayerJoined', player: { id: 'p2' } });
    ws._recv({ type: 'GamePlayerLeft', playerId: 'p2' });

    expect(msgs).toEqual([{ from: 'p2', payload: { kind: 'move' } }]);
    expect(joined.map((p) => p.id)).toEqual(['p2']);
    expect(left).toEqual(['p2']);
    expect(kb.players.map((p) => p.id)).toEqual(['me']); // joined then left
  });
});

describe('send API', () => {
  it('emits the right Game/SetLobbyOpen frames when open', async () => {
    const { kb, ws } = await importSdk();
    ws._open();
    kb.sendToHost({ a: 1 });
    kb.sendToAll({ b: 2 });
    kb.sendTo('p3', { c: 3 });
    kb.setLobbyOpen(true);

    const after = ws.sent.filter((f) => f.type !== 'Attach');
    expect(after).toEqual([
      { type: 'Game', to: 'host', payload: { a: 1 } },
      { type: 'Game', to: 'all', payload: { b: 2 } },
      { type: 'Game', to: 'p3', payload: { c: 3 } },
      { type: 'SetLobbyOpen', open: true },
    ]);
  });

  it('drops sends issued while the socket is not open', async () => {
    const { kb, ws } = await importSdk();
    kb.sendToAll({ early: 1 }); // before open — no socket buffer for game frames
    expect(ws.sent).toHaveLength(0);
  });
});

describe('server logging', () => {
  it('queues logs before attach and flushes them in order on open', async () => {
    const { kb, ws } = await importSdk();
    kb.log.info('first');
    kb.log.warn('second');
    ws._open();

    const logs = ws.sent.filter((f) => f.type === 'Log');
    expect(logs).toEqual([
      { type: 'Log', level: 'Information', message: 'first' },
      { type: 'Log', level: 'Warning', message: 'second' },
    ]);
  });

  it('bounds the pending-log queue (drop-oldest at 100)', async () => {
    const { kb, ws } = await importSdk();
    for (let i = 0; i < 101; i++) kb.log.info('m' + i); // 101 logs while not attached
    ws._open();

    const logs = ws.sent.filter((f) => f.type === 'Log');
    expect(logs).toHaveLength(100);
    expect(logs[0].message).toBe('m1'); // m0 dropped (oldest)
  });

  it('sends logs immediately once attached', async () => {
    const { kb, ws } = await importSdk();
    ws._open();
    kb.log.error('live');
    const logs = ws.sent.filter((f) => f.type === 'Log');
    expect(logs).toEqual([{ type: 'Log', level: 'Error', message: 'live' }]);
  });
});

describe('reconnection', () => {
  it('stops permanently on a terminal close (1008)', async () => {
    vi.useFakeTimers();
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => {});
    const { ws } = await importSdk();
    ws._open();
    ws._close(1008, 'invalid ticket');

    vi.advanceTimersByTime(60000);
    expect(FakeWebSocket.instances).toHaveLength(1); // never reconnected
    expect(warn).toHaveBeenCalled();
  });

  it('reconnects after a transient close and resets backoff on Ready', async () => {
    vi.useFakeTimers();
    const { ws } = await importSdk();
    ws._open();
    ws._close(1006); // abnormal → schedule reconnect at reconnectDelay(0)=1000ms
    vi.advanceTimersByTime(1000);
    expect(FakeWebSocket.instances).toHaveLength(2);

    const ws2 = getWs();
    ws2._open();
    ws2._recv({ type: 'Ready', playerId: 'me', players: [], isHost: true }); // resets attempt
    ws2._close(1006);
    vi.advanceTimersByTime(1000); // a non-reset backoff would be 2000ms and not fire yet
    expect(FakeWebSocket.instances).toHaveLength(3);
  });
});
