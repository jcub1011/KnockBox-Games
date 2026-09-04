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

  it('fires disconnect/connect callbacks without mutating the roster', async () => {
    const { kb, ws } = await importSdk();
    const disconnected = [], connected = [];
    kb.onPlayerDisconnected((id) => disconnected.push(id));
    kb.onPlayerConnected((id) => connected.push(id));
    ws._open();
    ws._recv({ type: 'Ready', playerId: 'me', players: [{ id: 'me' }, { id: 'p2' }], isHost: true });

    // A peer drops then returns within the grace window — they stay a member the whole time.
    ws._recv({ type: 'GamePlayerDisconnected', playerId: 'p2' });
    ws._recv({ type: 'GamePlayerConnected', playerId: 'p2' });

    expect(disconnected).toEqual(['p2']);
    expect(connected).toEqual(['p2']);
    expect(kb.players.map((p) => p.id)).toEqual(['me', 'p2']); // roster unchanged
  });

  it('surfaces server-authority Ready fields on the API and in the onReady payload', async () => {
    const { kb, ws } = await importSdk();
    const seen = [];
    kb.onReady((s) => seen.push(s));
    ws._open();
    ws._recv({
      type: 'Ready', playerId: 'me', players: [{ id: 'me' }, { id: 'owner' }],
      isHost: false, authority: 'server', ownerId: 'owner',
    });

    expect(kb.isHost).toBe(false); // no client is ever host in server mode
    expect(kb.authority).toBe('server');
    expect(kb.ownerId).toBe('owner');
    expect(kb.isOwner).toBe(false);
    expect(seen[0]).toMatchObject({ authority: 'server', ownerId: 'owner', isOwner: false });
  });

  it('falls back to host-mode defaults on an old-server Ready', async () => {
    const { kb, ws } = await importSdk();
    ws._open();
    ws._recv({ type: 'Ready', playerId: 'me', players: [{ id: 'me' }], isHost: true }); // no new fields

    expect(kb.authority).toBe('host');
    expect(kb.ownerId).toBe('me'); // the host IS the owner on an old server
    expect(kb.isOwner).toBe(true);
  });
});

describe('owner changes', () => {
  it('updates ownerId/isOwner and fires onOwnerChanged', async () => {
    const { kb, ws } = await importSdk();
    const seen = [];
    kb.onOwnerChanged((id) => seen.push({ id, isOwner: kb.isOwner }));
    ws._open();
    ws._recv({
      type: 'Ready', playerId: 'me', players: [{ id: 'me' }, { id: 'other' }],
      isHost: false, authority: 'server', ownerId: 'other',
    });
    expect(kb.isOwner).toBe(false);

    ws._recv({ type: 'GameOwnerChanged', ownerId: 'me' });   // promoted
    ws._recv({ type: 'GameOwnerChanged', ownerId: 'other' }); // demoted again

    expect(seen).toEqual([{ id: 'me', isOwner: true }, { id: 'other', isOwner: false }]);
    expect(kb.ownerId).toBe('other');
    expect(kb.isOwner).toBe(false);
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

describe('logPlay', () => {
  it('sends a GameLog frame with stringified metadata once attached', async () => {
    const { kb, ws } = await importSdk();
    ws._open();
    kb.logPlay({ placement: 1, playerCount: 4, result: 'win' });

    const entries = ws.sent.filter((f) => f.type === 'PlayLog');
    expect(entries).toEqual([
      { type: 'PlayLog', metadata: { placement: '1', playerCount: '4', result: 'win' } },
    ]);
  });

  it('queues entries before attach and flushes them on open (same path as logs)', async () => {
    const { kb, ws } = await importSdk();
    kb.logPlay({ a: 1 });
    expect(ws.sent.filter((f) => f.type === 'PlayLog')).toHaveLength(0);
    ws._open();
    expect(ws.sent.filter((f) => f.type === 'PlayLog')).toEqual([
      { type: 'PlayLog', metadata: { a: '1' } },
    ]);
  });

  it('tolerates a missing/non-object argument', async () => {
    const { kb, ws } = await importSdk();
    ws._open();
    kb.logPlay();
    expect(ws.sent.filter((f) => f.type === 'PlayLog')).toEqual([{ type: 'PlayLog', metadata: {} }]);
  });

  it('drops nullish values (no "null"/"undefined") but keeps falsy primitives', async () => {
    const { kb, ws } = await importSdk();
    ws._open();
    kb.logPlay({ a: 1, b: null, c: undefined, d: 0 });
    expect(ws.sent.filter((f) => f.type === 'PlayLog')).toEqual([
      { type: 'PlayLog', metadata: { a: '1', d: '0' } },
    ]);
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

describe('blob sharing', () => {
  it('rejects invalid arguments to registerBlob', async () => {
    const { kb } = await importSdk();
    await expect(kb.registerBlob('', new Blob(['test']))).rejects.toThrow(TypeError);
    await expect(kb.registerBlob('   ', new Blob(['test']))).rejects.toThrow(TypeError);
    await expect(kb.registerBlob(123, new Blob(['test']))).rejects.toThrow(TypeError);
    await expect(kb.registerBlob('map', null)).rejects.toThrow(TypeError);
    await expect(kb.registerBlob('map', 'not a blob')).rejects.toThrow(TypeError);
  });

  it('rejects registerBlob if ticket is missing', async () => {
    vi.spyOn(console, 'error').mockImplementation(() => {});
    const { kb } = await importSdk({ hash: '' });
    await expect(kb.registerBlob('map', new Blob(['test']))).rejects.toThrow('missing ticket');
  });

  it('probes HEAD, uploads PUT on 404, registers POST, and tracks blobUrl', async () => {
    const { kb } = await importSdk({ hash: '#kbTicket=secret-ticket&kbEndpoint=ws://test-host:8080/ws' });
    const blob = new Blob(['hello-world'], { type: 'text/plain' });

    const requests = [];
    vi.stubGlobal('fetch', vi.fn(async (url, init) => {
      requests.push({ url, method: init.method, headers: init.headers, body: init.body });
      if (init.method === 'HEAD') {
        return { status: 404, ok: false, statusText: 'Not Found' };
      }
      if (init.method === 'PUT') {
        return { status: 200, ok: true, statusText: 'OK' };
      }
      if (init.method === 'POST') {
        return {
          status: 200,
          ok: true,
          statusText: 'OK',
          json: async () => ({ ok: true, url: '/blob/abc.txt' }),
        };
      }
      return { status: 500, ok: false };
    }));

    expect(kb.blobUrl('map')).toBeNull();
    const url = await kb.registerBlob('map', blob);
    expect(url).toBe('/blob/abc.txt');
    expect(kb.blobUrl('map')).toBe('/blob/abc.txt');

    expect(requests).toHaveLength(3);
    // 1. HEAD probe
    expect(requests[0].method).toBe('HEAD');
    expect(requests[0].url).toContain('http://test-host:8080/blob/');
    expect(requests[0].headers['X-KnockBox-Ticket']).toBe('secret-ticket');

    // 2. PUT upload
    expect(requests[1].method).toBe('PUT');
    expect(requests[1].url).toContain('http://test-host:8080/blob/');
    expect(requests[1].headers['X-KnockBox-Ticket']).toBe('secret-ticket');
    expect(requests[1].headers['Content-Type']).toBe('text/plain');
    expect(requests[1].body).toBe(blob);

    // 3. POST register
    expect(requests[2].method).toBe('POST');
    expect(requests[2].url).toBe('http://test-host:8080/blob/register');
    expect(requests[2].headers['X-KnockBox-Ticket']).toBe('secret-ticket');
    const regBody = JSON.parse(requests[2].body);
    expect(regBody.logicalId).toBe('map');
    expect(regBody.contentType).toBe('text/plain');
    expect(regBody.sha256).toMatch(/^[0-9a-f]{64}$/);
  });

  it('skips PUT upload when HEAD probe returns 200 OK', async () => {
    const { kb } = await importSdk({ hash: '#kbTicket=secret-ticket&kbEndpoint=ws://test-host:8080/ws' });
    const blob = new Blob(['cached-content']);

    const methods = [];
    vi.stubGlobal('fetch', vi.fn(async (url, init) => {
      methods.push(init.method);
      if (init.method === 'HEAD') {
        return { status: 200, ok: true, statusText: 'OK' };
      }
      if (init.method === 'POST') {
        return {
          status: 200,
          ok: true,
          statusText: 'OK',
          json: async () => ({ ok: true, url: '/blob/cached.dat' }),
        };
      }
      return { status: 500, ok: false };
    }));

    const url = await kb.registerBlob('map', blob);
    expect(url).toBe('/blob/cached.dat');
    expect(methods).toEqual(['HEAD', 'POST']);
  });

  it('throws when probe, upload, or register fails', async () => {
    const { kb } = await importSdk();
    const blob = new Blob(['fail-test']);

    // Probe failure (e.g. 500)
    vi.stubGlobal('fetch', vi.fn(async () => ({ status: 500, ok: false, statusText: 'Server Error' })));
    await expect(kb.registerBlob('map', blob)).rejects.toThrow('Failed to probe blob: 500');

    // Upload failure (e.g. 413)
    vi.stubGlobal('fetch', vi.fn(async (url, init) => {
      if (init.method === 'HEAD') return { status: 404, ok: false };
      return { status: 413, ok: false, statusText: 'Payload Too Large' };
    }));
    await expect(kb.registerBlob('map', blob)).rejects.toThrow('Failed to upload blob: 413');

    // Register failure (e.g. 409)
    vi.stubGlobal('fetch', vi.fn(async (url, init) => {
      if (init.method === 'HEAD') return { status: 200, ok: true };
      return { status: 409, ok: false, statusText: 'Conflict' };
    }));
    await expect(kb.registerBlob('map', blob)).rejects.toThrow('Failed to register blob: 409');
  });

  it('unregisters a blob by logicalId and removes from blobUrl', async () => {
    const { kb } = await importSdk({ hash: '#kbTicket=secret-ticket&kbEndpoint=ws://srv/ws' });
    const blob = new Blob(['temp']);

    vi.stubGlobal('fetch', vi.fn(async (url, init) => {
      if (init.method === 'HEAD') return { status: 200, ok: true };
      if (init.method === 'POST') return { status: 200, ok: true, json: async () => ({ ok: true, url: '/blob/1.dat' }) };
      if (init.method === 'DELETE') return { status: 200, ok: true };
      return { status: 500, ok: false };
    }));

    await kb.registerBlob('temp-map', blob);
    expect(kb.blobUrl('temp-map')).toBe('/blob/1.dat');

    await kb.unregisterBlob('temp-map');
    expect(kb.blobUrl('temp-map')).toBeNull();
    expect(fetch).toHaveBeenCalledWith(
      'http://srv/blob/register/temp-map',
      expect.objectContaining({ method: 'DELETE', headers: { 'X-KnockBox-Ticket': 'secret-ticket' } })
    );
  });

  it('clears registered blob URLs on terminal socket close', async () => {
    vi.spyOn(console, 'warn').mockImplementation(() => {});
    const { kb, ws } = await importSdk();
    const blob = new Blob(['data']);

    vi.stubGlobal('fetch', vi.fn(async (url, init) => {
      if (init.method === 'HEAD') return { status: 200, ok: true };
      return { status: 200, ok: true, json: async () => ({ ok: true, url: '/blob/test.dat' }) };
    }));

    await kb.registerBlob('item', blob);
    expect(kb.blobUrl('item')).toBe('/blob/test.dat');

    ws._open();
    ws._close(1008, 'terminal');

    expect(kb.blobUrl('item')).toBeNull();
  });
});
