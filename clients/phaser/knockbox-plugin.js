// KnockBox networking plugin for Phaser — the "just send/receive between clients in the same lobby"
// client for games built on Phaser 3/4.
//
// It is a Phaser GLOBAL plugin (one shared WebSocket for the whole game, injected into every scene).
// It reads a lobby-scoped ticket + endpoint from its own URL FRAGMENT (the shell put them there; the
// fragment, unlike a query string, never leaks via Referer or logs), opens its OWN websocket to the
// server, authenticates with the ticket, and exposes a tiny API. The game never sees a lobby id, the
// player's identity, or the shell — the server resolves all routing from the bound connection.
//
// Install (game config):
//   import KnockBoxPlugin from './addons/knockbox/knockbox-plugin.js';   // or <script> global
//   new Phaser.Game({
//     plugins: { global: [{ key: 'KnockBox', plugin: KnockBoxPlugin, start: true, mapping: 'knockbox' }] }
//   });
//
// Use (in any scene, via the `mapping`):
//   this.knockbox.events.on('ready',   ({ playerId, players, isHost }) => { ... });
//   this.knockbox.events.on('message', ({ from, payload }) => { ... });
//   this.knockbox.events.on('player-joined', (player)   => { ... });
//   this.knockbox.events.on('player-left',   (playerId) => { ... });
//   this.knockbox.sendToAll({ kind: 'move', x, y });   // -> everyone incl. self (state)
//   this.knockbox.sendToHost({ kind: 'tap' });         // -> the authoritative host (intent)
//   this.knockbox.sendTo(playerId, { secret: 1 });     // -> one specific player
//
// After 'ready' fires, knockbox.playerId / players / isHost are populated.
//
// Engine note: this speaks the same JSON protocol as the vanilla-JS (web/knockbox.js) and Godot
// clients, so a Phaser game can share a lobby with games built on any of them.
(function (root, factory) {
  if (typeof module === 'object' && module.exports) {
    module.exports = factory(require('./kb-core.js'), root.Phaser);
  } else if (typeof define === 'function' && define.amd) {
    define(['./kb-core', 'Phaser'], factory);
  } else {
    root.KnockBoxPlugin = factory(root.KnockBoxCore, root.Phaser);
  }
})(typeof globalThis !== 'undefined' ? globalThis : (typeof self !== 'undefined' ? self : this), function (KBCore, Phaser) {
  'use strict';

  if (!Phaser) {
    throw new Error('[KnockBox] Phaser must be loaded before knockbox-plugin.js');
  }
  if (!KBCore) {
    // On the global <script> path, kb-core.js must be loaded first (it sets window.KnockBoxCore).
    // Fail loudly here rather than with an opaque "undefined" on the first protocol call.
    throw new Error('[KnockBox] kb-core.js must be loaded before knockbox-plugin.js');
  }

  // We subclass BasePlugin (global plugin) and emit through an internal EventEmitter so a game can
  // do `this.knockbox.events.on(...)`. Using a dedicated emitter (rather than making the plugin the
  // emitter) keeps the plugin's public API surface clean and matches the Godot addon's signal set.
  var KnockBoxPlugin = function (pluginManager) {
    Phaser.Plugins.BasePlugin.call(this, pluginManager);

    // Public state — populated after the first 'ready'.
    this.playerId = null;
    this.players = [];
    this.isHost = false;
    this.reconnected = false; // true when 'ready' fires after a reconnect

    // Subscribe to these for all networking events (see file header for the list).
    this.events = new Phaser.Events.EventEmitter();

    this._ws = null;
    this._ticket = null;
    this._endpoint = null;
    this._attached = false;   // sent the Attach frame on the current socket
    this._hasSession = false; // a 'ready' has fired at least once (=> next one is a reconnect)
    this._attempt = 0;        // consecutive transient connects, for backoff
    this._stopped = false;    // set on terminal close / destroy — don't reconnect
    this._pending = [];       // frames queued before the socket is open & attached
    this._reconnectTimer = null;
  };

  KnockBoxPlugin.prototype = Object.create(Phaser.Plugins.BasePlugin.prototype);
  KnockBoxPlugin.prototype.constructor = KnockBoxPlugin;

  // ── Plugin lifecycle ──────────────────────────────────────────────────────────────────────────

  // `data` comes from the plugin config's `data` field. Supply { ticket, endpoint } here to drive
  // the connection manually (native/editor/local testing) instead of reading the URL fragment.
  KnockBoxPlugin.prototype.init = function (data) {
    if (data && data.ticket) this._ticket = data.ticket;
    if (data && data.endpoint) this._endpoint = data.endpoint;
  };

  KnockBoxPlugin.prototype.start = function () {
    // If credentials weren't supplied via config, read them from the URL fragment the shell set.
    if (!this._ticket && typeof location !== 'undefined') {
      var launch = KBCore.parseLaunchParams(location.hash);
      this._ticket = this._ticket || launch.ticket;
      this._endpoint = this._endpoint || launch.endpoint;

      // Scrub the credentials from the address bar so they don't linger in history or stay readable
      // via location.hash by anything that loads later (analytics, third-party scripts).
      if (location.hash && typeof history !== 'undefined' && history.replaceState) {
        history.replaceState(null, '', location.pathname + location.search);
      }
    }
    if (!this._endpoint && typeof location !== 'undefined') {
      this._endpoint = KBCore.defaultEndpoint(location.protocol, location.host);
    }
    this._connect();
  };

  KnockBoxPlugin.prototype.stop = function () {
    this._teardownSocket();
  };

  KnockBoxPlugin.prototype.destroy = function () {
    this._teardownSocket();
    if (this.events) this.events.destroy();
    this.events = null;
    Phaser.Plugins.BasePlugin.prototype.destroy.call(this);
  };

  // ── Public send API ───────────────────────────────────────────────────────────────────────────

  // Manually supply credentials for native/editor/local testing. Call before the socket connects
  // (e.g. from the plugin `data` config); reconnects after this reuse them.
  KnockBoxPlugin.prototype.setLaunchParams = function (ticket, endpoint) {
    this._ticket = ticket;
    if (endpoint) this._endpoint = endpoint;
  };

  KnockBoxPlugin.prototype.sendToHost = function (payload) { this._send('host', payload); };
  KnockBoxPlugin.prototype.sendToAll = function (payload) { this._send('all', payload); };
  KnockBoxPlugin.prototype.sendTo = function (playerId, payload) { this._send(playerId, payload); };

  // Host-only: whether the lobby accepts new joins (open = listed + joinable). Non-host senders are
  // ignored by the server. The game owns this; the server never changes it on its own.
  KnockBoxPlugin.prototype.setLobbyOpen = function (open) {
    this._sendFrame({ type: 'SetLobbyOpen', open: !!open });
  };

  // Host-only: remove a player from the lobby (they're barred from rejoining). Non-host ignored.
  KnockBoxPlugin.prototype.kickPlayer = function (playerId) {
    this._sendFrame({ type: 'KickPlayer', targetPlayerId: playerId });
  };

  // ── Internals ─────────────────────────────────────────────────────────────────────────────────

  KnockBoxPlugin.prototype._send = function (to, payload) {
    this._sendFrame({ type: 'Game', to: to, payload: payload });
  };

  // Send a frame now if the socket is open & attached; otherwise queue it and flush on Attach. This
  // prevents an eager send (e.g. a guest's first sync on 'ready') from being silently dropped.
  KnockBoxPlugin.prototype._sendFrame = function (frame) {
    var json = JSON.stringify(frame);
    if (this._ws && this._attached && this._ws.readyState === 1 /* OPEN */) {
      this._ws.send(json);
    } else {
      this._pending.push(json);
    }
  };

  KnockBoxPlugin.prototype._flushPending = function () {
    if (!this._pending.length) return;
    for (var i = 0; i < this._pending.length; i++) this._ws.send(this._pending[i]);
    this._pending = [];
  };

  KnockBoxPlugin.prototype._connect = function () {
    if (!this._ticket) {
      console.error('[KnockBox] missing kbTicket — cannot attach.');
      return;
    }
    this._stopped = false;
    this._attached = false;
    var self = this;

    var ws;
    try {
      ws = new WebSocket(this._endpoint);
    } catch (e) {
      console.warn('[KnockBox] connect failed; will retry.', e);
      this._scheduleReconnect();
      return;
    }
    this._ws = ws;

    ws.onopen = function () {
      ws.send(JSON.stringify({ type: 'Attach', ticket: self._ticket, proto: KBCore.PROTOCOL_VERSION }));
      self._attached = true;
      self._flushPending();
    };
    ws.onmessage = function (e) {
      var msg;
      try { msg = JSON.parse(e.data); } catch (err) { return; }
      if (msg && typeof msg === 'object') self._handle(msg);
    };
    ws.onerror = function () { /* a failed connect surfaces as a close; reconnect is handled there */ };
    ws.onclose = function (e) {
      self._ws = null;
      self._attached = false;
      var terminal = KBCore.isTerminalClose(e.code);
      if (self.events) self.events.emit('closed', { terminal: terminal });
      if (terminal) {
        // The ticket is invalid or our lobby membership ended — retrying is pointless.
        self._stopped = true;
        console.warn('[KnockBox] data socket closed permanently:', e.reason || e.code);
        return;
      }
      self._scheduleReconnect();
    };
  };

  KnockBoxPlugin.prototype._scheduleReconnect = function () {
    if (this._stopped) return;
    var self = this;
    var delay = KBCore.reconnectDelay(this._attempt++);
    this._reconnectTimer = setTimeout(function () { self._connect(); }, delay);
  };

  KnockBoxPlugin.prototype._teardownSocket = function () {
    this._stopped = true;
    if (this._reconnectTimer) { clearTimeout(this._reconnectTimer); this._reconnectTimer = null; }
    if (this._ws) {
      // Drop handlers so a programmatic close doesn't trigger a reconnect.
      this._ws.onopen = this._ws.onmessage = this._ws.onerror = this._ws.onclose = null;
      try { this._ws.close(); } catch (e) { /* ignore */ }
      this._ws = null;
    }
    this._attached = false;
    this._pending = [];
  };

  KnockBoxPlugin.prototype._handle = function (msg) {
    switch (msg.type) {
      case 'Ready':
        this.playerId = msg.playerId;
        this.players = msg.players || [];
        this.isHost = !!msg.isHost;
        this._attempt = 0; // healthy connection — reset backoff
        this.reconnected = this._hasSession; // a prior session existed => this is a reconnect
        this._hasSession = true;
        this.events.emit('ready', { playerId: this.playerId, players: this.players, isHost: this.isHost });
        if (this.reconnected) this.events.emit('resumed');
        break;
      case 'Game':
        this.events.emit('message', { from: msg.from, payload: msg.payload });
        break;
      case 'GamePlayerJoined':
        this.players = KBCore.rosterAdd(this.players, msg.player);
        this.events.emit('player-joined', msg.player);
        break;
      case 'GamePlayerLeft':
        this.players = KBCore.rosterRemove(this.players, msg.playerId);
        this.events.emit('player-left', msg.playerId);
        break;
    }
  };

  return KnockBoxPlugin;
});
