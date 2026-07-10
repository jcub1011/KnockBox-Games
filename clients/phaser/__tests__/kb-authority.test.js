import { describe, it, expect, beforeEach } from 'vitest';
import KBAuthority from '../kb-authority.js'; // UMD default export (the constructor)
import LocalPkg from '../knockbox-local.js';

const { KnockBoxLocalPeer, _resetLocalHubs } = LocalPkg;

const flush = () => new Promise((resolve) => setTimeout(resolve, 0));

function peer(playerId) {
  return new KnockBoxLocalPeer({ mode: 'process', channel: 'auth', playerId });
}

// A trivial replicated counter: guests send 'point' intents, the host validates and broadcasts a
// delta, everyone converges. Exercises the default (broadcast) authority loop end-to-end.
function counterModel() {
  return {
    state: { score: 0 },
    applyIntent(_fromId, action) {
      if (action && action.kind === 'point') {
        this.state.score += 1;
        return { score: this.state.score }; // patch broadcast to everyone
      }
      return null; // reject / no-op
    },
    applyPatch(patch) { Object.assign(this.state, patch); },
    snapshot() { return { score: this.state.score }; },
    applySnapshot(s) { this.state = { score: s.score }; },
  };
}

describe('KBAuthority — default broadcast mode', () => {
  beforeEach(() => _resetLocalHubs());

  it('applies intents on the host and converges every client via deltas', async () => {
    const hostNet = peer('host');
    const guestNet = peer('guest');
    const hostModel = counterModel();
    const guestModel = counterModel();
    // Authorities must subscribe BEFORE 'ready' fires, so a guest's auto-sync isn't missed.
    new KBAuthority(hostNet, hostModel);
    const guestAuth = new KBAuthority(guestNet, guestModel);

    hostNet.start();
    guestNet.start();
    await flush();

    guestAuth.sendIntent({ kind: 'point' });
    await flush();
    expect(hostModel.state.score).toBe(1);
    expect(guestModel.state.score).toBe(1);
  });

  it('rejected intents (applyIntent → null) broadcast nothing', async () => {
    const hostNet = peer('host');
    const guestNet = peer('guest');
    const hostModel = counterModel();
    const guestModel = counterModel();
    new KBAuthority(hostNet, hostModel);
    const guestAuth = new KBAuthority(guestNet, guestModel);

    hostNet.start();
    guestNet.start();
    await flush();

    guestAuth.sendIntent({ kind: 'noop' }); // not 'point' → rejected
    await flush();
    expect(hostModel.state.score).toBe(0);
    expect(guestModel.state.score).toBe(0);
  });

  it('syncs a late joiner with a full snapshot of the current state', async () => {
    const hostNet = peer('host');
    const hostModel = counterModel();
    const hostAuth = new KBAuthority(hostNet, hostModel);

    hostNet.start();
    await flush();

    hostAuth.sendIntent({ kind: 'point' });
    hostAuth.sendIntent({ kind: 'point' });
    await flush();
    expect(hostModel.state.score).toBe(2);

    // A guest joins after state already advanced — it should catch up to score 2.
    const guestNet = peer('guest');
    const guestModel = counterModel();
    new KBAuthority(guestNet, guestModel);
    guestNet.start();
    await flush();

    expect(guestModel.state.score).toBe(2);
  });
});

describe('KBAuthority — both host and guest can drive intents', () => {
  beforeEach(() => _resetLocalHubs());

  it('counts intents from either side', async () => {
    const hostNet = peer('host');
    const guestNet = peer('guest');
    const hostModel = counterModel();
    const guestModel = counterModel();
    const hostAuth = new KBAuthority(hostNet, hostModel);
    const guestAuth = new KBAuthority(guestNet, guestModel);

    hostNet.start();
    guestNet.start();
    await flush();

    guestAuth.sendIntent({ kind: 'point' });
    hostAuth.sendIntent({ kind: 'point' });
    await flush();

    expect(hostModel.state.score).toBe(2);
    expect(guestModel.state.score).toBe(2);
  });
});

// Hidden-information games: each player sees only their own projection of the truth.
function secretModel(secrets) {
  return {
    revealed: false,
    applyIntent(_fromId, action) {
      if (action && action.kind === 'reveal') {
        this.revealed = true;
        return true; // accept; host re-projects a fresh snapshot to everyone (value ignored)
      }
      return null;
    },
    // Default-deny projection: a player only learns their own secret until the reveal.
    snapshot(forPlayerId) {
      return {
        you: Object.prototype.hasOwnProperty.call(secrets, forPlayerId) ? secrets[forPlayerId] : null,
        revealed: this.revealed,
        all: this.revealed ? secrets : null,
      };
    },
  };
}

describe('KBAuthority — per-recipient (hidden-information) mode', () => {
  beforeEach(() => _resetLocalHubs());

  it('projects a different view to each player and re-projects on intent', async () => {
    const secrets = { host: 10, g1: 20, g2: 30 };
    const hostNet = peer('host');
    const g1Net = peer('g1');
    const g2Net = peer('g2');

    const hostAuth = new KBAuthority(hostNet, secretModel(secrets), { perRecipient: true });
    // Guests need no model in per-recipient mode — they render currentView directly.
    const g1Auth = new KBAuthority(g1Net, {}, { perRecipient: true });
    const g2Auth = new KBAuthority(g2Net, {}, { perRecipient: true });

    hostNet.start();
    g1Net.start();
    g2Net.start();
    await flush();

    expect(hostAuth.currentView).toEqual({ you: 10, revealed: false, all: null });
    expect(g1Auth.currentView).toEqual({ you: 20, revealed: false, all: null });
    expect(g2Auth.currentView).toEqual({ you: 30, revealed: false, all: null });

    g1Auth.sendIntent({ kind: 'reveal' });
    await flush();

    expect(hostAuth.currentView.revealed).toBe(true);
    expect(g1Auth.currentView.revealed).toBe(true);
    expect(g2Auth.currentView.revealed).toBe(true);
    // After the reveal everyone can see all secrets.
    expect(g1Auth.currentView.all).toEqual(secrets);
  });
});

// ── Server-authority mode (design §10) ────────────────────────────────────────────────────────
// When net.authority === 'server' every client is a guest: auto-sync on ready, adopt only frames
// published by the reserved sender id 'server', and never run the host branch. Driven over a
// hand-rolled fake net so each frame's `from` is fully controlled.
describe('KBAuthority — server-authority mode', () => {
  function fakeNet({ playerId = 'me', authority = 'server', isHost = false } = {}) {
    const handlers = {};
    return {
      playerId,
      players: [{ id: playerId, displayName: playerId }],
      isHost,
      authority,
      reconnected: false,
      isLocal: false,
      sent: [],
      events: {
        on(event, fn) { (handlers[event] = handlers[event] || []).push(fn); return this; },
        off(event, fn) {
          handlers[event] = (handlers[event] || []).filter((h) => h !== fn);
          return this;
        },
        emit(event, arg) { (handlers[event] || []).forEach((fn) => fn(arg)); return this; },
      },
      sendToHost(payload) { this.sent.push({ to: 'host', payload }); },
      sendToAll(payload) { this.sent.push({ to: 'all', payload }); },
      sendTo(id, payload) { this.sent.push({ to: id, payload }); },
      setLobbyOpen() {},
    };
  }

  function trackedModel() {
    const model = counterModel();
    model.applied = [];
    const applyPatch = model.applyPatch.bind(model);
    const applySnapshot = model.applySnapshot.bind(model);
    model.applyPatch = (p) => { model.applied.push(['patch', p]); applyPatch(p); };
    model.applySnapshot = (s) => { model.applied.push(['snapshot', s]); applySnapshot(s); };
    return model;
  }

  it('auto-requests a sync on ready (everyone is a guest)', () => {
    const net = fakeNet();
    new KBAuthority(net, counterModel());
    net.events.emit('ready', { playerId: 'me', players: net.players, isHost: false, authority: 'server' });

    expect(net.sent).toEqual([{ to: 'host', payload: { _kb: 'sync' } }]);
  });

  it('adopts deltas and snapshots stamped from "server"', () => {
    const net = fakeNet();
    const model = trackedModel();
    new KBAuthority(net, model);

    net.events.emit('message', { from: 'server', payload: { _kb: 'delta', patch: { score: 3 } } });
    net.events.emit('message', { from: 'server', payload: { _kb: 'state', state: { score: 7 } } });

    expect(model.applied).toEqual([['patch', { score: 3 }], ['snapshot', { score: 7 }]]);
    expect(model.state.score).toBe(7);
  });

  it('ignores forged deltas and snapshots from a player id', () => {
    const net = fakeNet();
    const model = trackedModel();
    const auth = new KBAuthority(net, model);
    const changed = [];
    auth.events.on('state-changed', () => changed.push(1));

    net.events.emit('message', { from: 'cheater', payload: { _kb: 'delta', patch: { score: 999 } } });
    net.events.emit('message', { from: 'cheater', payload: { _kb: 'state', state: { score: 999 } } });

    expect(model.applied).toEqual([]);
    expect(model.state.score).toBe(0);
    expect(changed).toEqual([]);
  });

  it('never runs the host branch (intents are ignored by clients)', () => {
    const net = fakeNet();
    const model = trackedModel();
    let applied = 0;
    const applyIntent = model.applyIntent.bind(model);
    model.applyIntent = (f, a) => { applied++; return applyIntent(f, a); };
    new KBAuthority(net, model);

    net.events.emit('message', { from: 'peer', payload: { _kb: 'intent', action: { kind: 'point' } } });
    net.events.emit('message', { from: 'server', payload: { _kb: 'sync' } });

    expect(applied).toBe(0);
    expect(net.sent).toEqual([]); // no delta broadcast, no snapshot reply
  });

  it('host-mode regression: the hardening is inert when authority is undefined', () => {
    // An old plugin (or host mode) has no `authority` property — a guest must still adopt the
    // HOST PLAYER's deltas, whose `from` is an ordinary player id.
    const net = fakeNet();
    delete net.authority;
    const model = trackedModel();
    new KBAuthority(net, model);

    net.events.emit('message', { from: 'host-player', payload: { _kb: 'delta', patch: { score: 4 } } });

    expect(model.state.score).toBe(4);
  });
});
