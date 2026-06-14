import { describe, it, expect, beforeEach } from 'vitest';
import LocalPkg from '../knockbox-local.js'; // UMD default export ({ KnockBoxLocalPeer, ... })

const { KnockBoxLocalPeer, _resetLocalHubs } = LocalPkg;

// The 'process' transport routes synchronously within one JS realm, but start() and self-echo are
// deferred (queueMicrotask). A macrotask tick drains everything so assertions are deterministic.
const flush = () => new Promise((resolve) => setTimeout(resolve, 0));

function peer(opts) {
  return new KnockBoxLocalPeer({ mode: 'process', channel: 't', ...opts });
}

// Collects each emitted event into an array for later assertions.
function record(emitter, event) {
  const out = [];
  emitter.events.on(event, (arg) => out.push(arg));
  return out;
}

describe('process transport — roster & host election', () => {
  beforeEach(() => _resetLocalHubs());

  it('makes the first peer the host and gives later joiners the full roster', async () => {
    const host = peer({ playerId: 'host', displayName: 'Host' });
    const guest = peer({ playerId: 'guest', displayName: 'Guest' });
    const hostReady = record(host, 'ready');
    const guestReady = record(guest, 'ready');
    const hostJoins = record(host, 'player-joined');

    host.start();
    guest.start();
    await flush();

    expect(hostReady).toHaveLength(1);
    expect(hostReady[0].isHost).toBe(true);
    expect(guestReady[0].isHost).toBe(false);
    // players[0] is always the host, on every client.
    expect(guestReady[0].players.map((p) => p.id)).toEqual(['host', 'guest']);
    expect(hostJoins.map((p) => p.id)).toEqual(['guest']);
    expect(host.players.map((p) => p.id)).toEqual(['host', 'guest']);
  });
});

describe('process transport — message routing', () => {
  beforeEach(() => _resetLocalHubs());

  it('sendToHost reaches only the host; sendToAll reaches everyone including self', async () => {
    const host = peer({ playerId: 'host' });
    const guest = peer({ playerId: 'guest' });
    const hostMsgs = record(host, 'message');
    const guestMsgs = record(guest, 'message');

    host.start();
    guest.start();
    await flush();

    guest.sendToHost({ kind: 'tap' });
    host.sendToAll({ kind: 'state' });
    await flush();

    expect(hostMsgs).toEqual([
      { from: 'guest', payload: { kind: 'tap' } },
      { from: 'host', payload: { kind: 'state' } },
    ]);
    expect(guestMsgs).toEqual([{ from: 'host', payload: { kind: 'state' } }]);
  });

  it('sendTo delivers to exactly one named player', async () => {
    const host = peer({ playerId: 'host' });
    const a = peer({ playerId: 'a' });
    const b = peer({ playerId: 'b' });
    const aMsgs = record(a, 'message');
    const bMsgs = record(b, 'message');

    host.start();
    a.start();
    b.start();
    await flush();

    a.sendTo('b', { secret: 1 });
    await flush();

    expect(aMsgs).toEqual([]); // not the recipient
    expect(bMsgs).toEqual([{ from: 'a', payload: { secret: 1 } }]);
  });

  it('queues sends issued before ready and flushes them on ready', async () => {
    const host = peer({ playerId: 'host' });
    const guest = peer({ playerId: 'guest' });
    const hostMsgs = record(host, 'message');

    guest.sendToHost({ early: 1 }); // before start/ready — must be queued, not dropped
    host.start();
    guest.start();
    await flush();

    expect(hostMsgs).toEqual([{ from: 'guest', payload: { early: 1 } }]);
  });
});

describe('process transport — leaving & kicking', () => {
  beforeEach(() => _resetLocalHubs());

  it('ends the lobby for everyone when the host leaves (no migration)', async () => {
    const host = peer({ playerId: 'host' });
    const guest = peer({ playerId: 'guest' });
    const guestLeft = record(guest, 'player-left');
    const guestClosed = record(guest, 'closed');

    host.start();
    guest.start();
    await flush();

    host.destroy(); // host leaves
    await flush();

    expect(guestLeft).toEqual(['host']);
    expect(guestClosed).toEqual([{ terminal: true }]);
  });

  it('lets the host kick a player, closing them and dropping them from the roster', async () => {
    const host = peer({ playerId: 'host' });
    const guest = peer({ playerId: 'guest' });
    const hostLeft = record(host, 'player-left');
    const guestClosed = record(guest, 'closed');

    host.start();
    guest.start();
    await flush();

    host.kickPlayer('guest');
    await flush();

    expect(hostLeft).toEqual(['guest']);
    expect(guestClosed).toEqual([{ terminal: true }]);
    expect(host.players.map((p) => p.id)).toEqual(['host']);
  });

  it('ignores a non-host attempt to kick', async () => {
    const host = peer({ playerId: 'host' });
    const guest = peer({ playerId: 'guest' });
    const hostClosed = record(host, 'closed');

    host.start();
    guest.start();
    await flush();

    guest.kickPlayer('host'); // guests can't kick
    await flush();

    expect(hostClosed).toEqual([]);
    expect(host.players.map((p) => p.id)).toEqual(['host', 'guest']);
  });
});

describe('solo transport', () => {
  it('is a single host that echoes its own sends', async () => {
    const solo = new KnockBoxLocalPeer({ mode: 'solo', playerId: 'solo' });
    const ready = record(solo, 'ready');
    const msgs = record(solo, 'message');

    solo.start();
    await flush();

    expect(ready[0]).toMatchObject({ playerId: 'solo', isHost: true });
    expect(ready[0].players.map((p) => p.id)).toEqual(['solo']);

    solo.sendToAll({ tick: 1 });
    await flush();
    expect(msgs).toEqual([{ from: 'solo', payload: { tick: 1 } }]);
  });
});
