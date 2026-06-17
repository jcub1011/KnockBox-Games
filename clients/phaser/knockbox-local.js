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
    if (to === 'all') {
      this.peers.forEach(function (e) { e.peer._onDeliver(from, payload); });
    } else if (to === 'host') {
      if (this.peers.length) this.peers[0].peer._onDeliver(from, payload);
    } else {
      for (var i = 0; i < this.peers.length; i++) {
        if (this.peers[i].id === to) { this.peers[i].peer._onDeliver(from, payload); return; }
      }
    }
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
  SoloTransport.prototype.send = function (to, payload) {
    var peer = this.peer;
    if (to === 'all' || to === 'host' || to === peer.playerId) {
      defer(function () { peer._onDeliver(peer.playerId, payload); });
    }
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
  ProcessTransport.prototype.send = function (to, payload) {
    if (this.hub) this.hub.deliver(to, this.peer.playerId, payload);
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
    inbox.forEach(function (m) { peer._onDeliver(m.from, m.payload); });
  };
  TabTransport.prototype._onMessage = function (msg) {
    if (!msg || msg._src === this.self.id) return; // ignore our own (BroadcastChannel won't echo, but guard)
    switch (msg.kind) {
      case 'ANNOUNCE': return this._onAnnounce(msg.peer);
      case 'LEAVE': return this._onPeerGone(msg.id);
      case 'GAME': return this._onGame(msg);
      case 'KICK': return this._onKick(msg.targetId);
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
    if (this.readyFired) this.peer._onDeliver(msg.from, msg.payload);
    else this.inbox.push({ from: msg.from, payload: msg.payload });
  };
  TabTransport.prototype._onKick = function (targetId) {
    if (targetId === this.self.id) { this.peer._onClosed(true); this.stop(); return; }
    this._onPeerGone(targetId);
  };
  TabTransport.prototype.send = function (to, payload) {
    this._post('GAME', { to: to, from: this.self.id, payload: payload });
    // BroadcastChannel never delivers to the poster, so echo to ourselves when we're a recipient.
    var selfDeliver = to === 'all' || to === this.self.id || (to === 'host' && this.isHost());
    if (selfDeliver) {
      var peer = this.peer, from = this.self.id;
      defer(function () { peer._onDeliver(from, payload); });
    }
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
    this._transport.start();
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
    if (this._transport) { this._transport.stop(); this._transport = null; }
    if (this.events) this.events.destroy();
  };

  KnockBoxLocalPeer.prototype._send = function (to, payload) {
    if (this._stopped) return;
    if (this._ready) this._transport.send(to, payload);
    else this._pending.push({ to: to, payload: payload }); // flush on ready (parity with real plugin)
  };

  // ── Transport callbacks ──
  KnockBoxLocalPeer.prototype._onReady = function (roster, isHost) {
    this.players = roster || [];
    this.isHost = !!isHost;
    this._ready = true;
    this.events.emit('ready', { playerId: this.playerId, players: this.players, isHost: this.isHost });
    // Flush messages that arrived before we were ready, then queued outbound sends.
    var inbox = this._inbox; this._inbox = [];
    var self = this;
    inbox.forEach(function (m) { self.events.emit('message', { from: m.from, payload: m.payload }); });
    var pending = this._pending; this._pending = [];
    var t = this._transport;
    if (t) pending.forEach(function (m) { t.send(m.to, m.payload); });
  };
  KnockBoxLocalPeer.prototype._onDeliver = function (from, payload) {
    if (this._stopped) return;
    if (!this._ready) { this._inbox.push({ from: from, payload: payload }); return; }
    this.events.emit('message', { from: from, payload: payload });
  };
  KnockBoxLocalPeer.prototype._onJoined = function (roster, player) {
    this.players = roster || KBCore.rosterAdd(this.players, player);
    this.events.emit('player-joined', player);
  };
  KnockBoxLocalPeer.prototype._onLeft = function (roster, playerId) {
    this.players = roster || KBCore.rosterRemove(this.players, playerId);
    this.events.emit('player-left', playerId);
  };
  KnockBoxLocalPeer.prototype._onClosed = function (terminal) {
    this._ready = false;
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
    ['playerId', 'players', 'isHost', 'reconnected', 'isLocal', 'log'].forEach(function (prop) {
      Object.defineProperty(KnockBoxLocalPlugin.prototype, prop, {
        get: function () { return this._peer ? this._peer[prop] : undefined; },
        enumerable: true,
      });
    });
  }

  return {
    KnockBoxLocalPlugin: KnockBoxLocalPlugin,
    KnockBoxLocalPeer: KnockBoxLocalPeer,
    // Test helper: clear the in-process hub registry between tests.
    _resetLocalHubs: function () { hubs = {}; },
  };
});
