import { describe, it, expect, beforeEach } from 'vitest';
import LocalPkg from '../knockbox-local.js';
import KBAuthority from '../kb-authority.js';

// Server-authority emulation (design §12a): the `authority:` option runs the developer's REAL
// authority module as a virtual server actor on the elected peer, over the same transports — so
// client code exercises the byte-identical server-mode path with no server. Driven over the
// 'process' transport (deterministic, synchronous once started).
const { KnockBoxLocalPeer, scanAuthorityImports, _resetLocalHubs } = LocalPkg;

const flush = () => new Promise((resolve) => setTimeout(resolve, 0));

function peer(opts) {
  return new KnockBoxLocalPeer({ mode: 'process', channel: 'auth-emu', ...opts });
}

function record(emitter, event) {
  const out = [];
  emitter.events.on(event, (arg) => out.push(arg));
  return out;
}

// A counter authority in the server module shape (createAuthority + init/applyIntent/snapshot).
function counterAuthority(kb) {
  let state = null;
  return {
    init(players) { state = { count: 0, ids: players.map((p) => p.id) }; },
    applyIntent(fromId, action) {
      if (!action || action.kind !== 'inc') return null;
      state.count += 1;
      return { count: state.count, by: fromId };
    },
    snapshot() { return state; },
    onPlayerJoined(p) { state.ids.push(p.id); return null; },
    onPlayerLeft(id) { state.ids = state.ids.filter((x) => x !== id); return null; },
  };
}

const authorityPeer = (opts) => peer({ authority: counterAuthority, ...opts });
const serverFrames = (msgs) => msgs.filter((m) => m.from === 'server');

beforeEach(() => _resetLocalHubs());

describe('authority mode — ready synthesis', () => {
  it('tells EVERY peer it is a guest, with authority server and the elected owner', async () => {
    const a = authorityPeer({ playerId: 'a' });
    const b = authorityPeer({ playerId: 'b' });
    const aReady = record(a, 'ready');
    const bReady = record(b, 'ready');

    a.start();
    b.start();
    await flush();

    for (const ready of [aReady[0], bReady[0]]) {
      expect(ready.isHost).toBe(false); // even the actor-hosting peer's own game is a guest
      expect(ready.authority).toBe('server');
      expect(ready.ownerId).toBe('a');
    }
    expect(aReady[0].isOwner).toBe(true);
    expect(bReady[0].isOwner).toBe(false);
    expect(a.isHost).toBe(false);
    expect(a.authority).toBe('server');
  });

  it('host mode still reports parity fields (authority host, elected owner)', async () => {
    const host = peer({ playerId: 'h' });
    const ready = record(host, 'ready');
    host.start();
    await flush();

    expect(ready[0]).toMatchObject({ isHost: true, authority: 'host', ownerId: 'h', isOwner: true });
  });

  it('a module that fails to start closes the session loudly', async () => {
    const errors = [];
    const origError = console.error;
    console.error = (...args) => errors.push(args.join(' '));
    try {
      const broken = peer({ authority: () => ({ /* missing init/applyIntent/snapshot */ }), playerId: 'a' });
      const closed = record(broken, 'closed');
      const ready = record(broken, 'ready');
      broken.start();
      await flush();

      expect(closed).toEqual([{ terminal: true }]);
      expect(ready).toHaveLength(0); // never a half-alive session
      expect(errors.some((e) => e.includes('failed to start'))).toBe(true);
    } finally {
      console.error = origError;
    }
  });
});

describe('authority mode — the actor loop', () => {
  async function pair() {
    const a = authorityPeer({ playerId: 'a' });
    const b = authorityPeer({ playerId: 'b' });
    const aMsgs = record(a, 'message');
    const bMsgs = record(b, 'message');
    a.start();
    b.start();
    await flush();
    // b's join already triggered the actor's roster re-broadcast (the server's rule) — baseline
    // both captures so each test asserts only its own traffic.
    aMsgs.length = 0;
    bMsgs.length = 0;
    return { a, b, aMsgs, bMsgs };
  }

  it('routes intents through the module and broadcasts the delta from "server" to all peers', async () => {
    const { aMsgs, bMsgs, b } = await pair();

    b.sendToHost({ _kb: 'intent', action: { kind: 'inc' } });
    await flush();

    for (const msgs of [aMsgs, bMsgs]) {
      const deltas = serverFrames(msgs).filter((m) => m.payload._kb === 'delta');
      expect(deltas).toHaveLength(1);
      expect(deltas[0].payload.patch).toEqual({ count: 1, by: 'b' });
    }
  });

  it('sends nothing for a rejected intent', async () => {
    const { aMsgs, bMsgs, b } = await pair();

    b.sendToHost({ _kb: 'intent', action: { kind: 'bogus' } });
    await flush();

    expect(serverFrames(aMsgs)).toHaveLength(0);
    expect(serverFrames(bMsgs)).toHaveLength(0);
  });

  it('answers sync with a state snapshot to the requester only', async () => {
    const { aMsgs, bMsgs, b } = await pair();

    b.sendToHost({ _kb: 'sync' });
    await flush();

    const states = serverFrames(bMsgs).filter((m) => m.payload._kb === 'state');
    expect(states).toHaveLength(1);
    expect(states[0].payload.state).toEqual({ count: 0, ids: ['a', 'b'] });
    expect(serverFrames(aMsgs)).toHaveLength(0);
  });

  it('runs roster hooks and re-broadcasts state when a peer joins', async () => {
    const { aMsgs, bMsgs } = await pair();

    const c = authorityPeer({ playerId: 'c' });
    const cMsgs = record(c, 'message');
    c.start();
    await flush();

    for (const msgs of [aMsgs, bMsgs, cMsgs]) {
      const states = serverFrames(msgs).filter((m) => m.payload._kb === 'state');
      expect(states.length).toBeGreaterThanOrEqual(1);
      expect(states[states.length - 1].payload.state.ids).toEqual(['a', 'b', 'c']);
    }
  });

  it('projects per-recipient snapshots in perRecipient mode', async () => {
    function secretAuthority(kb) {
      // Like the server, init sees only the creator; later members arrive via onPlayerJoined.
      let secrets = null;
      return {
        init(players) { secrets = Object.fromEntries(players.map((p, i) => [p.id, 'secret-' + i])); },
        applyIntent() { return true; }, // truthy = accepted; re-projection follows
        snapshot(forPlayerId) { return { yours: secrets[forPlayerId] ?? null }; },
        onPlayerJoined(p) { secrets[p.id] = 'secret-' + Object.keys(secrets).length; return null; },
      };
    }
    const a = peer({ playerId: 'a', authority: secretAuthority, authorityConfig: { perRecipient: true } });
    const b = peer({ playerId: 'b', authority: secretAuthority, authorityConfig: { perRecipient: true } });
    const aMsgs = record(a, 'message');
    const bMsgs = record(b, 'message');
    a.start();
    b.start();
    await flush();

    b.sendToHost({ _kb: 'intent', action: {} });
    await flush();

    const last = (msgs) => serverFrames(msgs).filter((m) => m.payload._kb === 'state').pop();
    expect(last(aMsgs).payload.state).toEqual({ yours: 'secret-0' });
    expect(last(bMsgs).payload.state).toEqual({ yours: 'secret-1' });
  });

  it('module-namespace form: config.tickHz drives periodic deltas', async () => {
    const module = {
      createAuthority() {
        let t = 0;
        return {
          init() {},
          applyIntent() { return null; },
          snapshot() { return { t }; },
          tick(dtMs) { t += dtMs; return { t }; },
        };
      },
      config: { tickHz: 50 },
    };
    const a = peer({ playerId: 'a', authority: module });
    const aMsgs = record(a, 'message');
    a.start();
    await flush();

    await new Promise((r) => setTimeout(r, 90)); // a few 20 ms tick periods
    a.destroy();

    const deltas = serverFrames(aMsgs).filter((m) => m.payload._kb === 'delta');
    expect(deltas.length).toBeGreaterThanOrEqual(1);
    expect(deltas[0].payload.patch.t).toBeGreaterThan(0);
  });
});

describe('authority mode — relay-rule mirroring', () => {
  it('drops client-sent _kb delta/state with a warning and never delivers them', async () => {
    const warnings = [];
    const origWarn = console.warn;
    console.warn = (...args) => warnings.push(args.join(' '));
    try {
      const a = authorityPeer({ playerId: 'a' });
      const b = authorityPeer({ playerId: 'b' });
      const aMsgs = record(a, 'message');
      a.start();
      b.start();
      await flush();

      b.sendToAll({ _kb: 'state', state: { count: 999 } });
      b.sendToAll({ _kb: 'delta', patch: { count: 999 } });
      b.sendTo('a', { _kb: 'state', state: { count: 999 } });
      b.sendToAll({ emote: 'wave' }); // ordinary chatter still flows
      await flush();

      expect(aMsgs.filter((m) => m.from === 'b')).toEqual([{ from: 'b', payload: { emote: 'wave' } }]);
      expect(warnings.filter((w) => w.includes('only the authority publishes state'))).toHaveLength(3);
    } finally {
      console.warn = origWarn;
    }
  });

  it('drops a non-_kb payload addressed to host with a warning', async () => {
    const warnings = [];
    const origWarn = console.warn;
    console.warn = (...args) => warnings.push(args.join(' '));
    try {
      const a = authorityPeer({ playerId: 'a' });
      const b = authorityPeer({ playerId: 'b' });
      const aMsgs = record(a, 'message');
      a.start();
      b.start();
      await flush();
      aMsgs.length = 0; // drop the join-time roster re-broadcast

      b.sendToHost({ kind: 'legacy-move' });
      await flush();

      expect(warnings.some((w) => w.includes('_kb envelope is the contract'))).toBe(true);
      expect(serverFrames(aMsgs)).toHaveLength(0); // never reached the module
    } finally {
      console.warn = origWarn;
    }
  });
});

describe('authority mode — fidelity checks', () => {
  it('throws when a patch carries a function (names the boundary)', async () => {
    const a = peer({
      playerId: 'a',
      authority: () => ({
        init() {},
        applyIntent() { return { cb() {} }; },
        snapshot() { return {}; },
      }),
    });
    a.start();
    await flush();

    // Process-mode delivery is synchronous, so the fidelity violation surfaces at the send site.
    expect(() => a.sendToHost({ _kb: 'intent', action: {} }))
      .toThrow(/applyIntent result.*function/);
  });

  it('throws when the module reaches for Date instead of kb.now(), and restores Date after', async () => {
    const RealDate = Date;
    const a = peer({
      playerId: 'a',
      authority: () => ({
        init() {},
        applyIntent() { return { t: Date.now() }; },
        snapshot() { return {}; },
      }),
    });
    a.start();
    await flush();

    expect(() => a.sendToHost({ _kb: 'intent', action: {} })).toThrow(/kb\.now\(\)/);
    expect(globalThis.Date).toBe(RealDate); // restored even on the throwing path
  });

  it('kb.now() works and returns a number', async () => {
    let sawNow = null;
    const a = peer({
      playerId: 'a',
      authority: (kb) => ({
        init() { sawNow = kb.now(); },
        applyIntent() { return null; },
        snapshot() { return {}; },
      }),
    });
    a.start();
    await flush();

    expect(typeof sawNow).toBe('number');
    expect(sawNow).toBeGreaterThan(0);
  });

  it('scanAuthorityImports enforces the single-file rule', () => {
    expect(() => scanAuthorityImports("import x from './y.js';\nexport function createAuthority() {}"))
      .toThrow(/single-file/);
    expect(() => scanAuthorityImports("export { a } from './b.js';"))
      .toThrow(/single-file/);
    expect(() => scanAuthorityImports('export function createAuthority(kb) {}\nexport const config = {};'))
      .not.toThrow();
  });
});

describe('authority mode — owner emulation & module errors', () => {
  function successionAuthority(kb) {
    let state = { count: 0 };
    return {
      init() {},
      applyIntent(fromId, action) {
        if (action.kind === 'promote') { kb.setOwner(action.target); return { promoted: action.target }; }
        if (action.kind === 'boom') throw new Error('module bug');
        state.count += 1;
        return { count: state.count };
      },
      snapshot() { return state; },
    };
  }

  it('kb.setOwner updates every peer and fires owner-changed AFTER the delta', async () => {
    const a = peer({ playerId: 'a', authority: successionAuthority });
    const b = peer({ playerId: 'b', authority: successionAuthority });
    const order = [];
    a.events.on('message', (m) => { if (m.from === 'server' && m.payload._kb === 'delta') order.push('delta'); });
    a.events.on('owner-changed', (id) => order.push('owner:' + id));
    const bOwner = record(b, 'owner-changed');
    a.start();
    b.start();
    await flush();

    b.sendToHost({ _kb: 'intent', action: { kind: 'promote', target: 'b' } });
    await flush();

    expect(order).toEqual(['delta', 'owner:b']); // the server's ordering rule
    expect(bOwner).toEqual(['b']);
    expect(a.ownerId).toBe('b');
    expect(a.isOwner).toBe(false);
    expect(b.isOwner).toBe(true);
  });

  it('kb.setOwner to a non-member is ignored with an error log', async () => {
    const errors = [];
    const origError = console.error;
    console.error = (...args) => errors.push(args.join(' '));
    try {
      const a = peer({ playerId: 'a', authority: successionAuthority });
      const owner = record(a, 'owner-changed');
      a.start();
      await flush();

      a.sendToHost({ _kb: 'intent', action: { kind: 'promote', target: 'stranger' } });
      await flush();

      expect(owner).toEqual([]);
      expect(a.ownerId).toBe('a');
      expect(errors.some((e) => e.includes('not a lobby member'))).toBe(true);
    } finally {
      console.error = origError;
    }
  });

  it('a module throw is contained: logged, dropped, and state re-broadcast for convergence', async () => {
    const errors = [];
    const origError = console.error;
    console.error = (...args) => errors.push(args.join(' '));
    try {
      const a = peer({ playerId: 'a', authority: successionAuthority });
      const aMsgs = record(a, 'message');
      a.start();
      await flush();

      a.sendToHost({ _kb: 'intent', action: { kind: 'boom' } });
      await flush();

      expect(errors.some((e) => e.includes('module error'))).toBe(true);
      const frames = serverFrames(aMsgs);
      expect(frames).toHaveLength(1); // no delta — only the convergence re-sync
      expect(frames[0].payload._kb).toBe('state');
      expect(frames[0].payload.state).toEqual({ count: 0 }); // unchanged

      // The module survived: the next intent works.
      a.sendToHost({ _kb: 'intent', action: { kind: 'inc' } });
      await flush();
      expect(serverFrames(aMsgs).some((m) => m.payload._kb === 'delta')).toBe(true);
    } finally {
      console.error = origError;
    }
  });
});

describe('authority mode — KBAuthority end-to-end over the emulation', () => {
  it('a KBAuthority guest converges through the virtual server actor', async () => {
    // The same client contract games ship with: KBAuthority subscribes before start, auto-syncs on
    // ready (everyone is a guest), and adopts only from:'server' frames.
    const a = authorityPeer({ playerId: 'a' });
    const b = authorityPeer({ playerId: 'b' });
    const model = {
      state: null,
      applyIntent() { throw new Error('never runs on a guest'); },
      applyPatch(patch) { Object.assign(this.state, patch); },
      snapshot() { return this.state; },
      applySnapshot(s) { this.state = s; },
    };
    const auth = new KBAuthority(b, model, { devChecks: false });
    a.start();
    b.start();
    await flush();

    expect(model.state).toEqual({ count: 0, ids: ['a', 'b'] }); // the auto-sync adopted the snapshot

    auth.sendIntent({ kind: 'inc' });
    await flush();

    expect(model.state.count).toBe(1); // the from:'server' delta converged the guest
  });
});
