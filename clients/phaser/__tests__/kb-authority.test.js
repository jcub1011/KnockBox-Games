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
