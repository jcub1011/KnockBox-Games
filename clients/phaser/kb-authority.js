// KBAuthority — optional host-authoritative glue on top of the KnockBox Phaser plugin. It implements
// the whole "guest sends intent → host validates, mutates, and broadcasts → everyone renders"
// pattern once, so a game only writes its rules.
//
// The server is a blind relay with no server-side state, so exactly one client (the `isHost` player)
// owns the truth. This helper wires that up:
//   • guests auto-send a sync request on connect AND on reconnect;
//   • the host answers sync/late-joins/reconnects with a full snapshot;
//   • the host applies each intent and broadcasts the resulting (small) delta;
//   • every client applies snapshots/deltas to its local model and gets a 'state-changed' event.
//
// You supply a plain "model" object implementing:
//   applyIntent(fromId, action) -> patch | null
//       Host only. Mutate authoritative state; RETURN a small "patch" value to broadcast to everyone,
//       or null to reject/no-op (nothing is sent). Authorize here using `fromId`.
//   applyPatch(patch)                 // every client applies a broadcast delta
//   snapshot([forPlayerId]) -> object // full state for sync/join/reconnect
//   applySnapshot(state)              // every client adopts a full snapshot
//
// Usage:
//   import KBAuthority from './addons/knockbox/kb-authority.js';
//   const authority = new KBAuthority(this.knockbox, myModel);
//   authority.events.on('state-changed', () => render());
//   authority.sendIntent({ kind: 'roll' });   // from any client
//
// ── Per-recipient (hidden-information) mode ──
// Pass { perRecipient: true } for games where each player must see a DIFFERENT view (secret roles,
// hands, votes-before-reveal). The model then implements:
//   snapshot(forPlayerId) -> object   // the view projected for forPlayerId (default-deny)
//   applyIntent(fromId, action) -> truthy to accept (value ignored; host re-projects to everyone),
//                                   or null to reject.
// In this mode there are no deltas and guests need no model: the host sends each player their own
// snapshot, and `currentView` holds the local player's latest view — render from it (null until the
// first view arrives). applyPatch/applySnapshot are unused here.
//
// 'state-changed' fires whenever the local model may have changed (snapshot, delta, or a roster
// change), so just re-render. Use the plugin's own 'ready' / players / isHost for lobby/roster UI.
(function (root, factory) {
  if (typeof module === 'object' && module.exports) {
    module.exports = factory(root.Phaser);
  } else if (typeof define === 'function' && define.amd) {
    define(['Phaser'], factory);
  } else {
    root.KBAuthority = factory(root.Phaser);
  }
})(typeof globalThis !== 'undefined' ? globalThis : (typeof self !== 'undefined' ? self : this), function (Phaser) {
  'use strict';

  var ENVELOPE = '_kb'; // discriminator marking messages this helper owns (matches the Godot addon)

  // Minimal emitter fallback so this helper works even if Phaser isn't on the global (e.g. Node
  // tests). When Phaser is present we use its EventEmitter for consistency with the rest of the game.
  function makeEmitter() {
    if (Phaser && Phaser.Events && Phaser.Events.EventEmitter) return new Phaser.Events.EventEmitter();
    var listeners = {};
    return {
      on: function (e, fn, ctx) { (listeners[e] = listeners[e] || []).push({ fn: fn, ctx: ctx }); return this; },
      off: function (e, fn) {
        if (!listeners[e]) return this;
        listeners[e] = listeners[e].filter(function (l) { return l.fn !== fn; });
        return this;
      },
      emit: function (e) {
        var args = Array.prototype.slice.call(arguments, 1);
        (listeners[e] || []).slice().forEach(function (l) { l.fn.apply(l.ctx, args); });
        return this;
      },
    };
  }

  // net: a KnockBox Phaser plugin instance. model: the game's authoritative/replicated model.
  // options: { perRecipient?: boolean }
  var KBAuthority = function (net, model, options) {
    options = options || {};
    this._net = net;
    this._model = model;
    this._perRecipient = !!options.perRecipient;

    // The local player's latest projected view in per-recipient mode (null until the first arrives).
    // Render from this instead of the model. Stays null in the default broadcast mode.
    this.currentView = null;

    // Subscribe to 'state-changed' to re-render.
    this.events = makeEmitter();

    var self = this;
    this._onReady = function (info) { self._handleReady(info); };
    this._onMessage = function (m) { self._handleMessage(m.from, m.payload); };
    this._onRoster = function () { self._handleRosterChanged(); };

    net.events.on('ready', this._onReady);
    net.events.on('message', this._onMessage);
    net.events.on('player-joined', this._onRoster);
    net.events.on('player-left', this._onRoster);
  };

  // Send a game intent to the host (works the same on host and guests — the host's own intents loop
  // back through the same path).
  KBAuthority.prototype.sendIntent = function (action) {
    var msg = {};
    msg[ENVELOPE] = 'intent';
    msg.action = action;
    this._net.sendToHost(msg);
  };

  // Convenience for the host's join policy: open/close the lobby to new players.
  KBAuthority.prototype.setOpen = function (open) { this._net.setLobbyOpen(open); };

  // Detach all listeners. Call when tearing down the scene/owner of this helper.
  KBAuthority.prototype.destroy = function () {
    var net = this._net;
    if (net && net.events) {
      net.events.off('ready', this._onReady);
      net.events.off('message', this._onMessage);
      net.events.off('player-joined', this._onRoster);
      net.events.off('player-left', this._onRoster);
    }
    if (this.events && this.events.removeAllListeners) this.events.removeAllListeners();
  };

  KBAuthority.prototype._stateMsg = function (forPlayerId) {
    var msg = {};
    msg[ENVELOPE] = 'state';
    msg.state = this._perRecipient ? this._model.snapshot(forPlayerId) : this._model.snapshot();
    return msg;
  };

  KBAuthority.prototype._handleReady = function (info) {
    if (!info.isHost) {
      // Ask the host for the current state (covers first join, late join and reconnect).
      var sync = {};
      sync[ENVELOPE] = 'sync';
      this._net.sendToHost(sync);
    } else {
      // Host renders its own projected view; guests' views arrive via their sync responses.
      if (this._perRecipient) this.currentView = this._model.snapshot(this._net.playerId);
      // On a HOST reconnect, guests that already synced won't request again, so re-push the
      // authoritative state to everyone — covering anything that changed around the drop.
      if (this._net.reconnected) this._broadcastState();
    }
    // Render whatever we have (host: its initial/own state; guest: until the snapshot lands).
    this.events.emit('state-changed');
  };

  KBAuthority.prototype._handleRosterChanged = function () {
    // Host re-broadcasts the full state so newcomers (and everyone) converge.
    if (this._net.isHost) this._broadcastState();
    this.events.emit('state-changed');
  };

  // Host: push current state to everyone. Per-recipient mode sends each player their own projection
  // and sets the host's own currentView; default mode sends one shared snapshot to all.
  KBAuthority.prototype._broadcastState = function () {
    if (!this._net.isHost) return;
    if (this._perRecipient) {
      var players = this._net.players || [];
      for (var i = 0; i < players.length; i++) {
        var pid = String((players[i] && players[i].id) || '');
        if (pid === this._net.playerId) {
          this.currentView = this._model.snapshot(pid);
        } else {
          this._net.sendTo(pid, this._stateMsg(pid));
        }
      }
    } else {
      this._net.sendToAll(this._stateMsg());
    }
  };

  KBAuthority.prototype._handleMessage = function (fromId, payload) {
    if (!payload || typeof payload !== 'object' || !(ENVELOPE in payload)) {
      return; // not ours (a raw plugin game message) — ignore
    }
    // The host is the single source of truth: it mutates only via applyIntent and never consumes its
    // own broadcast echoes. Guests never mutate locally; they only adopt the host's deltas/snapshots.
    switch (payload[ENVELOPE]) {
      case 'intent':
        if (!this._net.isHost) return; // only the host acts on intents
        var patch = this._model.applyIntent(fromId, payload.action);
        if (patch != null) {
          if (this._perRecipient) {
            // Re-project a fresh per-player snapshot to everyone (patch is just an accept signal).
            this._broadcastState();
          } else {
            var delta = {};
            delta[ENVELOPE] = 'delta';
            delta.patch = patch;
            this._net.sendToAll(delta);
          }
          this.events.emit('state-changed'); // host renders its own mutation (ignores the echo)
        }
        break;
      case 'sync':
        if (this._net.isHost) this._net.sendTo(fromId, this._stateMsg(fromId));
        break;
      case 'delta':
        if (this._net.isHost) return; // already applied via applyIntent; the echo is for guests
        this._model.applyPatch(payload.patch);
        this.events.emit('state-changed');
        break;
      case 'state':
        if (this._net.isHost) return; // host is authoritative; never adopts a snapshot
        if (this._perRecipient) {
          this.currentView = payload.state; // guests render this directly; no model needed
        } else {
          this._model.applySnapshot(payload.state);
        }
        this.events.emit('state-changed');
        break;
    }
  };

  return KBAuthority;
});
