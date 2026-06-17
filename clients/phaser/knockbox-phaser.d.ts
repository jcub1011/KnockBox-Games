// TypeScript definitions for the KnockBox Phaser networking client.
//
// These are hand-written and only loosely reference Phaser's own types so they work whether or not
// `phaser` is installed with its type definitions. If Phaser's types are present, KnockBoxPlugin is
// assignable where a Phaser.Plugins.BasePlugin is expected.

/**
 * Recursively marks a value immutable. Used to type a replicated render copy (a guest's adopted
 * snapshot, `KBAuthority.currentView`) so accidentally mutating it is a compile error — mutate
 * authoritative state via intents instead. `DeepReadonly<any>` is `any`, so games that don't
 * parameterize the view type are unaffected.
 */
export type DeepReadonly<T> = T extends (infer U)[]
  ? ReadonlyArray<DeepReadonly<U>>
  : T extends object
    ? { readonly [K in keyof T]: DeepReadonly<T[K]> }
    : T;

/** A member of the lobby roster. */
export interface KBPlayer {
  id: string;
  displayName: string;
}

/** Payload of the `ready` event (also mirrored on the plugin's own properties). */
export interface KBReadyInfo {
  playerId: string;
  players: KBPlayer[];
  isHost: boolean;
}

/** Payload of the `message` event: a relayed game message from another player (or self). */
export interface KBMessage {
  /** The sender's player id (the server stamps this). */
  from: string;
  /** The opaque game-defined payload that was sent. */
  payload: any;
}

/** Payload of the `closed` event. */
export interface KBClosed {
  /** True when the socket closed permanently (invalid ticket / ended membership). No reconnect. */
  terminal: boolean;
}

/**
 * Console-like logger that ships lines to the SERVER log (not the player's console). Each method
 * mirrors a Microsoft.Extensions.Logging.LogLevel: info → Information, warn → Warning, etc.
 */
export interface KnockBoxLogger {
  trace(message: string): void;
  debug(message: string): void;
  info(message: string): void;
  warn(message: string): void;
  error(message: string): void;
  critical(message: string): void;
}

/** Maps each KnockBox event name to its listener signature. */
export interface KnockBoxEvents {
  ready: (info: KBReadyInfo) => void;
  message: (msg: KBMessage) => void;
  'player-joined': (player: KBPlayer) => void;
  'player-left': (playerId: string) => void;
  closed: (info: KBClosed) => void;
  resumed: () => void;
}

/** A minimal typed event emitter (compatible with Phaser.Events.EventEmitter). */
export interface KBEmitter<Events> {
  on<K extends keyof Events>(event: K, fn: Events[K], context?: any): this;
  once<K extends keyof Events>(event: K, fn: Events[K], context?: any): this;
  off<K extends keyof Events>(event: K, fn?: Events[K], context?: any): this;
  emit<K extends keyof Events>(event: K, ...args: any[]): boolean;
}

/** Optional config passed via the plugin's `data` field (for native/editor/local testing). */
export interface KnockBoxPluginData {
  ticket?: string;
  endpoint?: string;
}

/**
 * The KnockBox networking plugin — a Phaser GLOBAL plugin. Register it in the game config:
 *
 *   plugins: { global: [{ key: 'KnockBox', plugin: KnockBoxPlugin, start: true, mapping: 'knockbox' }] }
 *
 * then access it in a scene via the mapping, e.g. `this.knockbox`.
 */
export class KnockBoxPlugin {
  constructor(pluginManager: any);

  /** This player's id. Null until `ready` fires. */
  playerId: string | null;
  /** The lobby roster; index 0 is the host. Kept current as players join/leave. */
  players: KBPlayer[];
  /** Whether this player is the authoritative host (the lobby creator). */
  isHost: boolean;
  /** True when the most recent `ready` fired after a reconnect (a prior session existed). */
  reconnected: boolean;

  /** Subscribe to networking events here, e.g. `plugin.events.on('message', cb)`. */
  events: KBEmitter<KnockBoxEvents>;

  /** Console-like logging to the server, e.g. `plugin.log.info('started')`. */
  log: KnockBoxLogger;

  /** Phaser lifecycle — called by the plugin manager. */
  init(data?: KnockBoxPluginData): void;
  start(): void;
  stop(): void;
  destroy(): void;

  /** Manually supply credentials for native/editor/local testing (call before connecting). */
  setLaunchParams(ticket: string, endpoint?: string): void;

  /** Send to the authoritative host (use for intent). */
  sendToHost(payload: any): void;
  /** Send to everyone in the lobby including yourself (use for state). */
  sendToAll(payload: any): void;
  /** Send to one specific player by id (use for hidden info). */
  sendTo(playerId: string, payload: any): void;

  /** Host-only: open/close the lobby to new joins. Non-host calls are ignored by the server. */
  setLobbyOpen(open: boolean): void;
  /** Host-only: remove a player (barred from rejoining). Non-host calls are ignored. */
  kickPlayer(playerId: string): void;
}

export default KnockBoxPlugin;

// ── Local-testing client (no server) ──────────────────────────────────────────────────────────

/** Transport for the local-testing client. */
export type KnockBoxLocalMode =
  /** BroadcastChannel: each same-origin browser tab is a separate player. (default) */
  | 'tab'
  /** In-process hub: many peers in one JS realm message each other. For automated tests. */
  | 'process'
  /** Single-player host that echoes its own sends. */
  | 'solo';

export interface KnockBoxLocalOptions {
  /** Which transport to use. Default 'tab'. */
  mode?: KnockBoxLocalMode;
  /** Lobby/channel name scoping the BroadcastChannel or in-process hub. Default 'knockbox-local'. */
  channel?: string;
  /** This player's id. Default a random id. */
  playerId?: string;
  /** This player's display name. Default `Player-<id>`. */
  displayName?: string;
  /** Cross-tab presence settle window (ms) before the first `ready`. Default 250. */
  settleMs?: number;
}

/**
 * A Phaser-free local-testing client with the same public API as KnockBoxPlugin. Used directly in
 * automated tests, and composed internally by KnockBoxLocalPlugin. `reconnected` is always false;
 * `setLobbyOpen` and `setLaunchParams` are no-ops locally.
 */
export class KnockBoxLocalPeer {
  constructor(options?: KnockBoxLocalOptions);

  playerId: string;
  players: KBPlayer[];
  isHost: boolean;
  reconnected: boolean;
  /** Always true on the local-testing client; lets KBAuthority auto-enable its dev checks. */
  isLocal: boolean;

  events: KBEmitter<KnockBoxEvents>;

  /** Logs to the dev console locally (no server), with the same shape as the real plugin's logger. */
  log: KnockBoxLogger;

  /** Begin the session (connect to the in-process hub / BroadcastChannel, or fire solo ready). */
  start(): void;
  /** End the session and release the transport. */
  destroy(): void;

  sendToHost(payload: any): void;
  sendToAll(payload: any): void;
  sendTo(playerId: string, payload: any): void;
  /** Host-only: remove a player. */
  kickPlayer(playerId: string): void;
  /** No-op locally (no server-side join gate). */
  setLobbyOpen(open: boolean): void;
  /** No-op locally (credentials are meaningless). */
  setLaunchParams(ticket?: string, endpoint?: string): void;
}

/**
 * Drop-in replacement for KnockBoxPlugin that needs no server. Register it exactly like
 * KnockBoxPlugin (global plugin, `mapping`), passing the transport via `data`:
 *
 *   plugins: { global: [{ key:'KnockBox', plugin: KnockBoxLocalPlugin, start:true,
 *                         mapping:'knockbox', data:{ mode:'tab' } }] }
 *
 * Game code and KBAuthority are unchanged — same `events`, properties, and methods.
 */
export class KnockBoxLocalPlugin {
  constructor(pluginManager: any);

  readonly playerId: string | null;
  readonly players: KBPlayer[];
  readonly isHost: boolean;
  readonly reconnected: boolean;
  /** Always true on the local-testing client; lets KBAuthority auto-enable its dev checks. */
  readonly isLocal: boolean;
  readonly log: KnockBoxLogger;

  events: KBEmitter<KnockBoxEvents>;

  init(data?: KnockBoxLocalOptions): void;
  start(): void;
  stop(): void;
  destroy(): void;

  sendToHost(payload: any): void;
  sendToAll(payload: any): void;
  sendTo(playerId: string, payload: any): void;
  kickPlayer(playerId: string): void;
  setLobbyOpen(open: boolean): void;
  setLaunchParams(ticket?: string, endpoint?: string): void;
}

/** Test helper: clear the in-process hub registry between tests. */
export function _resetLocalHubs(): void;

// ── Host-authoritative helper ─────────────────────────────────────────────────────────────────

/**
 * The model a game supplies to KBAuthority. All members are optional because the required set
 * depends on the mode and the player's role:
 *   • Default (broadcast) mode: implement applyIntent + applyPatch + snapshot() + applySnapshot.
 *   • Per-recipient mode, HOST: implement applyIntent + snapshot(forPlayerId).
 *   • Per-recipient mode, GUEST: no methods are used — pass `{}` and render `currentView`.
 */
export interface KBModel<TView = any, TPatch = any> {
  /** Host only. Mutate state; return a patch to broadcast, or null to reject/no-op. (Unused on per-recipient guests.) */
  applyIntent?(fromId: string, action: any): TPatch | null;
  /** Every client applies a broadcast delta. (Unused in per-recipient mode.) */
  applyPatch?(patch: TPatch): void;
  /** Full state for sync/join/reconnect. In per-recipient mode, the view projected for forPlayerId. (Unused on per-recipient guests.) */
  snapshot?(forPlayerId?: string): TView;
  /** Every client adopts a full snapshot. (Unused in per-recipient mode.) */
  applySnapshot?(state: TView): void;
}

export interface KBAuthorityEvents {
  /** The local model (or currentView) may have changed — re-render. */
  'state-changed': () => void;
}

export interface KBAuthorityOptions {
  /** Per-player projected snapshots for hidden-information games. Default false. */
  perRecipient?: boolean;
  /**
   * Deep-freeze the replicated render copy this helper hands you (a guest's adopted snapshot/delta
   * and `currentView`) so accidentally mutating it throws. Defaults to the transport's `isLocal`
   * flag — on under the local-testing client (dev), off in production. Set `false` to disable even
   * locally (e.g. a high-frequency game that doesn't want to freeze large per-frame state).
   */
  devChecks?: boolean;
}

/**
 * Optional host-authoritative glue layer on top of a KnockBoxPlugin. Parameterize `TView` with your
 * snapshot/view type to have `currentView` typed as a `DeepReadonly` render copy (mutating it becomes
 * a compile error); leave it as the default `any` to opt out.
 */
export class KBAuthority<TView = any, TPatch = any> {
  constructor(net: KnockBoxPlugin, model: KBModel<TView, TPatch>, options?: KBAuthorityOptions);

  /** In per-recipient mode, the local player's latest projected view (null until first arrives). */
  readonly currentView: DeepReadonly<TView> | null;

  /** Subscribe to 'state-changed' here. */
  events: KBEmitter<KBAuthorityEvents>;

  /** Send a game intent to the host (works the same on host and guests). */
  sendIntent(action: any): void;
  /** Host convenience: open/close the lobby to new players. */
  setOpen(open: boolean): void;
  /** Detach all listeners. */
  destroy(): void;
}
