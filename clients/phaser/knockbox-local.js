// KnockBox LOCAL testing client for Phaser — a drop-in replacement for knockbox-plugin.js that needs
// NO server and NO ticket. It exposes the exact same public API (events, players, isHost,
// sendToHost/All/To, kickPlayer, …) so game code and KBAuthority are unchanged; you only swap the
// `plugin:` class (and pick a `mode`) in your dev game config.
//
// Three transports, chosen by `mode`:
//   • 'tab'     (default) — BroadcastChannel: every browser TAB on the same origin is a separate
//                           player in one shared local lobby. Manual multiplayer with zero infra.
//   • 'process'           — an in-process hub: many peers in ONE JS realm message each other
//                           (deterministic, synchronous). For automated tests (Node/Vitest/jsdom).
//   • 'solo'              — a single-player host that echoes its own sends. "Just run my scene."
//
// Drop-in (dev config):
//   import { KnockBoxLocalPlugin } from './addons/knockbox/knockbox-local.js';
//   plugins: { global: [{ key:'KnockBox', plugin: KnockBoxLocalPlugin, start:true,
//                         mapping:'knockbox', data:{ mode:'tab' } }] }
//
// Automated test (no Phaser):
//   const { KnockBoxLocalPeer, _resetLocalHubs } = require('./knockbox-local.js');
//   const a = new KnockBoxLocalPeer({ mode:'process', channel:'t', playerId:'a' });
//   const b = new KnockBoxLocalPeer({ mode:'process', channel:'t', playerId:'b' });
//   a.events.on('message', m => ...); a.start(); b.start(); a.sendToAll({ hi:1 });
//
// Host = the first peer to join (lowest joinedAt). When the host leaves, the lobby ENDS: remaining
// peers get `player-left` for the host then `closed` (matching the real server — no host migration).
//
// SERVER-AUTHORITY emulation: pass `authority:` (the game's real authority.js — a createAuthority
// function, a module namespace, or a URL string) and the elected peer runs it as a virtual server
// actor: every peer becomes a guest (isHost:false, authority:'server', ownerId set), to:'host'
// frames feed the module, and its deltas/snapshots come back stamped from:'server' — the
// byte-identical server-mode path, with default-on fidelity checks (strict JSON boundary, Date
// poisoning, single-file import scan). See the LocalAuthorityActor section below.
(function (root, factory) {
  if (typeof module === 'object' && module.exports) {
    module.exports = factory(require('./kb-core.js'), root.Phaser);
  } else if (typeof define === 'function' && define.amd) {
    define(['./kb-core', 'Phaser'], factory);
  } else {
    var api = factory(root.KnockBoxCore, root.Phaser);
    root.KnockBoxLocalPlugin = api.KnockBoxLocalPlugin;
    root.KnockBoxLocalPeer = api.KnockBoxLocalPeer;
  }
})(typeof globalThis !== 'undefined' ? globalThis : (typeof self !== 'undefined' ? self : this), function (KBCore, Phaser) {
  'use strict';

  // Defer in FIFO order so a listener attached right after construction isn't missed, and so two
  // peers started back-to-back register in call order (mirrors Godot's call_deferred).
  var defer = (typeof queueMicrotask === 'function')
    ? queueMicrotask
    : function (fn) { setTimeout(fn, 0); };

  function randomId() {
    // Short, collision-unlikely id for a local player. (Runtime code — Math.random is fine here.)
    return 'p-' + Math.random().toString(36).slice(2, 8);
  }

  function makeEmitter() {
    if (Phaser && Phaser.Events && Phaser.Events.EventEmitter) return new Phaser.Events.EventEmitter();
    var listeners = {};
    return {
      on: function (e, fn, ctx) { (listeners[e] = listeners[e] || []).push({ fn: fn, ctx: ctx }); return this; },
      once: function (e, fn, ctx) {
        var self = this;
        function wrap() { self.off(e, wrap); fn.apply(ctx, arguments); }
        wrap.fn = fn; // allow off() by original fn
        return this.on(e, wrap, ctx);
      },
      off: function (e, fn) {
        if (!listeners[e]) return this;
        listeners[e] = listeners[e].filter(function (l) { return l.fn !== fn && l.fn.fn !== fn; });
        return this;
      },
      emit: function (e) {
        var args = Array.prototype.slice.call(arguments, 1);
        (listeners[e] || []).slice().forEach(function (l) { l.fn.apply(l.ctx, args); });
        return this;
      },
      removeAllListeners: function () { listeners = {}; return this; },
      destroy: function () { listeners = {}; },
    };
  }

  // ── In-process hub registry (the 'process' transport's shared relay) ──────────────────────────
  // Keyed by channel name; the JS analog of Godot's kb_local_relay.gd.
  var hubs = {};

  function getHub(channel) {
    if (!hubs[channel]) hubs[channel] = new Hub(channel);
    return hubs[channel];
  }

  function Hub(channel) {
    this.channel = channel;
    this.peers = []; // [{ id, displayName, peer }], index 0 = host
  }
  Hub.prototype.roster = function () {
    return this.peers.map(function (e) { return { id: e.id, displayName: e.displayName }; });
  };
  Hub.prototype.hostId = function () { return this.peers.length ? this.peers[0].id : ''; };
  Hub.prototype.indexOf = function (id) {
    for (var i = 0; i < this.peers.length; i++) if (this.peers[i].id === id) return i;
    return -1;
  };
  Hub.prototype.register = function (entry) {
    var isHost = this.peers.length === 0;
    this.peers.push(entry);
    var player = { id: entry.id, displayName: entry.displayName };
    var roster = this.roster();
    // Tell already-present peers about the newcomer, then hand the newcomer its session.
    for (var i = 0; i < this.peers.length; i++) {
      if (this.peers[i].id !== entry.id) this.peers[i].peer._onJoined(roster, player);
    }
    entry.peer._onReady(roster, isHost);
  };
  Hub.prototype.deliver = function (to, from, payload) {
    // `to` rides along so the receiving peer can divert to-host frames into its authority actor.
    if (to === 'all') {
      this.peers.forEach(function (e) { e.peer._onDeliver(from, payload, to); });
    } else if (to === 'host') {
      if (this.peers.length) this.peers[0].peer._onDeliver(from, payload, to);
    } else {
      for (var i = 0; i < this.peers.length; i++) {
        if (this.peers[i].id === to) { this.peers[i].peer._onDeliver(from, payload, to); return; }
      }
    }
  };
  // Owner-changed control event (the local analog of OwnerChanged/GameOwnerChanged): every peer
  // updates ownerId/isOwner and emits 'owner-changed'.
  Hub.prototype.owner = function (ownerId) {
    this.peers.forEach(function (e) { e.peer._onOwnerChanged(ownerId); });
  };
  Hub.prototype.kick = function (byId, targetId) {
    if (!this.peers.length || this.peers[0].id !== byId) return; // only host may kick
    if (targetId === this.peers[0].id) return;                   // can't kick the host
    var idx = this.indexOf(targetId);
    if (idx < 0) return;
    var removed = this.peers.splice(idx, 1)[0];
    var roster = this.roster();
    this.peers.forEach(function (e) { e.peer._onLeft(roster, targetId); });
    removed.peer._onClosed(true); // the kicked peer's session ends
  };
  Hub.prototype.leave = function (id) {
    var idx = this.indexOf(id);
    if (idx < 0) return;
    var wasHost = idx === 0;
    this.peers.splice(idx, 1);
    var roster = this.roster();
    if (wasHost) {
      // Host left → lobby ends for everyone (no migration), matching the real server.
      this.peers.forEach(function (e) { e.peer._onLeft(roster, id); e.peer._onClosed(true); });
      this.peers = [];
    } else {
      this.peers.forEach(function (e) { e.peer._onLeft(roster, id); });
    }
    if (!this.peers.length) delete hubs[this.channel];
  };

  // ── Transports ────────────────────────────────────────────────────────────────────────────────
  // Each transport calls back into the peer: _onReady / _onJoined / _onLeft / _onDeliver / _onClosed.

  function SoloTransport(peer) { this.peer = peer; }
  SoloTransport.prototype.start = function () {
    var peer = this.peer;
    defer(function () {
      peer._onReady([{ id: peer.playerId, displayName: peer.displayName }], true);
    });
  };
  SoloTransport.prototype.send = function (to, payload, fromOverride) {
    var peer = this.peer;
    var from = fromOverride || peer.playerId;
    if (to === 'all' || to === 'host' || to === peer.playerId) {
      defer(function () { peer._onDeliver(from, payload, to); });
    }
  };
  SoloTransport.prototype.ownerChanged = function (ownerId) {
    var peer = this.peer;
    defer(function () { peer._onOwnerChanged(ownerId); });
  };
  SoloTransport.prototype.kick = function () { /* nobody else to kick */ };
  SoloTransport.prototype.stop = function () { /* nothing to tear down */ };

  function ProcessTransport(peer) { this.peer = peer; this.hub = null; }
  ProcessTransport.prototype.start = function () {
    var self = this, peer = this.peer;
    defer(function () {
      self.hub = getHub(peer.channel);
      self.hub.register({ id: peer.playerId, displayName: peer.displayName, peer: peer });
    });
  };
  ProcessTransport.prototype.send = function (to, payload, fromOverride) {
    if (this.hub) this.hub.deliver(to, fromOverride || this.peer.playerId, payload);
  };
  ProcessTransport.prototype.ownerChanged = function (ownerId) {
    if (this.hub) this.hub.owner(ownerId);
  };
  ProcessTransport.prototype.kick = function (targetId) {
    if (this.hub) this.hub.kick(this.peer.playerId, targetId);
  };
  ProcessTransport.prototype.stop = function () {
    if (this.hub) { this.hub.leave(this.peer.playerId); this.hub = null; }
  };

  var HEARTBEAT_MS = 1000;
  var PRUNE_MS = 3000;

  function TabTransport(peer) {
    this.peer = peer;
    this.bc = null;
    this.self = null;          // { id, displayName, joinedAt }
    this.members = {};         // id -> { id, displayName, joinedAt, lastSeen }
    this.readyFired = false;
    this.inbox = [];           // GAME deliveries buffered until ready
    this._timers = [];
    this._onUnload = null;
  }
  TabTransport.prototype.start = function () {
    var self = this, peer = this.peer;
    this.self = { id: peer.playerId, displayName: peer.displayName, joinedAt: Date.now() };
    this.members[this.self.id] = { id: this.self.id, displayName: this.self.displayName, joinedAt: this.self.joinedAt, lastSeen: Date.now() };

    this.bc = new BroadcastChannel(peer.channel);
    this.bc.onmessage = function (ev) { self._onMessage(ev.data); };
    this._post('ANNOUNCE', { peer: this.self });

    this._timers.push(setInterval(function () {
      self._post('ANNOUNCE', { peer: self.self });
      self._prune();
    }, HEARTBEAT_MS));

    // Settle window: give existing peers a moment to announce so isHost/roster are correct on the
    // first `ready`.
    this._timers.push(setTimeout(function () { self._settle(); }, peer.settleMs));

    // Best-effort leave notice when the tab closes.
    if (typeof addEventListener === 'function') {
      this._onUnload = function () { self._post('LEAVE', { id: self.self.id }); };
      addEventListener('pagehide', this._onUnload);
      addEventListener('beforeunload', this._onUnload);
    }
  };
  TabTransport.prototype._post = function (kind, body) {
    if (this.bc) this.bc.postMessage(Object.assign({ kind: kind, _src: this.self.id }, body));
  };
  TabTransport.prototype._rosterArray = function () {
    var out = [];
    for (var id in this.members) if (Object.prototype.hasOwnProperty.call(this.members, id)) out.push(this.members[id]);
    out.sort(function (a, b) { return a.joinedAt - b.joinedAt || (a.id < b.id ? -1 : a.id > b.id ? 1 : 0); });
    return out.map(function (m) { return { id: m.id, displayName: m.displayName }; });
  };
  TabTransport.prototype._hostId = function () {
    var r = this._rosterArray();
    return r.length ? r[0].id : '';
  };
  TabTransport.prototype.isHost = function () { return this._hostId() === this.self.id; };
  TabTransport.prototype._settle = function () {
    if (this.readyFired) return;
    this.readyFired = true;
    this.peer._onReady(this._rosterArray(), this.isHost());
    // Flush any messages that arrived before we were ready.
    var inbox = this.inbox; this.inbox = [];
    var peer = this.peer;
    inbox.forEach(function (m) { peer._onDeliver(m.from, m.payload, m.to); });
  };
  TabTransport.prototype._onMessage = function (msg) {
    if (!msg || msg._src === this.self.id) return; // ignore our own (BroadcastChannel won't echo, but guard)
    switch (msg.kind) {
      case 'ANNOUNCE': return this._onAnnounce(msg.peer);
      case 'LEAVE': return this._onPeerGone(msg.id);
      case 'GAME': return this._onGame(msg);
      case 'KICK': return this._onKick(msg.targetId);
      case 'OWNER': return this.peer._onOwnerChanged(msg.ownerId);
    }
  };
  TabTransport.prototype._onAnnounce = function (p) {
    if (!p || !p.id) return;
    var known = !!this.members[p.id];
    this.members[p.id] = { id: p.id, displayName: p.displayName, joinedAt: p.joinedAt, lastSeen: Date.now() };
    if (!known) {
      // Reply so the newcomer learns about us too (only on first sighting → no reply storm).
      this._post('ANNOUNCE', { peer: this.self });
      if (this.readyFired) this.peer._onJoined(this._rosterArray(), { id: p.id, displayName: p.displayName });
    }
  };
  TabTransport.prototype._onPeerGone = function (id) {
    if (!this.members[id]) return;
    var wasHost = this._hostId() === id;
    delete this.members[id];
    if (!this.readyFired) return;
    if (wasHost) {
      // Host left → lobby ends (no migration).
      this.peer._onLeft(this._rosterArray(), id);
      this.peer._onClosed(true);
      this.stop();
    } else {
      this.peer._onLeft(this._rosterArray(), id);
    }
  };
  TabTransport.prototype._prune = function () {
    var now = Date.now(), gone = [];
    for (var id in this.members) {
      if (id === this.self.id) continue;
      if (now - this.members[id].lastSeen > PRUNE_MS) gone.push(id);
    }
    for (var i = 0; i < gone.length; i++) this._onPeerGone(gone[i]);
  };
  TabTransport.prototype._onGame = function (msg) {
    var deliver = msg.to === 'all' || msg.to === this.self.id || (msg.to === 'host' && this.isHost());
    if (!deliver) return;
    if (this.readyFired) this.peer._onDeliver(msg.from, msg.payload, msg.to);
    else this.inbox.push({ from: msg.from, payload: msg.payload, to: msg.to });
  };
  TabTransport.prototype._onKick = function (targetId) {
    if (targetId === this.self.id) { this.peer._onClosed(true); this.stop(); return; }
    this._onPeerGone(targetId);
  };
  TabTransport.prototype.send = function (to, payload, fromOverride) {
    var from = fromOverride || this.self.id;
    this._post('GAME', { to: to, from: from, payload: payload });
    // BroadcastChannel never delivers to the poster, so echo to ourselves when we're a recipient —
    // including the authority actor's own broadcasts, so the actor tab's game gets them too.
    var selfDeliver = to === 'all' || to === this.self.id || (to === 'host' && this.isHost());
    if (selfDeliver) {
      var peer = this.peer;
      defer(function () { peer._onDeliver(from, payload, to); });
    }
  };
  TabTransport.prototype.ownerChanged = function (ownerId) {
    this._post('OWNER', { ownerId: ownerId });
    // BroadcastChannel never echoes to the poster — apply locally too.
    var peer = this.peer;
    defer(function () { peer._onOwnerChanged(ownerId); });
  };
  TabTransport.prototype.kick = function (targetId) {
    if (!this.isHost() || targetId === this.self.id) return;
    this._post('KICK', { targetId: targetId });
    this._onPeerGone(targetId); // remove locally too (we won't receive our own post)
  };
  TabTransport.prototype.stop = function () {
    if (!this.bc) return;
    this._timers.forEach(function (t) { clearTimeout(t); clearInterval(t); });
    this._timers = [];
    if (this._onUnload && typeof removeEventListener === 'function') {
      removeEventListener('pagehide', this._onUnload);
      removeEventListener('beforeunload', this._onUnload);
    }
    try { this._post('LEAVE', { id: this.self.id }); } catch (e) { /* channel may be closing */ }
    try { this.bc.close(); } catch (e) { /* ignore */ }
    this.bc = null;
  };

  function makeTransport(peer) {
    switch (peer.mode) {
      case 'solo': return new SoloTransport(peer);
      case 'process': return new ProcessTransport(peer);
      case 'tab':
      default:
        if (typeof BroadcastChannel === 'undefined') {
          console.warn('[KnockBox] BroadcastChannel unavailable; falling back to solo mode.');
          return new SoloTransport(peer);
        }
        return new TabTransport(peer);
    }
  }

  // ── Local authority emulation (server-authority mode without a server) ───────────────────────
  // With an `authority:` option, the transport-elected host peer becomes a virtual server actor:
  // it instantiates the developer's REAL authority.js module and runs the server's loop (intent →
  // applyIntent → delta; sync → snapshot → state; roster change → hook + re-broadcast; optional
  // tick), stamping every send from:'server'. EVERY peer — including the actor host's own game —
  // gets ready with isHost:false / authority:'server' / ownerId, so client code runs the
  // byte-identical server-mode path. Fidelity checks are DEFAULT ON (authority mode IS dev mode):
  //   • JSON round-trip boundary — every value crossing into or out of the module is strict-cloned
  //     (functions, undefined properties, symbols, cycles, and class instances THROW), mirroring
  //     the server's strings-of-JSON boundary.
  //   • Date poisoning — the module runs with globalThis.Date swapped for a throwing stub (the
  //     server deletes Date; kb.now() is the only clock). Math.random is NOT poisoned (allowed v1).
  //   • The URL form of `authority:` is import-scanned before loading (the single-file rule the
  //     server enforces by having no module loader).
  // Known emulation limitation: locally the module's state lives in the elected peer, so closing
  // the actor tab still ends the session — the REAL server survives the creator leaving. Ownership
  // (kb.setOwner) is still fully emulated; owner and actor-host are separate concepts here too.

  var RealDate = Date; // captured before any poisoning can be active

  function fidelityError(message) {
    var err = new Error('[KnockBox authority] ' + message);
    err.kbFidelity = true; // fidelity violations propagate loudly; module bugs are contained
    return err;
  }

  function PoisonDate() {
    throw fidelityError('authority modules must use kb.now() — the server deletes the Date global');
  }
  PoisonDate.now = PoisonDate.parse = PoisonDate.UTC = function () {
    throw fidelityError('authority modules must use kb.now() — the server deletes the Date global');
  };

  // Strict JSON boundary: throws on anything JSON.stringify would silently mangle, then actually
  // round-trips, so what the module sees/emits locally is exactly what the server would see.
  function strictJsonClone(value, label) {
    if (value === undefined || value === null) return null;
    var seen = [];
    (function check(v, path) {
      if (v === null) return;
      var t = typeof v;
      if (t === 'function' || t === 'symbol') {
        throw fidelityError(label + path + ' is a ' + t + ' — only JSON crosses the authority boundary');
      }
      if (t !== 'object') return;
      if (seen.indexOf(v) !== -1) throw fidelityError(label + path + ' is cyclic — JSON cannot represent it');
      seen.push(v);
      if (Array.isArray(v)) {
        for (var i = 0; i < v.length; i++) check(v[i], path + '[' + i + ']');
      } else {
        var proto = Object.getPrototypeOf(v);
        if (proto !== Object.prototype && proto !== null) {
          var name = (v.constructor && v.constructor.name) || 'unknown';
          throw fidelityError(label + path + ' is a class instance (' + name + ') — only plain objects cross the authority boundary');
        }
        for (var k in v) {
          if (!Object.prototype.hasOwnProperty.call(v, k)) continue;
          if (v[k] === undefined) {
            throw fidelityError(label + path + '.' + k + ' is undefined — JSON drops it silently; use null');
          }
          check(v[k], path + '.' + k);
        }
      }
      seen.pop();
    })(value, '');
    return JSON.parse(JSON.stringify(value));
  }

  // Static scan for the single-file rule (the server configures no module loader, so ANY import
  // inside authority.js fails there). Mirrors tools/pack-game's scan — keep the two in sync.
  function scanAuthorityImports(source) {
    var lines = String(source).split('\n');
    for (var i = 0; i < lines.length; i++) {
      var line = lines[i];
      if (/^\s*import[\s('"]/.test(line) || /^\s*export\s+[^;]*\sfrom\s*['"]/.test(line)) {
        throw new Error('[KnockBox authority] authority modules must be single-file (the server has no module loader) — bundle your imports. Offending line ' + (i + 1) + ': ' + line.trim());
      }
    }
  }

  // ── Local kb.words emulation ─────────────────────────────────────────────────────────────────
  // Mirrors the server's word service (AuthorityWordService / WordPoolSet) closely enough that the
  // same authority.js behaves identically: ASCII-only, per-dictionary case folding, and — critically —
  // the SAME pick ordering (length buckets ascending, ordinal within a length, one contiguous global
  // index). A naive array would drift; the shared-fixture Vitest/xUnit test pins this.
  function isAsciiWord(s) {
    for (var i = 0; i < s.length; i++) if (s.charCodeAt(i) > 127) return false;
    return true;
  }

  function buildLocalWordPool(list, caseInsensitive) {
    var byLength = {}; // len -> Set of normalized words (dedupe)
    for (var i = 0; i < list.length; i++) {
      if (list[i] == null) continue;
      var trimmed = String(list[i]).trim();
      if (trimmed.length === 0 || !isAsciiWord(trimmed)) continue;
      var w = caseInsensitive ? trimmed.toLowerCase() : trimmed;
      (byLength[w.length] || (byLength[w.length] = new Set())).add(w);
    }
    var lengths = Object.keys(byLength).map(Number).sort(function (a, b) { return a - b; });
    var order = [], perLength = {};
    lengths.forEach(function (len) {
      var arr = Array.from(byLength[len]).sort(); // default sort is code-unit order == ordinal for ASCII
      perLength[len] = arr;
      for (var j = 0; j < arr.length; j++) order.push(arr[j]);
    });
    return { order: order, perLength: perLength, set: new Set(order), caseInsensitive: caseInsensitive };
  }

  // Mirrors AuthorityOptions.DefaultMaxWordsPerCall. Kept as a named constant on both sides because a
  // silent disagreement here is the worst kind: the module works in the tab and truncates on the server.
  var maxWordsPerCall = 512;

  function makeWordsCapability(pools) {
    function pool(dict) { return Object.prototype.hasOwnProperty.call(pools, dict) ? pools[dict] : null; }
    function norm(p, word) {
      if (typeof word !== 'string' || !isAsciiWord(word)) return null; // server returns false for non-ASCII
      return p.caseInsensitive ? word.toLowerCase() : word;
    }
    function idx(v) { var n = Math.trunc(Number(v)); return Number.isFinite(n) ? n : NaN; }
    return Object.freeze({
      has: function (dict, word) {
        var p = pool(dict); if (!p) return false;
        var q = norm(p, word); return q !== null && p.set.has(q);
      },
      count: function (dict) { var p = pool(dict); return p ? p.order.length : 0; },
      pick: function (dict, index) {
        var p = pool(dict); if (!p) return null;
        var i = idx(index);
        return (i >= 0 && i < p.order.length) ? p.order[i] : null;
      },
      countOfLength: function (dict, len) {
        var p = pool(dict); if (!p) return 0;
        var arr = p.perLength[idx(len)]; return arr ? arr.length : 0;
      },
      pickOfLength: function (dict, len, index) {
        var p = pool(dict); if (!p) return null;
        var arr = p.perLength[idx(len)]; if (!arr) return null;
        var i = idx(index);
        return (i >= 0 && i < arr.length) ? arr[i] : null;
      },
      // [start, end) of the words of `len` beginning with `prefix`. Server-side this is two binary
      // searches over the packed buffer; locally the bucket is a sorted array, so the same two bounds
      // come out of the same two searches. Emulated rather than skipped because a game that only ever
      // ran locally would otherwise write against a capability that is absent in the tab and present
      // on the server — the exact asymmetry this file exists to prevent.
      rangeOfPrefix: function (dict, len, prefix) {
        var p = pool(dict); if (!p) return null;
        if (typeof prefix !== 'string') return null;
        var arr = p.perLength[idx(len)]; if (!arr) return [0, 0];
        if (prefix.length === 0) return [0, arr.length];          // every word has the empty prefix
        if (prefix.length > idx(len) || !isAsciiWord(prefix)) return [0, 0];
        var q = p.caseInsensitive ? prefix.toLowerCase() : prefix;
        var n = q.length;
        function bound(inclusive) {
          var lo = 0, hi = arr.length;
          while (lo < hi) {
            var mid = (lo + hi) >> 1;
            var head = arr[mid].slice(0, n);
            // `inclusive` picks which side an exact prefix match falls on, which is what turns one
            // search into the lower bound and the other into the upper.
            if (head < q || (inclusive && head === q)) lo = mid + 1; else hi = mid;
          }
          return lo;
        }
        var start = bound(false);
        return [start, Math.max(start, bound(true))];
      },
      // A slice of a length bucket in one call. `maxWordsPerCall` mirrors the server's cap so a module
      // tuned locally cannot discover the truncation only in production.
      pickRange: function (dict, len, start, count) {
        var p = pool(dict); if (!p) return null;
        var arr = p.perLength[idx(len)]; if (!arr) return [];
        var s = idx(start), c = idx(count);
        if (!(s >= 0) || s >= arr.length || !(c > 0)) return [];
        c = Math.min(c, maxWordsPerCall, arr.length - s);
        return arr.slice(s, s + c);
      },
    });
  }

  // Builds the capability from a plain spec map (no fetching) — used by _resolveWords once lists are
  // in hand, and exported for tests. Each value is an array of words or { words|list, caseInsensitive }.
  function buildLocalWordsFromSpec(spec) {
    var pools = {};
    for (var k in spec) {
      if (!Object.prototype.hasOwnProperty.call(spec, k)) continue;
      var v = spec[k];
      var list = Array.isArray(v) ? v : (v.words || v.list || []);
      var ci = Array.isArray(v) ? true : (v.caseInsensitive !== false);
      pools[k] = buildLocalWordPool(list, ci);
    }
    return makeWordsCapability(pools);
  }

  var EMPTY_WORDS = makeWordsCapability({});

  function LocalAuthorityActor(peer, createAuthority, config, words) {
    if (typeof createAuthority !== 'function') {
      throw new Error('[KnockBox authority] the authority option must provide a createAuthority(kb) function');
    }
    this.peer = peer;
    this.config = config || {};
    this.perRecipient = !!this.config.perRecipient;
    this._pendingOwner = null;
    this._tickTimer = null;
    this._lastTick = RealDate.now();

    var self = this;
    var kb = Object.freeze({
      now: function () { return RealDate.now(); },
      // Locally there is no Jint and no per-call timeout, so the honest emulation is "you have the
      // server's default budget and nothing is counting it down". A module that paces itself with this
      // then behaves in the tab as it would on a server it is comfortably inside — which is the point.
      // What it CANNOT do is let the tab tell you whether you fit; only the server can answer that.
      budgetRemainingMs: function () { return 250; },
      setOwner: function (playerId) { self._pendingOwner = playerId; }, // deferred, like the server
      setLobbyOpen: function (open) {
        console.info('[KnockBox authority] setLobbyOpen(' + !!open + ') — no-op locally (no join gate)');
      },
      log: Object.freeze({
        debug: function (m) { (console.debug || console.log).call(console, '[KnockBox authority] ' + m); },
        info: function (m) { (console.info || console.log).call(console, '[KnockBox authority] ' + m); },
        warn: function (m) { console.warn('[KnockBox authority] ' + m); },
        error: function (m) { console.error('[KnockBox authority] ' + m); },
      }),
      words: words || EMPTY_WORDS,
    });

    this.instance = createAuthority(kb);
    if (!this.instance || typeof this.instance.applyIntent !== 'function' || typeof this.instance.snapshot !== 'function'
        || typeof this.instance.init !== 'function') {
      throw new Error('[KnockBox authority] createAuthority(kb) must return an object with init, applyIntent, and snapshot');
    }
  }

  // Called once from the elected peer's ready, with the initial roster.
  LocalAuthorityActor.prototype.init = function (roster) {
    this._invoke('init', [roster]);
    this._applyEffects();
    var hz = Number(this.config.tickHz) || 0;
    if (typeof this.instance.tick === 'function' && hz > 0) {
      var self = this;
      this._tickTimer = setInterval(function () { self._tick(); }, 1000 / hz);
    }
  };

  LocalAuthorityActor.prototype.destroy = function () {
    if (this._tickTimer) { clearInterval(this._tickTimer); this._tickTimer = null; }
  };

  // The relay divert target: a to:'host' frame from any peer (including the actor host's own game).
  LocalAuthorityActor.prototype.handleFrame = function (fromId, payload) {
    var kind = payload && payload._kb;
    var self = this;
    if (kind === 'intent') {
      this._contained('applyIntent', function () {
        var patch = self._invoke('applyIntent', [fromId, payload.action]);
        if (patch !== null) {
          if (self.perRecipient) self._broadcastState();
          else self._send('all', { _kb: 'delta', patch: patch });
        }
        self._applyEffects();
      });
    } else if (kind === 'sync') {
      this._contained('snapshot', function () {
        self._send(fromId, { _kb: 'state', state: self._invoke('snapshot', [fromId]) });
        self._applyEffects();
      });
    } else {
      console.warn('[KnockBox authority] dropping non-contract payload to the authority (kind: ' + kind + ')');
    }
  };

  // Roster hooks (the server's roster-work rule: optional hook, then ALWAYS re-broadcast state).
  LocalAuthorityActor.prototype.playerJoined = function (player) { this._roster('onPlayerJoined', player); };
  LocalAuthorityActor.prototype.playerLeft = function (playerId) { this._roster('onPlayerLeft', playerId); };

  LocalAuthorityActor.prototype._roster = function (hook, arg) {
    var self = this;
    this._contained(hook, function () {
      if (typeof self.instance[hook] === 'function') self._invoke(hook, [arg]);
      self._broadcastState();
      self._applyEffects();
    });
  };

  LocalAuthorityActor.prototype._tick = function () {
    var now = RealDate.now();
    var dt = now - this._lastTick;
    this._lastTick = now;
    var self = this;
    this._contained('tick', function () {
      var patch = self._invoke('tick', [dt]);
      if (patch !== null) {
        if (self.perRecipient) self._broadcastState();
        else self._send('all', { _kb: 'delta', patch: patch });
      }
      self._applyEffects();
    });
  };

  // The server's §7 contained path, minus the fatal escalation (locally the dev just fixes the
  // bug): a module throw is logged, the work dropped, and the UNCHANGED snapshot re-broadcast so
  // clients converge. Fidelity violations are NOT contained — they rethrow to the offending caller
  // so the dev sees them where they happen (in process mode that's the sending test/game line).
  LocalAuthorityActor.prototype._contained = function (context, fn) {
    try {
      fn();
    } catch (err) {
      this._pendingOwner = null; // a failed call's partial effects must not leak
      if (err && err.kbFidelity) throw err;
      console.error('[KnockBox authority] module error in ' + context + ' (intent dropped, state re-broadcast):', err);
      try { this._broadcastState(); }
      catch (resyncErr) { console.error('[KnockBox authority] re-sync after the error also failed:', resyncErr); }
    }
  };

  // One chokepoint for every module invocation: JSON boundary on the way in and out, Date poisoned
  // for the (synchronous) duration of the call.
  LocalAuthorityActor.prototype._invoke = function (name, args) {
    var cleanArgs = [];
    for (var i = 0; i < args.length; i++) cleanArgs.push(strictJsonClone(args[i], name + ' argument ' + i));
    var g = globalThis;
    var prevDate = g.Date;
    g.Date = PoisonDate;
    var result;
    try { result = this.instance[name].apply(this.instance, cleanArgs); }
    finally { g.Date = prevDate; }
    if (result === undefined || result === null) return null;
    return strictJsonClone(result, name + ' result');
  };

  LocalAuthorityActor.prototype._broadcastState = function () {
    if (this.perRecipient) {
      for (var i = 0; i < this.peer.players.length; i++) {
        var id = this.peer.players[i].id;
        this._send(id, { _kb: 'state', state: this._invoke('snapshot', [id]) });
      }
    } else {
      this._send('all', { _kb: 'state', state: this._invoke('snapshot', [null]) });
    }
  };

  LocalAuthorityActor.prototype._send = function (to, envelope) {
    var t = this.peer._transport;
    if (t) t.send(to, envelope, 'server'); // the reserved sender id, like the real relay
  };

  // Deferred kb.setOwner, applied AFTER the invocation's own sends (the server's ordering rule:
  // the owner event always follows the delta of the intent that triggered it).
  LocalAuthorityActor.prototype._applyEffects = function () {
    if (this._pendingOwner == null) return;
    var target = this._pendingOwner;
    this._pendingOwner = null;
    var isMember = this.peer.players.some(function (p) { return p.id === target; });
    if (!isMember) {
      console.error('[KnockBox authority] kb.setOwner(' + JSON.stringify(target) + ') ignored — not a lobby member');
      return;
    }
    var t = this.peer._transport;
    if (t && t.ownerChanged) t.ownerChanged(target);
  };

  // ── KnockBoxLocalPeer ─────────────────────────────────────────────────────────────────────────
  // The transport-agnostic client. Phaser-free; this is what automated tests use and what the
  // plugin composes internally. Public API matches KnockBoxPlugin.
  function KnockBoxLocalPeer(opts) {
    opts = opts || {};
    this.mode = opts.mode || 'tab';
    this.channel = opts.channel || 'knockbox-local';
    this.playerId = opts.playerId || randomId();
    this.displayName = opts.displayName || ('Player-' + String(this.playerId).slice(-4));
    this.settleMs = (opts.settleMs != null) ? opts.settleMs : 250;

    this.players = [];
    this.isHost = false;
    this.reconnected = false; // local sessions never reconnect
    this.isLocal = true;      // marks the local-testing transport; KBAuthority auto-enables dev checks

    // Server-authority emulation (§12a): pass the developer's real authority module and the
    // elected peer runs it as a virtual server actor (see LocalAuthorityActor above). Accepts:
    //   • the createAuthority function itself      authority: createAuthority
    //   • a module namespace / object              authority: await import('./authority.js')
    //   • a URL string (fetched + import-scanned)  authority: './authority.js'
    // `authorityConfig` supplies the config export when the function form is used.
    this._authorityOpt = opts.authority || null;
    this._authorityConfig = opts.authorityConfig || null;
    // Optional word dictionaries for kb.words. A map key -> array | { words|list, caseInsensitive } |
    // { file|url, caseInsensitive }. When `authority:` is a URL, the sibling ./GAME.json's
    // authorityWords are auto-discovered too (explicit keys here win). See _resolveWords.
    this._wordsOpt = opts.words || null;
    this._words = EMPTY_WORDS;
    this._resolved = null; // { createAuthority, config } once the option is resolved
    this._actor = null;    // set on the transport-elected peer in authority mode
    this.authority = this._authorityOpt ? 'server' : 'host';
    this.ownerId = null;
    this.isOwner = false;

    this.events = makeEmitter();

    this._ready = false;
    this._stopped = false;
    this._pending = [];  // outbound sends queued until ready
    this._inbox = [];    // inbound messages that arrived before our own ready
    this._transport = makeTransport(this);

    // There's no server to receive logs locally, so mirror them to the dev console (API parity with
    // the real plugin's log.info / warn / error / …). Level name → the closest console method.
    this.log = KBCore.makeLogger(function (frame) {
      if (typeof console === 'undefined') return;
      var fn = frame.level === 'Warning' ? console.warn
        : (frame.level === 'Error' || frame.level === 'Critical') ? console.error
        : (frame.level === 'Trace' || frame.level === 'Debug') ? (console.debug || console.log)
        : (console.info || console.log);
      fn.call(console, '[KnockBox][' + frame.level + '] ' + frame.message);
    });
  }

  KnockBoxLocalPeer.prototype.start = function () {
    if (this._stopped) return;
    var opt = this._authorityOpt;
    if (!opt) { this._transport.start(); return; }

    // Resolve the authority option before the transport elects a host. The URL form is async
    // (fetch + import-scan + dynamic import); a load failure is LOUD and the session never starts —
    // mirroring the server, where TryStart failing fails lobby creation.
    var self = this;
    var authUrl = typeof opt === 'string' ? opt : null;
    var resolveAuthority = authUrl
      ? this._loadAuthority(authUrl)
      : Promise.resolve(typeof opt === 'function'
          ? { createAuthority: opt, config: this._authorityConfig || {} }
          : { createAuthority: opt.createAuthority, config: opt.config || this._authorityConfig || {} });

    resolveAuthority
      .then(function (resolved) {
        self._resolved = resolved;
        return self._resolveWords(authUrl); // fetch/build word dictionaries before electing a host
      })
      .then(function (words) {
        if (self._stopped) return;
        self._words = words;
        self._transport.start();
      })
      .catch(function (err) {
        console.error('[KnockBox authority] module failed to load — session not started:', err);
        self.events.emit('closed', { terminal: true });
      });
  };

  // Resolves kb.words data from the explicit `words` option and, for the URL form of `authority:`,
  // the sibling ./GAME.json's authorityWords (explicit keys win). URL/file entries are fetched; the
  // built capability mirrors the server's ordering. Missing GAME.json is fine (rely on the option).
  KnockBoxLocalPeer.prototype._resolveWords = function (authUrl) {
    var decls = {}; // key -> { list?: string[], url?: string, caseInsensitive: bool }
    var opt = this._wordsOpt;
    if (opt) {
      for (var k in opt) {
        if (!Object.prototype.hasOwnProperty.call(opt, k)) continue;
        var v = opt[k];
        if (Array.isArray(v)) decls[k] = { list: v, caseInsensitive: true };
        else if (v && typeof v === 'object') {
          decls[k] = {
            list: v.words || v.list || null,
            url: v.url || v.file || null,
            caseInsensitive: v.caseInsensitive !== false,
          };
        }
      }
    }

    var dir = authUrl ? authUrl.replace(/[^/]*$/, '') : null;
    var discover = (authUrl && typeof fetch === 'function')
      ? fetch(dir + 'GAME.json')
          .then(function (res) { return res.ok ? res.json() : null; })
          .then(function (manifest) {
            var words = manifest && manifest.authorityWords;
            if (!words) return;
            for (var key in words) {
              if (!Object.prototype.hasOwnProperty.call(words, key) || decls[key]) continue; // explicit wins
              var d = words[key];
              if (d && d.file) decls[key] = { url: dir + d.file, caseInsensitive: d.caseInsensitive !== false };
            }
          })
          .catch(function () { /* no manifest / not fetchable — rely on the explicit option */ })
      : Promise.resolve();

    return discover.then(function () {
      var keys = Object.keys(decls);
      return Promise.all(keys.map(function (key) {
        var d = decls[key];
        if (d.list) return { key: key, list: d.list, ci: d.caseInsensitive };
        if (d.url && typeof fetch === 'function') {
          return fetch(d.url)
            .then(function (res) {
              if (!res.ok) throw new Error('fetch of ' + d.url + ' failed: ' + res.status);
              return res.text();
            })
            .then(function (text) { return { key: key, list: text.split(/\r?\n/), ci: d.caseInsensitive }; });
        }
        return { key: key, list: [], ci: d.caseInsensitive };
      })).then(function (built) {
        var spec = {};
        built.forEach(function (b) { spec[b.key] = { list: b.list, caseInsensitive: b.ci }; });
        return buildLocalWordsFromSpec(spec);
      });
    });
  };

  // URL form: fetch the source, run the single-file import scan (a relative import would happily
  // resolve in the browser but fail on the server), then dynamic-import for real.
  KnockBoxLocalPeer.prototype._loadAuthority = function (url) {
    return fetch(url)
      .then(function (res) {
        if (!res.ok) throw new Error('fetch of ' + url + ' failed: ' + res.status);
        return res.text();
      })
      .then(function (source) {
        scanAuthorityImports(source);
        return import(/* @vite-ignore */ url);
      })
      .then(function (mod) {
        if (typeof mod.createAuthority !== 'function') {
          throw new Error(url + ' must export a createAuthority(kb) function');
        }
        return { createAuthority: mod.createAuthority, config: mod.config || {} };
      });
  };

  KnockBoxLocalPeer.prototype.sendToHost = function (payload) { this._send('host', payload); };
  KnockBoxLocalPeer.prototype.sendToAll = function (payload) { this._send('all', payload); };
  KnockBoxLocalPeer.prototype.sendTo = function (playerId, payload) { this._send(playerId, payload); };

  // No server-side join gate in the local model — documented no-op for API parity.
  KnockBoxLocalPeer.prototype.setLobbyOpen = function () { /* no-op locally */ };
  // Credentials are meaningless locally — ignored for API parity.
  KnockBoxLocalPeer.prototype.setLaunchParams = function () { /* no-op locally */ };

  KnockBoxLocalPeer.prototype.kickPlayer = function (playerId) {
    if (this._transport) this._transport.kick(playerId);
  };

  KnockBoxLocalPeer.prototype.destroy = function () {
    this._stopped = true;
    if (this._actor) { this._actor.destroy(); this._actor = null; }
    if (this._transport) { this._transport.stop(); this._transport = null; }
    if (this.events) this.events.destroy();
  };

  KnockBoxLocalPeer.prototype._send = function (to, payload) {
    if (this._stopped) return;
    // Mirror the real relay's server-authority rules (§5a/§5d) at the sender, so a game can't
    // accidentally depend on something the real server forbids.
    if (this.authority === 'server') {
      var kind = payload && payload._kb;
      if (kind === 'delta' || kind === 'state') {
        console.warn('[KnockBox] dropped client-sent _kb "' + kind + '" — only the authority publishes state in server-authority mode');
        return;
      }
      if (to === 'host' && !kind) {
        console.warn('[KnockBox] dropped non-_kb payload to "host" — the _kb envelope is the contract in server-authority mode');
        return;
      }
    }
    if (this._ready) this._transport.send(to, payload);
    else this._pending.push({ to: to, payload: payload }); // flush on ready (parity with real plugin)
  };

  // ── Transport callbacks ──
  KnockBoxLocalPeer.prototype._onReady = function (roster, isHost) {
    this.players = roster || [];
    var electedId = this.players.length ? this.players[0].id : null; // index 0 is the elected host on every transport

    if (this._authorityOpt) {
      // The transport-elected peer becomes the virtual server actor. Everyone — including that
      // peer's own game — is then told isHost:false, exactly like the real server-mode Ready.
      if (isHost && !this._actor) {
        try {
          this._actor = new LocalAuthorityActor(this, this._resolved.createAuthority, this._resolved.config, this._words);
          this._actor.init(this.players);
        } catch (err) {
          console.error('[KnockBox authority] failed to start the authority module — session closed:', err);
          this._actor = null;
          this._onClosed(true);
          return;
        }
      }
      this.isHost = false;
      this.ownerId = electedId;
    } else {
      this.isHost = !!isHost;
      this.ownerId = electedId; // host-mode parity fields: the elected host is the owner
    }
    this.isOwner = this.ownerId != null && this.ownerId === this.playerId;
    this._ready = true;
    this.events.emit('ready', {
      playerId: this.playerId,
      players: this.players,
      isHost: this.isHost,
      authority: this.authority,
      ownerId: this.ownerId,
      isOwner: this.isOwner,
    });
    // Flush messages that arrived before we were ready, then queued outbound sends.
    var inbox = this._inbox; this._inbox = [];
    var self = this;
    inbox.forEach(function (m) { self._dispatchDeliver(m.from, m.payload, m.to); });
    var pending = this._pending; this._pending = [];
    var t = this._transport;
    if (t) pending.forEach(function (m) { t.send(m.to, m.payload); });
  };
  KnockBoxLocalPeer.prototype._onDeliver = function (from, payload, to) {
    if (this._stopped) return;
    if (!this._ready) { this._inbox.push({ from: from, payload: payload, to: to }); return; }
    this._dispatchDeliver(from, payload, to);
  };
  // The local analog of the relay's to:'host' divert: on the actor-hosting peer, frames addressed
  // to 'host' feed the module instead of the game's onMessage (there is no host player).
  KnockBoxLocalPeer.prototype._dispatchDeliver = function (from, payload, to) {
    if (this._actor && to === 'host') { this._actor.handleFrame(from, payload); return; }
    this.events.emit('message', { from: from, payload: payload });
  };
  KnockBoxLocalPeer.prototype._onJoined = function (roster, player) {
    this.players = roster || KBCore.rosterAdd(this.players, player);
    this.events.emit('player-joined', player);
    if (this._actor) this._actor.playerJoined(player);
  };
  KnockBoxLocalPeer.prototype._onLeft = function (roster, playerId) {
    this.players = roster || KBCore.rosterRemove(this.players, playerId);
    this.events.emit('player-left', playerId);
    if (this._actor) this._actor.playerLeft(playerId);
  };
  KnockBoxLocalPeer.prototype._onOwnerChanged = function (ownerId) {
    this.ownerId = ownerId;
    this.isOwner = ownerId === this.playerId;
    this.events.emit('owner-changed', ownerId);
  };
  KnockBoxLocalPeer.prototype._onClosed = function (terminal) {
    this._ready = false;
    if (this._actor) { this._actor.destroy(); this._actor = null; }
    this.events.emit('closed', { terminal: !!terminal });
  };

  // ── KnockBoxLocalPlugin ───────────────────────────────────────────────────────────────────────
  // Phaser global plugin wrapping a KnockBoxLocalPeer. Drop-in for KnockBoxPlugin.
  var KnockBoxLocalPlugin = null;
  if (Phaser && Phaser.Plugins && Phaser.Plugins.BasePlugin) {
    KnockBoxLocalPlugin = function (pluginManager) {
      Phaser.Plugins.BasePlugin.call(this, pluginManager);
      this._opts = {};
      this._peer = null;
      this.events = null;
    };
    KnockBoxLocalPlugin.prototype = Object.create(Phaser.Plugins.BasePlugin.prototype);
    KnockBoxLocalPlugin.prototype.constructor = KnockBoxLocalPlugin;

    KnockBoxLocalPlugin.prototype.init = function (data) {
      this._opts = data || {};
      this._peer = new KnockBoxLocalPeer(this._opts);
      this.events = this._peer.events; // available before scenes' create()
    };
    KnockBoxLocalPlugin.prototype.start = function () { this._peer.start(); };
    KnockBoxLocalPlugin.prototype.stop = function () { if (this._peer) this._peer.destroy(); };
    KnockBoxLocalPlugin.prototype.destroy = function () {
      if (this._peer) this._peer.destroy();
      this._peer = null;
      this.events = null;
      Phaser.Plugins.BasePlugin.prototype.destroy.call(this);
    };

    // Forward the send API to the peer.
    ['sendToHost', 'sendToAll', 'sendTo', 'setLobbyOpen', 'kickPlayer', 'setLaunchParams'].forEach(function (m) {
      KnockBoxLocalPlugin.prototype[m] = function () { return this._peer[m].apply(this._peer, arguments); };
    });
    // Mirror the peer's state as read-only properties (log is the peer's console-like logger object).
    ['playerId', 'players', 'isHost', 'authority', 'ownerId', 'isOwner', 'reconnected', 'isLocal', 'log'].forEach(function (prop) {
      Object.defineProperty(KnockBoxLocalPlugin.prototype, prop, {
        get: function () { return this._peer ? this._peer[prop] : undefined; },
        enumerable: true,
      });
    });
  }

  return {
    KnockBoxLocalPlugin: KnockBoxLocalPlugin,
    KnockBoxLocalPeer: KnockBoxLocalPeer,
    // The single-file import scan (also run by tools/pack-game) — exported so tests and tooling
    // can call it without going through the URL loader.
    scanAuthorityImports: scanAuthorityImports,
    // Builds a kb.words capability from a plain spec map (key -> array | { words|list, caseInsensitive })
    // — no fetching. Exported so tests can pin the has/count/pick behaviour and the server-identical
    // pick ordering (shared-fixture parity with the C# WordPoolSet).
    _buildLocalWords: buildLocalWordsFromSpec,
    // Test helper: clear the in-process hub registry between tests.
    _resetLocalHubs: function () { hubs = {}; },
  };
});
