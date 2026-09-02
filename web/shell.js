// Platform shell — owns the single CONTROL websocket, identity, and the lobby UI. When a game
// starts it requests a lobby-scoped ticket and embeds the game in a cross-origin iframe (the game
// origin). It does NOT bridge gameplay: the game opens its own data websocket via the ticket and
// talks to the server directly. The shell and game are isolated (separate origins) on purpose.
import { LAUNCH_EXIT_MS, LAUNCH_MAX_MS, LAUNCH_MORPH_EASING, LAUNCH_MORPH_MS, LAUNCH_SLOW_MS, PROTOCOL_VERSION, announcementSeverity, announcementText, appendPlayLog, buildGameSrc, buildJoinLink, calculateDragTilt, debounce, dominantColorFromPixels, filterAndSortGames, formatPlayerCapacity, formatTagsTooltip, gameWsEndpoint, launchFlipFrom, launchMessage, normalizeTags, ordinal, parseGameParam, parseJoinParam, parseRgbComponents, partitionPlayLogMetadata, pickContrastText, pickRandomFavicon, reconnectDelay, rosterAdd, rosterRemove, rotationFromMatrix, sanitizeGameOrigin, shouldShowAnnouncement, stepSpring1D } from './kb-core.js';

// ── Identity (client-side) ───────────────────────────────────────────────────
// The server mints the playerId and a signed token on first connect; we persist the TOKEN (not the
// id) in sessionStorage — per-tab, so each tab is a distinct anonymous player — and resend it to
// prove ownership of that id on reconnect. The token never leaves this (shell) origin; games get a
// scoped ticket instead. (No login by design.)
//
// The display NAME, by contrast, lives in localStorage so it survives closing the browser — a
// returning player doesn't retype it. It is read EXACTLY ONCE into the in-memory `displayName` below
// and thereafter owned by this tab: we write on change but never re-read and never listen for the
// cross-tab `storage` event. That isolation is deliberate — with a host tab (screen-share) and a
// player tab open in the same browser they share the one localStorage key, so reacting to each
// other's writes would flip a tab's name out from under the user. Last writer wins only for the
// NEXT fresh load; live tabs keep whatever name they were given.
//
// Unlike a server browser, joining here is BY CODE ONLY — there is no lobby-listing endpoint, so a
// private lobby is discoverable only to players who were given its code.

let playerId = null;                                  // assigned by the server (Welcome)
let token = sessionStorage.getItem('kb.token');       // signed identity token (anti-spoof), per-tab
let displayName = localStorage.getItem('kb.displayName') || '';   // read once; empty until named
let gameOrigin = null;                                // where game iframes/sockets live (set by Welcome)

// Auto-join (test convenience): a tab opened via middle-click on the room-code button carries
// "?join=CODE". Such a tab must act as a DISTINCT player — but window.open copies the opener's
// sessionStorage, so it would inherit the opener's identity token (and saved lobby). Clear them so
// this tab gets a fresh server-minted identity; we join the code once connected (see Welcome).
let pendingJoinCode = parseJoinParam(location.search);
// A staged game's direct link ("?game=<id>"). Staged games are withheld from the catalog, so this id is
// also what ListGames must be told to re-admit — otherwise the shell's own launch allowlist, built from
// that catalog, rejects the EnterGame it asked for. Read before the URL is tidied below.
//
// Unlike ?join=, this does NOT reset identity: that reset exists because a middle-clicked join tab
// inherits the opener's sessionStorage and has to become a distinct player. A staged link is an ordinary
// navigation, and clearing the token here would sign the operator out of the session they were in.
const stagedGameId = parseGameParam(location.search);
if (pendingJoinCode) {
  sessionStorage.removeItem('kb.token');
  sessionStorage.removeItem('kb.lobbyId');
  token = null;
}
if (pendingJoinCode || stagedGameId) {
  history.replaceState(null, '', location.pathname); // tidy URL so a refresh won't re-trigger
}

const el = (id) => document.getElementById(id);

// Current session state.
let ws = null;
let reconnectAttempt = 0;       // 0-based; drives exponential backoff, reset once a session is confirmed
let games = new Map();          // gameId -> manifest
let gamesLoaded = false;        // has a ListGames reply landed? distinguishes "empty" from "not yet"
let searchQuery = '';           // search text filter (name/tags)
let playerFilter = '';          // player count filter (e.g. "4", "9+")
let sortOption = 'newest';      // 'newest' | 'updated' | 'alphabetical'
let lobby = null;               // { lobbyId, gameId, hostId, players: [] } once in a game
// The operator's live announcement, or null. Server-owned: pushed on connect and whenever it changes,
// never persisted here — only the id of the one this browser dismissed is.
let announcement = null;
const pending = new Map();      // cid -> resolver
let cidSeq = 0;

// ── WebSocket plumbing (control plane) ────────────────────────────────────────
export function connect() {
  const proto = location.protocol === 'https:' ? 'wss' : 'ws';
  ws = new WebSocket(`${proto}://${location.host}/ws`);

  ws.onopen = () => {
    el('conn').textContent = 'online';
    // Hello carries the current name (restored from sessionStorage or just typed), so the server
    // is in sync from the first frame and after any reconnect.
    send({ type: 'Hello', displayName, token, proto: PROTOCOL_VERSION });
  };
  ws.onclose = () => {
    // Back off exponentially (matching the SDK's data socket) so a server restart doesn't get
    // hammered at 1 Hz by every connected browser. Reset on a confirmed session (Welcome).
    el('conn').textContent = 'offline — reconnecting…';
    setTimeout(connect, reconnectDelay(reconnectAttempt++));
  };
  ws.onmessage = (e) => {
    // The server is the only sender, so a bad frame is unexpected — but it must not throw uncaught
    // out of the handler and silently wedge control-plane dispatch. Log and drop it.
    try { handle(JSON.parse(e.data)); }
    catch (err) { console.error('[KnockBox shell] discarding unparseable frame:', err); }
  };
}

function send(msg) { ws.send(JSON.stringify(msg)); }

// Send a cid-correlated request and await the matching reply.
function request(type, extra = {}) {
  const cid = 'c' + (++cidSeq);
  return new Promise((resolve) => {
    pending.set(cid, resolve);
    send({ type, cid, ...extra });
  });
}

// Re-announce the chosen display name without cycling the socket. The server binds the name at Hello
// but honours SetName afterwards; sent on rename and just before create/join (WS preserves order, so
// the server applies it before the CreateLobby/JoinLobby that follows).
function sendName() {
  if (ws && ws.readyState === WebSocket.OPEN && displayName.trim()) {
    send({ type: 'SetName', displayName });
  }
}

// The name box fires `input` per keystroke; sending a SetName each time trips the control-plane
// rate limit (5/sec) when typing fast. Debounce the network send so a burst collapses into one
// frame — local UI (gate, localStorage) still updates immediately on every keystroke.
const sendNameDebounced = debounce(sendName, 250);

// Search re-renders the whole grid, so it is debounced for the same reason the name send is: a
// keystroke burst must collapse into one pass. Short enough to feel immediate, long enough that no
// intermediate query ever paints. See the search input's wiring below.
const renderGamesDebounced = debounce(renderGames, 120);

export function handle(msg) {
  // Resolve any awaiting request first.
  if (msg.cid && pending.has(msg.cid)) {
    pending.get(msg.cid)(msg);
    pending.delete(msg.cid);
    // fall through: some replies (Joined) also drive UI below
  }

  switch (msg.type) {
    case 'Welcome':
      playerId = msg.playerId;
      token = msg.token;
      sessionStorage.setItem('kb.token', token);
      // Validate the server-supplied origin to an http(s) origin before it can flow into an iframe
      // src; fall back to this origin when missing or invalid.
      gameOrigin = sanitizeGameOrigin(msg.gameOrigin) || location.origin;
      reconnectAttempt = 0; // session confirmed; next drop starts backoff fresh
      // Fresh socket: any abandoned-lobby marker whose EnterGame push never arrived (the drop ate it)
      // would otherwise sit there and swallow one later, legitimate entry into that same lobby.
      abandonedLobbies.clear();
      // Load the game catalog FIRST, then (re)join. An EnterGame — from an auto-join or a rejoin —
      // makes enterGame resolve the manifest from `games`, which must be populated by then. The
      // server replies in order, so an un-gated JoinLobby/Rejoin would land its EnterGame before
      // the ListGames reply and enterGame would reject it as "Unknown game".
      refreshGames().then(() => {
        // A game-scoped announcement arrives immediately after Welcome, i.e. BEFORE this catalog reply,
        // so its first render has no title to label it with. Re-render now that `games` is populated.
        renderAnnouncement();
        // First connect of an auto-join tab joins the URL code; null it after so a later reconnect
        // rejoins the now-saved lobby via tryRejoin() instead of re-running the auto-join.
        if (pendingJoinCode) { const code = pendingJoinCode; pendingJoinCode = null; autoJoin(code); }
        else tryRejoin();
      });
      break;
    case 'PlayerJoined':
      if (lobby && msg.lobbyId === lobby.lobbyId) {
        lobby.players = rosterAdd(lobby.players, msg.player);
      }
      break;
    case 'PlayerLeft':
      if (lobby && msg.lobbyId === lobby.lobbyId) {
        lobby.players = rosterRemove(lobby.players, msg.playerId);
      }
      break;
    case 'PlayerDisconnected':
    case 'PlayerConnected':
      // A member's shell dropped (or returned) within the reconnect grace window. They stay in the
      // roster the whole time, so don't add/remove — a true departure arrives later as PlayerLeft.
      break;
    case 'EnterGame':
      enterGame(msg);
      break;
    case 'Error':
      showError(msg.reason || 'Something went wrong.');
      break;
    case 'Kicked':
      // The host removed us. Leave the game, forget the lobby (so we don't auto-rejoin), and say so.
      console.info('[KnockBox shell] Kicked received for lobby', msg.lobbyId);
      if (!lobby || msg.lobbyId === lobby.lobbyId) {
        sessionStorage.removeItem('kb.lobbyId');
        showLobbyView();
        showError('You were kicked from the lobby.');
      }
      break;
    case 'RejoinRejected':
      sessionStorage.removeItem('kb.lobbyId');
      showLobbyView();
      break;
    case 'LobbyClosed':
      // The server closed a LIVE lobby (today: the game's server-side authority module failed
      // fatally). Same shape as Kicked: leave the game, forget the lobby, explain why.
      console.info('[KnockBox shell] LobbyClosed received for lobby', msg.lobbyId, msg.reason);
      if (!lobby || msg.lobbyId === lobby.lobbyId) {
        sessionStorage.removeItem('kb.lobbyId');
        showLobbyView();
        showError(msg.reason === 'authority-failed'
          ? "The game's server logic failed, so the lobby was closed."
          : 'The lobby was closed.');
      }
      break;
    case 'OwnerChanged':
      // The lobby owner (kick/open-close powers) moved — a server-authority game promoted a
      // successor. The shell renders no owner UI; just keep its lobby record honest.
      if (lobby && msg.lobbyId === lobby.lobbyId) lobby.hostId = msg.ownerId;
      break;
    case 'PlayLog':
      // A game we're playing recorded a Play Log entry; the server already stamped game/time/host
      // and routed it back to us. Persist it (browser-local) and refresh the home-page panel.
      recordPlayLog(msg);
      break;
    case 'AnnouncementPosted':
      // The operator's banner: broadcast when posted, and pushed again to each shell as it connects,
      // so a reload or a late arrival sees the same notice.
      announcement = { id: msg.id, text: msg.text, severity: msg.severity, gameId: msg.gameId };
      renderAnnouncement();
      break;
    case 'AnnouncementCleared':
      // Only if it's the one we're showing: a newer announcement may have arrived between the operator
      // clearing the old one and this frame reaching us.
      if (announcement && announcement.id === msg.id) {
        announcement = null;
        renderAnnouncement();
      }
      break;
  }
}

// ── Operator announcement ───────────────────────────────────────────────────
// The one banner the platform shows players. A fixed toast at the top of the screen: it stays until the
// player dismisses it or the operator takes it down, so "maintenance in 20 minutes" stays readable without
// causing layout shift. Text is untrusted operator input and goes in via textContent; the severity is
// validated to a known value before it becomes a class name.
const ANNOUNCEMENT_DISMISSED_KEY = 'kb.announcementDismissed';

function renderAnnouncement() {
  const banner = el('announcement-banner');
  const dismissed = localStorage.getItem(ANNOUNCEMENT_DISMISSED_KEY);
  if (!shouldShowAnnouncement(announcement, dismissed)) {
    banner.hidden = true;
    return;
  }

  // A game-scoped notice is labelled with the game's title — on the home page it is otherwise
  // indistinguishable from a platform-wide one. An id we don't know (a game since uninstalled) simply
  // shows the text: a raw id means nothing to a player.
  const gameName = announcement.gameId ? games.get(announcement.gameId)?.name : null;
  el('announcement-text').textContent = announcementText(announcement, gameName);
  banner.className = `home-announcement severity-${announcementSeverity(announcement.severity)}`;
  banner.hidden = false;
}

// ── Play Log (home page) ───────────────────────────────────────────────────────
// Games push entries via KnockBox.logPlay(); the server stamps gameId/timestamp/isHost and routes
// them to this player's OWN control socket. We keep the most-recent PLAY_LOG_MAX in localStorage
// (per browser, like the display name) and render them on the home page, newest first. Every
// game-supplied string (metadata keys/values, resolved game name) is untrusted and written via
// textContent — never innerHTML — so a game can't inject markup into the shell.
const PLAYLOG_KEY = 'kb.playLog';

function readPlayLog() {
  try {
    const parsed = JSON.parse(localStorage.getItem(PLAYLOG_KEY) || '[]');
    return Array.isArray(parsed) ? parsed : [];
  } catch { return []; }
}

function recordPlayLog(msg) {
  const entry = {
    gameId: msg.gameId || null,
    timestamp: msg.timestamp || null,
    isHost: !!msg.isHost,
    metadata: msg.metadata && typeof msg.metadata === 'object' ? msg.metadata : {},
  };
  const next = appendPlayLog(readPlayLog(), entry);
  try { localStorage.setItem(PLAYLOG_KEY, JSON.stringify(next)); } catch { /* storage full/blocked — skip */ }
  renderPlayLog(); // the panel is hidden while in-game; re-rendering it then is harmless
}

function clearPlayLog() {
  try { localStorage.removeItem(PLAYLOG_KEY); } catch { /* storage blocked — ignore */ }
  renderPlayLog();
}

function plChip(text, className) {
  const span = document.createElement('span');
  span.className = className ? `pl-chip ${className}` : 'pl-chip';
  span.textContent = text;
  return span;
}

// Render the stored UTC timestamp in the player's locale, keeping the ISO instant in the <time>
// element's datetime/title for the exact value. Returns null for a missing/unparseable stamp.
function playLogTime(iso) {
  if (!iso) return null;
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return null;
  const t = document.createElement('time');
  t.className = 'pl-item-time';
  t.dateTime = iso;
  t.title = iso;
  // dateStyle/timeStyle are supported on all current engines; the fallback covers very old ones
  // (and any locale data gap) so a play-log row never renders blank or throws.
  try { t.textContent = d.toLocaleString([], { dateStyle: 'medium', timeStyle: 'short' }); }
  catch { t.textContent = d.toLocaleString(); }
  return t;
}

function playLogItem(entry) {
  const li = document.createElement('li');
  li.className = 'pl-item';

  const head = document.createElement('div');
  head.className = 'pl-item-head';
  const name = document.createElement('span');
  name.className = 'pl-item-game';
  const manifest = entry.gameId ? games.get(entry.gameId) : null;
  name.textContent = manifest ? manifest.name : (entry.gameId || 'Unknown game');
  head.appendChild(name);
  const time = playLogTime(entry.timestamp);
  if (time) head.appendChild(time);
  li.appendChild(head);

  // Recognized standard keys become dedicated chips; everything else drops to the details table.
  const { standard, extra } = partitionPlayLogMetadata(entry.metadata);
  const chips = document.createElement('div');
  chips.className = 'pl-chips';
  if (entry.isHost) chips.appendChild(plChip('Host', 'pl-chip-host'));
  for (const [key, value] of standard) {
    if (key === 'placement') chips.appendChild(plChip(ordinal(value), 'pl-chip-placement'));
    else if (key === 'playerCount') chips.appendChild(plChip(`${value} player${value === '1' ? '' : 's'}`, 'pl-chip-players'));
    else chips.appendChild(plChip(`${key}: ${value}`));
  }
  if (chips.childElementCount) li.appendChild(chips);

  if (extra.length) {
    const details = document.createElement('details');
    details.className = 'pl-details';
    const summary = document.createElement('summary');
    summary.textContent = `Details (${extra.length})`;
    details.appendChild(summary);
    const table = document.createElement('table');
    table.className = 'pl-meta-table';
    for (const [key, value] of extra) {
      const tr = document.createElement('tr');
      const th = document.createElement('th');
      th.scope = 'row';
      th.textContent = key;
      const td = document.createElement('td');
      td.textContent = value;
      tr.append(th, td);
      table.appendChild(tr);
    }
    details.appendChild(table);
    li.appendChild(details);
  }
  return li;
}

export function renderPlayLog() {
  const list = el('playlog-list');
  const empty = el('playlog-empty');
  if (!list || !empty) return; // panel markup not present (some test fixtures)
  const entries = readPlayLog();
  list.innerHTML = '';
  const hasEntries = entries.length > 0;
  empty.hidden = hasEntries;
  list.hidden = !hasEntries;
  const clearBtn = el('playlog-clear');
  if (clearBtn) clearBtn.hidden = !hasEntries;
  for (const entry of entries) list.appendChild(playLogItem(entry));
}

// ── Home view: name gate, game tiles (host), join-by-code ─────────────────────

// The player must name themselves before hosting or joining (the old CanJoinOrCreate gate).
function applyGate() {
  const ok = !!displayName.trim();
  el('join-btn').disabled = !ok;
  document.querySelectorAll('#games .game-tile').forEach((b) => { b.disabled = !ok; });
}

export async function refreshGames() {
  // `include` asks the server to re-admit one staged game — the one this tab was linked to. Omitted
  // (undefined, so it isn't serialized) on a normal load, which is every load but a staged link.
  const reply = await request('ListGames', stagedGameId ? { include: stagedGameId } : {});
  games = new Map((reply.games || []).map((g) => [g.id, g]));
  gamesLoaded = true;
  renderGames();
}

export function renderGames() {
  const host = el('games');
  if (!host) return;
  host.innerHTML = '';
  if (games.size === 0) {
    // "Drop one in /games" is a DEPLOYMENT diagnostic, and this path is reachable before the ListGames
    // reply lands or while the socket is reconnecting — a search keystroke during connect used to
    // render it. Only claim the server has no games once one reply has actually said so.
    const empty = gamesLoaded ? 'No games discovered. Drop one in /games.' : 'Loading games…';
    host.innerHTML = `<p class="games-empty">${empty}</p>`;
    setGamesStatus(gamesLoaded ? empty : '');
    return;
  }

  const visibleGames = filterAndSortGames([...games.values()], {
    search: searchQuery,
    playerCount: playerFilter,
    sort: sortOption,
  });

  if (visibleGames.length === 0) {
    host.innerHTML = '<p class="games-empty">No games match your search.</p>';
    setGamesStatus('No games match your search.');
    return;
  }
  setGamesStatus(`${visibleGames.length} game${visibleGames.length === 1 ? '' : 's'} shown`);

  for (const g of visibleGames) {
    const btn = document.createElement('button');
    btn.className = 'game-tile';
    btn.type = 'button';
    btn.setAttribute('aria-label', g.name);
    // The staged game this tab was linked to: present in the grid only because we asked for it, and
    // marked so whoever is testing it can tell it apart from the public tiles. No auto-launch — it goes
    // through the same click, the same name gate and the same launch overlay as everything else.
    if (stagedGameId && g.id === stagedGameId) {
      btn.classList.add('is-staged');
      btn.title = `${g.name} — unlisted (staged) game`;
    }

    const surface = document.createElement('div');
    surface.className = 'game-tile-surface';

    const art = document.createElement('div');
    art.className = 'game-tile-art';

    if (g.thumbnail) {
      const img = document.createElement('img');
      img.className = 'game-tile-img';
      img.src = `/games/${g.id}/${g.thumbnail}`;
      img.alt = '';
      img.draggable = false;
      img.loading = 'lazy';
      // No tile art → fall back to the hot-pink "needs art" surface showing the name.
      img.onerror = () => { img.replaceWith(fallbackSurface(g.name)); };
      art.appendChild(img);
    } else {
      art.appendChild(fallbackSurface(g.name));
    }
    surface.appendChild(art);

    // Chin bar: player capacity + overflowing tag chips with tooltip
    const chin = document.createElement('div');
    chin.className = 'game-tile-chin';

    const cap = document.createElement('span');
    cap.className = 'game-chin-capacity';
    const capIcon = document.createElement('span');
    capIcon.className = 'game-chin-icon';
    capIcon.setAttribute('aria-hidden', 'true');
    capIcon.innerHTML = '<svg viewBox="0 0 24 24" width="13" height="13" fill="currentColor" aria-hidden="true"><path d="M16 11c1.66 0 2.99-1.34 2.99-3S17.66 5 16 5c-1.66 0-3 1.34-3 3s1.34 3 3 3zm-8 0c1.66 0 2.99-1.34 2.99-3S9.66 5 8 5C6.34 5 5 6.34 5 8s1.34 3 3 3zm0 2c-2.33 0-7 1.17-7 3.5V19h14v-2.5c0-2.33-4.67-3.5-7-3.5zm8 0c-.29 0-.62.02-.97.05 1.16.84 1.97 1.97 1.97 3.45V19h6v-2.5c0-2.33-4.67-3.5-7-3.5z"/></svg>';
    const capText = document.createElement('span');
    capText.className = 'game-chin-capacity-text';
    capText.textContent = `${formatPlayerCapacity(g.minPlayers, g.maxPlayers)}`;
    cap.append(capIcon, capText);
    chin.appendChild(cap);

    // Normalized once, so the chips, the tooltip and the search all agree about what a tag is —
    // nothing validates `tags` server-side, and a raw iteration renders a bordered chip for `""`.
    const tags = normalizeTags(g.tags);
    if (tags.length > 0) {
      const tagsEl = document.createElement('span');
      tagsEl.className = 'game-chin-tags';
      const tooltip = formatTagsTooltip(tags);
      if (tooltip) tagsEl.title = tooltip;
      for (const tag of tags) {
        const tagChip = document.createElement('span');
        tagChip.className = 'game-chin-tag';
        tagChip.textContent = tag;
        tagsEl.appendChild(tagChip);
      }
      chin.appendChild(tagsEl);
    }

    surface.appendChild(chin);
    btn.appendChild(surface);

    // The button is handed along so the launch overlay can fly this very tile to the centre.
    btn.onclick = () => createLobby(g.id, btn);
    host.appendChild(btn);
  }
  applyGate();
}

// The grid's screen-reader narration. A live region on #games itself would re-announce every tile on
// every render (and renderGames rebuilds all of them), so the count lives in its own .sr-only region:
// typing in the search box then reports what changed, which is the only feedback a non-sighted user
// gets that the result set moved at all.
function setGamesStatus(text) {
  const status = el('games-status');
  if (status) status.textContent = text;
}

function fallbackSurface(name) {
  const div = document.createElement('div');
  div.className = 'game-tile-fallback';
  div.textContent = name;
  return div;
}

export async function createLobby(gameId, sourceEl) {
  if (!displayName.trim()) { showError('Please enter a name to start playing!'); return; }
  sendNameDebounced.cancel();   // we send immediately below; drop any pending trailing send
  sendName();
  // Up before the first round trip — the name is known synchronously here, so the overlay is
  // correct from the very first frame after the click, and sourceEl's rect is still the one the
  // player just clicked (measure now, before the home view starts fading).
  const clicked = games.get(gameId);
  showLaunchOverlay(clicked && clicked.name, launchArtUrl(clicked), sourceEl);
  const abortAt = launchAbortSeq;
  const reply = await request('CreateLobby', { gameId });
  if (abortAt !== launchAbortSeq) {
    // Backed out while the server was replying. Don't drag them in — and don't strand the lobby
    // the server went ahead and made either. The EnterGame push that follows the reply is already
    // in flight, so mark the lobby abandoned to stand that down too.
    if (reply.type === 'LobbyCreated') {
      abandonedLobbies.add(reply.lobbyId);
      send({ type: 'LeaveLobby', lobbyId: reply.lobbyId });
    }
    return;
  }
  if (reply.type === 'LobbyCreated') {
    sessionStorage.setItem('kb.lobbyId', reply.lobbyId);
    lobby = { lobbyId: reply.lobbyId, gameId, hostId: playerId, players: [{ id: playerId, displayName }] };
    showRoom();
  } else {
    showError(reply.reason || 'Could not create lobby.');
  }
}

export async function joinByCode() {
  const code = (el('room-code-input').value || '').trim().toUpperCase();
  if (!displayName.trim()) { showError('Please enter a name to start playing!'); return; }
  if (!code) { showError('Please enter a valid room code.'); return; }
  sendNameDebounced.cancel();   // we send immediately below; drop any pending trailing send
  sendName();
  // Track the target lobby so any PlayerJoined push that races ahead of EnterGame attaches, but
  // DON'T switch to the game view yet — a wrong code must not flash the waiting screen. On success
  // we show the room; the EnterGame that follows swaps in the iframe (it lands after this reply's
  // continuation, so it never clobbers showRoom).
  lobby = { lobbyId: code, gameId: null, hostId: null, players: [{ id: playerId, displayName }] };
  // Which game this code belongs to is unknown until EnterGame, so start on the generic label; the
  // overlay is neutral, so a wrong code still doesn't flash the game view.
  showLaunchOverlay(null);
  const abortAt = launchAbortSeq;
  const reply = await request('JoinLobby', { lobbyId: code });
  if (abortAt !== launchAbortSeq) {
    if (reply.type === 'LobbyJoined') {
      abandonedLobbies.add(reply.lobbyId);   // stand down the EnterGame push already behind this reply
      send({ type: 'LeaveLobby', lobbyId: reply.lobbyId });
    }
    lobby = null;
    return;
  }
  if (reply.type === 'LobbyJoined') {
    sessionStorage.setItem('kb.lobbyId', reply.lobbyId);
    showRoom();
  } else {
    lobby = null;
    showError(reply.reason || 'Could not join lobby.');
  }
}

export function tryRejoin() {
  const saved = sessionStorage.getItem('kb.lobbyId');
  if (saved) request('RejoinLobby', { lobbyId: saved });
}

// Auto-join the lobby a middle-click "test player" tab was opened for, reusing the normal join path.
function autoJoin(code) {
  // Keep the player's saved name; only invent one when none is set, and never persist it (no
  // localStorage write) so it stays a throwaway. The server makes the name unique within the lobby,
  // so a test tab sharing the opener's name shows up as "Name (2)".
  if (!displayName.trim()) displayName = `Tester ${1000 + Math.floor(Math.random() * 9000)}`;
  el('player-name-input').value = displayName;
  el('room-code-input').value = code;
  joinByCode();
}

// ── Waiting room (shown on create/join, before the game starts) ───────────────
function setDocumentTitle(gameName) {
  document.title = gameName ? `KnockBox Games - ${gameName}` : 'KnockBox Games';
}

export function showRoom() {
  const manifest = lobby.gameId ? games.get(lobby.gameId) : null;
  const displayName = manifest ? manifest.name : (lobby.gameId || `Lobby ${lobby.lobbyId}`);
  el('game-title').textContent = displayName;
  setDocumentTitle(displayName);
  el('lobby-code').textContent = lobby.lobbyId;
  el('frame-host').innerHTML = ''; // no iframe until EnterGame
  themeHeader(manifest);
  revealGameView();
}

// ── In-game: embed the game on its own origin and hand it a scoped ticket ─────
export async function enterGame(starting) {
  // A push for a launch the player deliberately backed out of (the reply's abort branch already sent
  // LeaveLobby). Drop it here, before anything below can raise the overlay or rewrite `lobby` —
  // enterGame's own launchAbortSeq check comes too late to stop that.
  if (abandonedLobbies.delete(starting.lobbyId)) return;
  // Only launch games discovered in our catalog (the allowlist refreshGames built). This rejects a
  // EnterGame for an unknown id instead of feeding a server-supplied id straight into the iframe URL.
  const manifest = games.get(starting.gameId);
  if (!manifest) { showError('Unknown game.'); return; }
  // Usually an overlay is already up from the click; name it now that we know the game. When it
  // isn't, this is a rejoin (tryRejoin ignores its reply — the view switch happens only here) or a
  // reconnect, both of which rebuild the iframe and so deserve the same cover.
  if (launchOverlayUp()) setLaunchName(manifest.name, launchArtUrl(manifest));
  else showLaunchOverlay(manifest.name, launchArtUrl(manifest));
  lobby = {
    lobbyId: starting.lobbyId,
    gameId: starting.gameId,
    hostId: starting.hostId,
    players: starting.players.slice(),
  };

  el('game-title').textContent = manifest.name;
  setDocumentTitle(manifest.name);
  el('lobby-code').textContent = starting.lobbyId;
  themeHeader(manifest);

  // Lobby-scoped credential for the game's own data socket. The game never sees our identity token.
  const abortAt = launchAbortSeq;
  const reply = await request('RequestTicket', { lobbyId: starting.lobbyId });
  if (abortAt !== launchAbortSeq) return;   // gave up on the launch while the ticket was in flight
  if (reply.type !== 'Ticket') { showError(reply.reason || 'Could not start game.'); return; }

  const entry = manifest.entry;
  // Credentials go in the URL fragment (not the query string) so they never leak via Referer/logs.
  let src;
  try {
    src = buildGameSrc(
      gameOrigin,
      starting.gameId,
      entry,
      reply.ticket,
      gameWsEndpoint(gameOrigin),
      manifest?.version,
      manifest?.updatedAt || manifest?.createdAt,
    );
  } catch {
    // gameOrigin is sanitized at the source (Welcome), so this is defensive: surface it like every
    // other enterGame failure rather than letting the rejection escape.
    showError('Could not start game.');
    return;
  }

  el('frame-host').innerHTML = '';
  const frame = document.createElement('iframe');
  // The one cross-origin signal we get that the game's document and its subresources are in. Listen
  // before setting src, and guard on the launch sequence so a late `load` from a frame we've already
  // torn down can't clear a newer overlay.
  const seq = launchSeq;
  // A real game on the other side, so this is the ending that hands the tile over to it.
  frame.addEventListener('load', () => { if (seq === launchSeq) hideLaunchOverlay(true); }, { once: true });
  frame.src = src;
  frame.id = 'game-frame';
  if (manifest && manifest.crossOriginIsolated) frame.allow = 'cross-origin-isolated';
  el('frame-host').appendChild(frame);

  // The iframe is in the DOM with its src set, so the download is already running; the reveal itself
  // can wait for the scrim (see revealGameView).
  revealGameView();
}

export function showLobbyView() {
  lobby = null;
  // Every way out of a session lands here (Leave, Kicked, RejoinRejected, session-ended, and the
  // overlay's own escape hatch), so this one call retires any launch still in flight.
  abortLaunch();
  clearGameMorph();   // leaving mid-expand must not strand a transform on the game view
  closeCodeModal();
  resetHeaderTheme();
  setDocumentTitle(null);
  el('frame-host').innerHTML = '';
  document.body.classList.remove('in-game');
  el('game-view').style.display = 'none';
  el('lobby-view').style.display = 'block';
  // Belt and braces with the overlay teardown: if is-launching survived, the home page we just
  // switched back to would render at opacity 0 and the app would look dead.
  clearLaunchingClass();
  renderPlayLog();
}

// ── Per-game header tint ──────────────────────────────────────────────────────
// Make the in-game chrome feel like part of the game: derive a header background from the game
// (an explicit manifest themeColor, else the dominant color of its thumbnail) and a contrasting
// text color (explicit themeTextColor, else auto black/white). Falls back to the default white
// header when nothing resolves. All author-supplied colors are validated before use.
let themeSeq = 0;

export async function themeHeader(manifest) {
  const seq = ++themeSeq;
  let bg = manifest && manifest.themeColor ? colorToRgb(manifest.themeColor) : null;
  if (!bg && manifest && manifest.thumbnail) {
    // The thumbnail is served same-origin (shell origin gates /games/* to it), so we can read its
    // pixels off a canvas without a CORS taint. A plain await keeps enterGame's flow simple.
    bg = await dominantColorFromImage(`/games/${manifest.id}/${manifest.thumbnail}`);
    if (seq !== themeSeq) return; // left the game (or switched) while sampling — drop this result
  }
  if (!bg) { resetHeaderTheme(); return; }

  let fg = manifest && manifest.themeTextColor ? colorToRgb(manifest.themeTextColor) : null;
  if (!fg) fg = pickContrastText(bg);
  applyHeaderColors(bg, fg);
}

export function applyHeaderColors(bg, fg) {
  const h = document.querySelector('.game-header');
  if (!h) return;
  const rgb = (c) => `rgb(${c.r}, ${c.g}, ${c.b})`;
  const rgba = (c, a) => `rgba(${c.r}, ${c.g}, ${c.b}, ${a})`;
  h.style.setProperty('--gh-bg', rgb(bg));
  h.style.setProperty('--gh-fg', rgb(fg));
  h.style.setProperty('--gh-fg-muted', rgba(fg, 0.65));
  h.style.setProperty('--gh-btn-bg', rgba(fg, 0.14));
  h.style.setProperty('--gh-btn-bg-hover', rgba(fg, 0.26));
}

export function resetHeaderTheme() {
  themeSeq++; // cancel any in-flight thumbnail sampling
  const h = document.querySelector('.game-header');
  if (!h) return;
  for (const p of ['--gh-bg', '--gh-fg', '--gh-fg-muted', '--gh-btn-bg', '--gh-btn-bg-hover']) {
    h.style.removeProperty(p);
  }
}

// Validate an author-supplied CSS color via the CSSOM (invalid values are rejected, never injected),
// returning normalized {r,g,b} or null. Non-opaque values (e.g. `transparent`, which normalizes to
// rgba(0,0,0,0)) are rejected too, so theming falls back to thumbnail sampling / the default header
// instead of painting a wrong (black/translucent) tint.
export function colorToRgb(value) {
  if (typeof value !== 'string' || !value) return null;
  const probe = document.createElement('span');
  probe.style.color = value; // CSSOM ignores anything that isn't a valid single color
  if (!probe.style.color) return null;
  probe.style.display = 'none';
  document.body.appendChild(probe);
  const norm = getComputedStyle(probe).color; // always rgb()/rgba()
  probe.remove();
  return parseRgbComponents(norm); // numeric parse + opaque check (pure, in kb-core)
}

// Draw the thumbnail small, hand its pixels to the pure bucketing helper, and resolve the dominant
// color (see dominantColorFromPixels in kb-core). Resolves null on any failure.
export function dominantColorFromImage(url) {
  return new Promise((resolve) => {
    const img = new Image();
    img.onload = () => {
      try {
        const w = 48, h = 48;
        const canvas = document.createElement('canvas');
        canvas.width = w; canvas.height = h;
        const ctx = canvas.getContext('2d', { willReadFrequently: true });
        ctx.drawImage(img, 0, 0, w, h);
        const { data } = ctx.getImageData(0, 0, w, h);
        resolve(dominantColorFromPixels(data));
      } catch {
        resolve(null); // tainted canvas / decode failure — fall back to default header
      }
    };
    img.onerror = () => resolve(null);
    img.src = url;
  });
}

// ── Game launch overlay ───────────────────────────────────────────────────────
// Clicking a tile used to do nothing visible until the game painted: two socket round trips, then a
// cold HTML/JS/WASM download that can run for seconds. This covers that whole gap. It is dismissed
// on the game iframe's `load` event — the only cross-origin signal we get — with a hard ceiling so a
// missed event can't strand a running game behind it. `launchSeq` invalidates the timers and the
// `load` handler of a launch we've already moved on from (same trick as themeSeq).
//
// Presentation: the clicked tile itself flies to the centre (a FLIP against its grid rect) while the
// home view dissolves, so nothing large ever arrives — a launch that resolves in 150ms reads as a
// lift, not a flash. See the .launch-* block in home.css; the LAUNCH_*_MS constants mirror it.
let launchSeq = 0;
let launchTimers = [];
// Bumped only when the player DELIBERATELY abandons a launch (the escape hatch, Leave, a kick).
// Separate from launchSeq because that also moves on a routine dismissal — a launch whose overlay
// timed out must still finish wiring up the game, whereas an abandoned one must not.
let launchAbortSeq = 0;
// The server answers a create/join we've already backed out of: the reply, and right behind it an
// EnterGame push for the lobby the abort branch just sent LeaveLobby for. Record that lobby so the
// push is dropped instead of pulling the player into the session they cancelled — enterGame raises
// the overlay and rewrites `lobby` before it ever reaches its own launchAbortSeq check. Consumed on
// the first matching push (the server sends exactly one per entry), so a later, genuine join of the
// same code still enters.
const abandonedLobbies = new Set();
// The tile we borrowed from the grid, so its visibility is restored when the launch ends, and the
// running expand-to-fullscreen animation (see startGameMorph).
let launchSource = null;
let gameMorph = null;
let morphTimer = null;

// ── Interactive hero tile dragging & momentum physics ─────────────────────────
// Users can grab and drag the hero tile around while waiting for a game to launch.
// As it moves, the tile exhibits momentum (following with smooth spring inertia,
// dynamic velocity-based tilt, and a slight lift). On release, it rubber-bands
// back to center with underdamped spring physics.
let dragActive = false;
let dragPointerId = null;
let dragStartPointerX = 0;
let dragStartPointerY = 0;
let dragStartTileX = 0;
let dragStartTileY = 0;
let dragPosX = 0;
let dragPosY = 0;
let dragVelX = 0;
let dragVelY = 0;
let dragTargetX = 0;
let dragTargetY = 0;
let dragRot = 0;
let dragVelRot = 0;
let dragScale = 1;
let dragRaf = null;
let dragLastTime = 0;
let dragListenersInstalled = false;

function initHeroTileDrag() {
  const tile = el('launch-tile');
  if (!tile || dragListenersInstalled) return;
  dragListenersInstalled = true;

  tile.addEventListener('pointerdown', onHeroPointerDown);
  tile.addEventListener('pointermove', onHeroPointerMove);
  tile.addEventListener('pointerup', onHeroPointerUp);
  tile.addEventListener('pointercancel', onHeroPointerUp);
  tile.addEventListener('lostpointercapture', onHeroPointerUp);

  if (typeof window !== 'undefined') {
    window.addEventListener('blur', () => {
      if (dragActive) resetHeroDragState(true);
    });
  }
  if (typeof document !== 'undefined') {
    document.addEventListener('visibilitychange', () => {
      if (document.hidden && dragActive) resetHeroDragState(true);
    });
  }
}

function onHeroPointerDown(e) {
  if (e.button !== 0) return;
  const tile = el('launch-tile');
  if (!tile || tile.hidden) return;

  try {
    if (typeof tile.setPointerCapture === 'function') {
      tile.setPointerCapture(e.pointerId);
    }
  } catch (_) {}

  dragActive = true;
  dragPointerId = e.pointerId;
  tile.classList.add('is-dragging');
  tile.classList.remove('is-popping', 'no-transition', 'is-dancing');
  tile.style.transition = 'none';

  dragStartPointerX = e.clientX;
  dragStartPointerY = e.clientY;
  dragStartTileX = dragPosX;
  dragStartTileY = dragPosY;
  dragTargetX = dragPosX;
  dragTargetY = dragPosY;

  dragLastTime = typeof performance !== 'undefined' ? performance.now() : Date.now();
  if (!dragRaf) {
    dragRaf = requestAnimationFrame(updateHeroDragPhysics);
  }
}

function onHeroPointerMove(e) {
  if (!dragActive || (dragPointerId !== null && e.pointerId !== dragPointerId)) return;
  const dx = e.clientX - dragStartPointerX;
  const dy = e.clientY - dragStartPointerY;
  dragTargetX = dragStartTileX + dx;
  dragTargetY = dragStartTileY + dy;
}

function onHeroPointerUp(e) {
  if (!dragActive || (dragPointerId !== null && e.pointerId !== dragPointerId)) return;
  const tile = el('launch-tile');
  if (tile) {
    try {
      if (typeof tile.hasPointerCapture === 'function' && tile.hasPointerCapture(e.pointerId)) {
        tile.releasePointerCapture(e.pointerId);
      }
    } catch (_) {}
    tile.classList.remove('is-dragging');
  }
  dragActive = false;
  dragPointerId = null;
  dragTargetX = 0;
  dragTargetY = 0;

  if (prefersReducedMotion()) {
    resetHeroDragState(true);
    return;
  }

  if (!dragRaf) {
    dragLastTime = typeof performance !== 'undefined' ? performance.now() : Date.now();
    dragRaf = requestAnimationFrame(updateHeroDragPhysics);
  }
}

function updateHeroDragPhysics(currentTime) {
  dragRaf = null;
  const tile = el('launch-tile');
  if (!tile || tile.hidden) {
    resetHeroDragState(false);
    return;
  }

  const now = currentTime || (typeof performance !== 'undefined' ? performance.now() : Date.now());
  const dt = Math.min(Math.max((now - dragLastTime) / 1000, 0.001), 0.05);
  dragLastTime = now;

  if (dragActive) {
    // Follow pointer with momentum and perceptible mass
    const stepX = stepSpring1D(dragPosX, dragTargetX, dragVelX, { stiffness: 380, damping: 28 }, dt);
    dragPosX = stepX.pos;
    dragVelX = stepX.vel;

    const stepY = stepSpring1D(dragPosY, dragTargetY, dragVelY, { stiffness: 380, damping: 28 }, dt);
    dragPosY = stepY.pos;
    dragVelY = stepY.vel;

    // Dynamic tilt based on velocity and displacement
    const targetRot = calculateDragTilt(dragVelX, dragPosX, 15);
    const stepRot = stepSpring1D(dragRot, targetRot, dragVelRot, { stiffness: 300, damping: 24 }, dt);
    dragRot = stepRot.pos;
    dragVelRot = stepRot.vel;

    // Subtle lift scale
    const stepScale = stepSpring1D(dragScale, 1.04, 0, { stiffness: 200, damping: 20 }, dt);
    dragScale = stepScale.pos;
  } else {
    // Rubber-band return to center (0, 0)
    const stepX = stepSpring1D(dragPosX, 0, dragVelX, { stiffness: 220, damping: 20 }, dt);
    dragPosX = stepX.pos;
    dragVelX = stepX.vel;

    const stepY = stepSpring1D(dragPosY, 0, dragVelY, { stiffness: 220, damping: 20 }, dt);
    dragPosY = stepY.pos;
    dragVelY = stepY.vel;

    // Return rotation to 0
    const stepRot = stepSpring1D(dragRot, 0, dragVelRot, { stiffness: 180, damping: 18 }, dt);
    dragRot = stepRot.pos;
    dragVelRot = stepRot.vel;

    // Return scale to 1
    const stepScale = stepSpring1D(dragScale, 1, 0, { stiffness: 200, damping: 20 }, dt);
    dragScale = stepScale.pos;

    // Settling condition: snap cleanly when motion is negligible
    if (
      Math.hypot(dragPosX, dragPosY) < 0.2 &&
      Math.hypot(dragVelX, dragVelY) < 0.2 &&
      Math.abs(dragRot) < 0.1 &&
      Math.abs(dragScale - 1) < 0.005
    ) {
      resetHeroDragState(true);
      return;
    }
  }

  tile.style.transform = `translate(${dragPosX.toFixed(2)}px, ${dragPosY.toFixed(2)}px) rotate(${dragRot.toFixed(2)}deg) scale(${dragScale.toFixed(3)})`;

  dragRaf = requestAnimationFrame(updateHeroDragPhysics);
}

function resetHeroDragState(resumeDance = false) {
  if (dragRaf) {
    cancelAnimationFrame(dragRaf);
    dragRaf = null;
  }
  dragActive = false;
  dragPointerId = null;
  dragPosX = 0;
  dragPosY = 0;
  dragVelX = 0;
  dragVelY = 0;
  dragTargetX = 0;
  dragTargetY = 0;
  dragRot = 0;
  dragVelRot = 0;
  dragScale = 1;

  const tile = el('launch-tile');
  if (tile) {
    tile.classList.remove('is-dragging');
    tile.style.transition = '';
    tile.style.transform = '';
    if (resumeDance && launchOverlayUp() && !tile.hidden) {
      tile.classList.add('is-dancing');
    } else if (!resumeDance) {
      tile.classList.remove('is-dancing');
    }
  }
}

function clearLaunchTimers() {
  for (const t of launchTimers) clearTimeout(t);
  launchTimers = [];
}

// True while a launch is on screen AND still current. Excludes an overlay mid-exit, so a late reply
// can't relabel a launch on its way out. Also the "markup is present" check — showError calls into
// here, and it runs against fixtures that may not carry the overlay.
export function launchOverlayUp() {
  const overlay = el('launch-overlay');
  return !!overlay && !overlay.hidden && !overlay.classList.contains('is-leaving');
}

// `sourceEl` is the clicked grid tile, when there is one. Without it (join-by-code, a rejoin, a
// reconnect) the launch tile arrives in place instead of flying.
export function showLaunchOverlay(gameName, artUrl, sourceEl) {
  const overlay = el('launch-overlay');
  if (!overlay) return; // overlay markup not present (some test fixtures)
  const seq = ++launchSeq;
  clearLaunchTimers();
  clearGameMorph();               // a relaunch during a morph must not inherit its half-done geometry
  restoreLaunchSource();          // a re-launch before teardown must not leave the last tile hidden
  resetHeroDragState(false);
  initHeroTileDrag();
  overlay.classList.remove('is-leaving');
  el('launch-title').textContent = launchMessage(gameName);
  if (el('launch-hint')) el('launch-hint').hidden = true;
  if (el('launch-cancel')) {
    el('launch-cancel').hidden = false;
    el('launch-cancel').classList.remove('is-highlighted');
  }
  overlay.hidden = false;
  restartLaunchRise();
  setLaunchArt(artUrl);
  flyLaunchTile(sourceEl);
  // Fade the page the tile is leaving. Cleared on teardown AND in showLobbyView — a stuck class
  // leaves the home view rendered at opacity 0, which looks like a dead app.
  const home = el('lobby-view');
  if (home) home.classList.add('is-launching');
  // Say so once it's clearly slow, and highlight the escape hatch: the overlay covers the in-game header, so
  // this button is the only exit from a launch that never finishes.
  launchTimers.push(setTimeout(() => {
    if (seq !== launchSeq) return;
    if (el('launch-hint')) el('launch-hint').hidden = false;
    if (el('launch-cancel')) el('launch-cancel').classList.add('is-highlighted');
  }, LAUNCH_SLOW_MS));
  launchTimers.push(setTimeout(() => { if (seq === launchSeq) hideLaunchOverlay(); }, LAUNCH_MAX_MS));
}

// FLIP the launch tile onto the clicked grid tile, then let CSS carry it home. Measure both rects,
// apply the inverse transform with the transition suppressed for one frame, force a reflow so that
// position is what the browser has painted, then drop back to the resting transform.
function flyLaunchTile(sourceEl) {
  const tile = el('launch-tile');
  if (!tile) { launchSource = null; return; }
  // Reset before the hidden check too, so a relaunch can't inherit the previous launch's entrance.
  resetHeroDragState(false);
  tile.classList.remove('is-popping', 'no-transition', 'is-dancing');
  tile.style.transform = '';
  tile.style.width = '';
  if (tile.hidden) { launchSource = null; return; }
  const src = sourceEl ? sourceEl.getBoundingClientRect() : null;
  if (src && src.width) tile.style.width = `${Math.round(launchTileWidth(src.width))}px`;
  // Carry the clicked tile's own shadow colour across. The grid cycles pink/cyan/yellow by
  // nth-child, so a hardcoded shadow here snaps two thirds of the tiles to pink on the first frame
  // of the flight — which is the one frame this whole transition exists to make continuous.
  adoptLaunchShadow(sourceEl, tile);
  const flip = src ? launchFlipFrom(src, tile.getBoundingClientRect()) : null;
  const seq = launchSeq;
  if (!flip) {
    // No usable source rect: no click to fly from, a tile scrolled out of view, or jsdom (where every
    // rect is zero). Arrive in place. The reflow flushes the class removal above, so re-launching
    // actually restarts the animation instead of leaving it finished.
    void tile.offsetWidth;
    tile.classList.add('is-popping');
    launchSource = null;
    launchTimers.push(setTimeout(() => {
      if (seq === launchSeq && !dragActive && launchOverlayUp() && tile && !tile.hidden) {
        tile.classList.add('is-dancing');
      }
    }, 280));
    return;
  }
  // Start at the source tile's own nth-child angle and settle to square — picking a sticker up off
  // the table straightens it.
  const rot = rotationFromMatrix(getComputedStyle(sourceEl).transform);
  tile.classList.add('no-transition');
  tile.style.transform =
    `translate(${flip.dx}px, ${flip.dy}px) scale(${flip.scale}) rotate(${rot}deg)`;
  void tile.offsetWidth;   // flush the start state before the transition is re-enabled
  tile.classList.remove('no-transition');
  tile.style.transform = '';
  // Start synchronized dance bounce once the flight transition has settled.
  launchTimers.push(setTimeout(() => {
    if (seq === launchSeq && !dragActive && launchOverlayUp() && tile && !tile.hidden) {
      tile.classList.add('is-dancing');
    }
  }, 420));
  // Hide the original so it doesn't double-image beneath the flying copy. The flying tile is exactly
  // on top of it at this instant, so the swap itself is invisible.
  launchSource = sourceEl;
  sourceEl.style.visibility = 'hidden';
}

// Un-hiding the overlay normally restarts its animations for us (leaving display:none does that), but
// a relaunch that lands before the previous teardown never went away — so the status group would sit
// there already risen. Force it back to the start.
function restartLaunchRise() {
  const status = document.querySelector('.launch-status');
  if (!status) return;
  status.style.animation = 'none';
  void status.offsetWidth;
  status.style.animation = '';
}

// Reads --tile-shadow-hover off the clicked tile onto the launch tile. Falsy (no source element, or
// jsdom, where custom properties resolve to '') leaves the CSS fallback in place, so a launch with no
// tile to fly from is unaffected.
function adoptLaunchShadow(sourceEl, tile) {
  tile.style.removeProperty('--launch-shadow-color');
  if (!sourceEl) return;
  const hover = getComputedStyle(sourceEl).getPropertyValue('--tile-shadow-hover').trim();
  if (hover) tile.style.setProperty('--launch-shadow-color', hover);
}

// How big the tile should be once it lands: a quarter larger than the one that was clicked, so the
// move always reads as coming forward. The grid is elastic (minmax(240px, 1fr)), so a fixed size would
// be a shrink on a wide window. Capped by the viewport — width, and height via the 3/2 ratio, leaving
// room for the status group beneath. On a phone, where the tile is already near full-bleed, it simply
// stays put and the travel does the work.
function launchTileWidth(sourceWidth) {
  const maxWidth = Math.min(window.innerWidth - 32, window.innerHeight * 0.45 * 1.5);
  return Math.min(sourceWidth * 1.25, maxWidth);
}

function restoreLaunchSource() {
  if (launchSource) launchSource.style.visibility = '';
  launchSource = null;
}

// Join-by-code only learns which game it is when EnterGame lands. Re-label in place WITHOUT
// restarting the timers — the wait started at the click, not here.
export function setLaunchName(gameName, artUrl) {
  if (!launchOverlayUp()) return;
  el('launch-title').textContent = launchMessage(gameName);
  const tile = el('launch-tile');
  const hadTile = tile && !tile.hidden;
  setLaunchArt(artUrl);
  // Art arriving for a launch that had none (the join-by-code path): introduce the tile rather than
  // snapping it in. A launch already showing its tile is left alone — re-running the entrance
  // mid-flight would be the jolt this design exists to avoid.
  if (tile && !tile.hidden && !hadTile) flyLaunchTile(null);
}

function setLaunchArt(artUrl) {
  const art = el('launch-art');
  const tile = el('launch-tile');
  if (!art) return;
  // Stamped with the launch it belongs to: an error arriving from a load we've already moved past
  // must not hide the tile of the launch that is on screen now.
  const seq = launchSeq;
  const failed = () => { if (seq === launchSeq && tile) tile.hidden = true; };
  art.onerror = null;
  // No art (or a broken thumbnail) means no tile — the dots and the title carry the launch alone.
  if (!artUrl) {
    if (tile) tile.hidden = true;
    art.removeAttribute('src');
    return;
  }
  // Already showing exactly this art (createLobby raises the overlay, then enterGame re-labels it
  // with the same game): leave the element — and the tile's shown/hidden state — alone. Reloading an
  // identical src would blink the tile mid-flight, which is the jolt this design exists to avoid.
  if (art.getAttribute('src') === artUrl) { art.onerror = failed; return; }
  // An <img> keeps painting its current image until the pending one decodes, so clear it first:
  // otherwise a slow (or 404) thumbnail leaves the PREVIOUS game's art sitting on the tile.
  art.removeAttribute('src');
  art.src = artUrl;
  art.onerror = failed;
  if (tile) tile.hidden = false;
}

// Nothing here waits. The instant a launch is over, whatever it was holding back is released: the home
// view stops being faded and the borrowed tile goes back to the grid so it isn't a hole if that's where
// we're headed.
//
// `intoGame` is passed only by the iframe's `load` handler — the one ending where a real game is ready
// on the other side. Then the tile hands over: the game takes the exact rect the tile had reached and
// expands from it, and the overlay is gone from that first frame. Every other ending (an error, a
// bail-out, the LAUNCH_MAX_MS ceiling, a launch that never had a tile) just fades.
export function hideLaunchOverlay(intoGame = false) {
  launchSeq++;   // a late `load` from the frame we're dropping can't reopen/close a newer overlay
  clearLaunchTimers();
  const overlay = el('launch-overlay');
  restoreLaunchSource();
  clearLaunchingClass();
  if (!overlay || overlay.hidden) { unveilGameView(); return; }
  const tile = el('launch-tile');
  const from = intoGame && tile && !tile.hidden ? tile.getBoundingClientRect() : null;
  if (from && from.width && startGameMorph(from)) {
    teardownLaunchOverlay();   // no fade: the game is already standing where the tile stood
    return;
  }
  unveilGameView();
  overlay.classList.add('is-leaving');
  const seq = launchSeq;
  launchTimers.push(setTimeout(() => { if (seq === launchSeq) teardownLaunchOverlay(); }, LAUNCH_EXIT_MS));
}

// Put the game view exactly where the tile was — mid-flight or settled, whichever it had reached — and
// let it grow to fill the screen. The scale is deliberately non-uniform: matching the tile's rect on
// both axes is what makes the game look like it came OUT of that tile, and a uniform scale would start
// the game at nearly full height on a portrait phone, where there'd be nothing left to expand.
//
// Driven by the Web Animations API rather than a CSS transition. A transition here has to be armed by
// writing a start value, forcing a reflow and then clearing it, which makes the animation a hostage of
// style-recalc ordering: it was observed sticking at the start matrix with playState 'running' and
// transition-duration 0s, leaving the game frozen at tile size. An explicit animation has explicit
// keyframes, its own clock, and a `finished` promise, so none of that can race.
//
// Returns false when there's nothing to morph (or motion is unwanted), so the caller falls back to the
// fade.
function startGameMorph(from) {
  const view = el('game-view');
  if (!view || view.style.display !== 'block' || typeof view.animate !== 'function') return false;
  if (prefersReducedMotion()) return false;
  view.classList.remove('launch-veil');
  const to = view.getBoundingClientRect();
  if (!to.width || !to.height) return false;
  const sx = from.width / to.width;
  const sy = from.height / to.height;
  cancelGameMorph();
  view.classList.add('launch-morph');       // a marker for unveilGameView, and hints the compositor
  view.style.transformOrigin = '0 0';
  view.style.overflow = 'hidden';           // so the rounded corners actually clip the game
  gameMorph = view.animate(
    [
      {
        transform: `translate(${from.left - to.left}px, ${from.top - to.top}px) scale(${sx}, ${sy})`,
        // Elliptical radii, pre-divided by the scale, so the squashed corner still reads as 16px.
        borderRadius: `${16 / sx}px / ${16 / sy}px`,
      },
      { transform: 'none', borderRadius: '0px' },
    ],
    { duration: LAUNCH_MORPH_MS, easing: LAUNCH_MORPH_EASING, fill: 'none' },
  );
  // `fill: none` means the element is already back on its own styles by the time this resolves, so
  // there's no frame where a finished animation still pins the geometry. A cancel rejects instead.
  gameMorph.finished.then(endGameMorph, () => {});
  // Safety net, comfortably past the end so it can't clip the last frames: without it, an animation
  // that never resolves would leave the background permanently un-swapped. Held on its own, not in
  // launchTimers, so ending the morph can cancel it — a stray one firing later would strip the class
  // off whatever launch is running by then.
  morphTimer = setTimeout(endGameMorph, LAUNCH_MORPH_MS + 120);
  return true;
}

function prefersReducedMotion() {
  return typeof matchMedia === 'function' && matchMedia('(prefers-reduced-motion: reduce)').matches;
}

function cancelGameMorph() {
  if (!gameMorph) return;
  const morph = gameMorph;
  gameMorph = null;
  morph.cancel();
}

// Strip every trace of the morph. Called on completion, and again by showLobbyView in case a player
// leaves mid-expand — a half-scaled game view left behind would break the next session.
function clearGameMorph() {
  cancelGameMorph();
  if (morphTimer) { clearTimeout(morphTimer); morphTimer = null; }
  const view = el('game-view');
  if (!view) return;
  view.classList.remove('launch-morph');
  view.style.transformOrigin = '';
  view.style.overflow = '';
}

function endGameMorph() {
  clearGameMorph();
  // Only now does the game actually fill the screen, which makes this the one invisible moment to swap
  // the background out from under it.
  document.body.classList.add('in-game');
}

function teardownLaunchOverlay() {
  resetHeroDragState();
  const overlay = el('launch-overlay');
  if (overlay) {
    overlay.hidden = true;
    overlay.classList.remove('is-leaving');
  }
  const tile = el('launch-tile');
  if (tile) {
    tile.classList.remove('is-popping', 'no-transition', 'is-dancing');
    tile.style.transform = '';
    tile.style.width = '';
    tile.style.removeProperty('--launch-shadow-color');
  }
  unveilGameView();   // belt and braces: the overlay must never leave the game veiled behind it
  if (el('launch-cancel')) {
    el('launch-cancel').classList.remove('is-highlighted');
  }
  if (el('launch-hint')) {
    el('launch-hint').hidden = true;
  }
  restoreLaunchSource();
  clearLaunchingClass();
}

function clearLaunchingClass() {
  const home = el('lobby-view');
  if (home) home.classList.remove('is-launching');
}

// Give up on the launch in flight: take the overlay down AND invalidate it, so a reply that lands
// afterwards stands down instead of dragging the player into a session they backed out of.
export function abortLaunch() {
  resetHeroDragState();
  launchAbortSeq++;
  hideLaunchOverlay();
}

// The tile art is served same-origin (the shell origin gates /games/* to the declared thumbnail).
function launchArtUrl(manifest) {
  return manifest && manifest.thumbnail ? `/games/${manifest.id}/${manifest.thumbnail}` : null;
}

// Switch to the game view. The overlay is transparent, so while a launch is still on screen the game
// view is put in place but veiled — it has to be in the layout for the iframe to download, and it must
// not be seen half-built. The background state (body.in-game) waits with it: swapping it now would
// change the one thing the launch animation keeps still. Both are released by hideLaunchOverlay's
// cross-fade, or immediately when no launch is covering (a reconnect that rebuilds the view).
function revealGameView() {
  el('game-view').style.display = 'block';
  el('lobby-view').style.display = 'none';
  if (launchOverlayUp()) el('game-view').classList.add('launch-veil');
  else unveilGameView();
}

function unveilGameView() {
  const view = el('game-view');
  if (!view) return;
  view.classList.remove('launch-veil');
  // Only claim the in-game background once the game view is actually the thing on screen — which
  // during a morph it isn't yet, so endGameMorph does it instead.
  if (view.style.display === 'block' && !view.classList.contains('launch-morph')) {
    document.body.classList.add('in-game');
  }
}

// ── Transient error toast ─────────────────────────────────────────────────────
function showError(message) {
  // Every launch failure surfaces as a toast — the enterGame bail-outs, a rejected CreateLobby/
  // JoinLobby, and server-pushed Errors — so clearing the overlay here covers them all in one place.
  hideLaunchOverlay();
  const prev = document.querySelector('.home-error-toast');
  if (prev) prev.remove();
  const toast = document.createElement('div');
  toast.className = 'home-error-toast';
  const icon = document.createElement('span');
  icon.className = 'home-error-icon';
  icon.setAttribute('aria-hidden', 'true');
  icon.textContent = '⚠️';
  const text = document.createElement('span');
  text.textContent = message;
  toast.append(icon, text);
  document.body.appendChild(toast);
  // Mirror the .home-error-toast CSS animation duration (3s) then remove.
  setTimeout(() => toast.remove(), 3000);
}

// Brief positive confirmation, mirroring showError's lifecycle but with the success styling.
function flashCopied() {
  const prev = document.querySelector('.home-copy-toast');
  if (prev) prev.remove();
  const toast = document.createElement('div');
  toast.className = 'home-copy-toast';
  toast.textContent = 'Copied!';
  document.body.appendChild(toast);
  setTimeout(() => toast.remove(), 3000);
}

// ── UI wiring ────────────────────────────────────────────────────────────────
const nameInput = el('player-name-input');
nameInput.value = displayName;
nameInput.addEventListener('input', () => {
  displayName = nameInput.value.trim();
  // Persist for the next browser session. This tab keeps its own in-memory name regardless of what
  // other tabs write (no `storage` listener) — see the identity note above.
  localStorage.setItem('kb.displayName', displayName);
  applyGate();
  sendNameDebounced();
});

// The three filter controls. Each seeds its module state from the DOM as it is wired, because
// browsers restore form-control values across a soft reload and back/forward navigation: without
// this the user comes back to "Sort: Alphabetical" on screen while the module still holds 'newest',
// and the controls contradict the grid until touched. (The markup also carries autocomplete="off",
// which is the other half — this half makes the DOM authoritative whatever any browser does.)
const searchInput = el('games-search-input');
if (searchInput) {
  searchQuery = searchInput.value;
  // Per-keystroke rendering rebuilds every tile and every <img>, so typing five characters
  // re-creates the thumbnails five times — visible flicker, a re-decode each pass, and lazy loading
  // restarted for everything below the fold. Collapse a burst into one render.
  searchInput.addEventListener('input', () => {
    searchQuery = searchInput.value;
    renderGamesDebounced();
  });
}

const playerFilterSelect = el('games-player-filter');
if (playerFilterSelect) {
  playerFilter = playerFilterSelect.value;
  // Discrete events, not a keystroke stream: render immediately.
  playerFilterSelect.addEventListener('change', () => {
    playerFilter = playerFilterSelect.value;
    renderGames();
  });
}

const sortSelect = el('games-sort-select');
if (sortSelect) {
  sortOption = sortSelect.value || 'newest';
  sortSelect.addEventListener('change', () => {
    sortOption = sortSelect.value;
    renderGames();
  });
}

// A game iframe (on the game origin) can tell us its session ended terminally — kicked, ticket
// expired, or lobby gone — so we leave the game view even if the control-plane push was missed.
// This only fires on a terminal socket close, never on a normal game-over (the data socket stays up).
window.addEventListener('message', (e) => {
  // Only trust the game origin, and never before Welcome has set it (until then gameOrigin is null,
  // so a same-origin message sent during initial load can't spoof a session-ended).
  if (!gameOrigin || e.origin !== gameOrigin) return;
  if (e.data && e.data.kb === 'session-ended' && lobby) {
    sessionStorage.removeItem('kb.lobbyId');
    showLobbyView();
    showError('The game session ended.');
  }
});

el('join-form').addEventListener('submit', (e) => { e.preventDefault(); joinByCode(); });

export function leaveGame() {
  if (lobby) send({ type: 'LeaveLobby', lobbyId: lobby.lobbyId });
  sessionStorage.removeItem('kb.lobbyId');
  showLobbyView();
}

el('leave').onclick = leaveGame;

// Escape hatch for a launch that stalls before the in-game header (and its Leave button) exists.
if (el('launch-cancel')) el('launch-cancel').onclick = leaveGame;

// The game name doubles as a "home" link: leave the session and return to the lobby view in-SPA.
// href="/" is the no-JS fallback; we intercept so the control socket stays up.
el('game-title').addEventListener('click', (e) => {
  e.preventDefault();
  leaveGame();
});

// ── Room code button: click crossfades the code; dbl-click opens a big modal; right-click and
// mobile long-press copy to the clipboard. ───────────────────────────────────────────────────
async function copyRoomCode() {
  if (!lobby) return;
  try {
    await navigator.clipboard.writeText(lobby.lobbyId);
    flashCopied();
  } catch {
    showError('Could not copy.');
  }
}

// Shareable auto-join URL for this lobby: opening it lands a player straight in the lobby (see the
// "?join=" handling at startup). Carries only the public room code — no identity token.
function joinLink() {
  return buildJoinLink(location.origin, lobby.lobbyId);
}

async function copyJoinLink() {
  if (!lobby) return;
  try {
    await navigator.clipboard.writeText(joinLink());
    flashCopied();
  } catch {
    showError('Could not copy.');
  }
}

// Shared dialog focus management for the .rc-modal overlays (room-code + clear-play-log). Both
// declare aria-modal, so on open we remember the trigger and move focus in; on close we restore it
// (a keyboard user lands back where they were, not at the top of the document); Tab is trapped
// inside the open dialog by the keydown handler below.
let lastFocused = null;   // control to restore focus to when the active modal closes
let activeModal = null;   // the currently-open .rc-modal element, or null

// Focusable controls inside a dialog card (buttons; the backdrop is not focusable). Exclude
// anything inside a [hidden] subtree — correct in both jsdom and a real browser (offsetParent is
// always null in jsdom, so it can't be used here).
function modalFocusables(modal) {
  return [...modal.querySelectorAll('button:not([disabled]), [href], [tabindex]:not([tabindex="-1"])')]
    .filter((n) => !n.closest('[hidden]'));
}

function openModal(modal, initialFocus) {
  lastFocused = document.activeElement;
  modal.hidden = false;
  activeModal = modal;
  (initialFocus || modalFocusables(modal)[0])?.focus();
}

function closeModal(modal) {
  modal.hidden = true;
  if (activeModal === modal) activeModal = null;
  if (lastFocused?.isConnected) lastFocused.focus(); // return focus to the trigger
  lastFocused = null;
}

function openCodeModal() {
  if (!lobby) return;
  el('rc-modal-code').textContent = lobby.lobbyId;
  openModal(el('rc-modal'), el('rc-modal-copy'));
}

function closeCodeModal() {
  closeModal(el('rc-modal'));
}

function openClearModal() {
  const n = readPlayLog().length;
  if (!n) return; // nothing to clear
  el('pl-clear-text').textContent =
    `This removes all ${n} ${n === 1 ? 'entry' : 'entries'} and can't be undone.`;
  // default focus on the safe (Cancel) path
  openModal(el('pl-clear-modal'), el('pl-clear-modal').querySelector('.rc-modal-copy.secondary'));
}

function closeClearModal() {
  closeModal(el('pl-clear-modal'));
}

const rc = el('room-code-btn');
let longPressTimer = null;
let longPressed = false;
// -Infinity, not 0, and the same on reset below: this is "there was no previous click", which must not
// be a value the clock can actually report. performance.now() starts AT 0, so a 0 sentinel says "a click
// just happened" for the first quarter-second of the timeline — and a fake clock, which stays at 0, says
// it forever, so the very first click opened the modal instead of revealing the code.
let lastClickAt = -Infinity;
const DBL_MS = 250;

// Single click/tap toggles the crossfade immediately (instant feedback); a quick second click/tap
// within DBL_MS opens the big modal. We detect the double from successive `click` events rather
// than the native `dblclick` so the "mobile-friendly large view" is actually reachable on touch —
// `dblclick` doesn't reliably fire on touch (and `contextmenu` for copy doesn't either; long-press
// covers copy there). DBL_MS is a fixed guess independent of the OS double-click interval.
rc.addEventListener('click', () => {
  if (longPressed) return; // a long-press already handled this gesture
  if (!el('rc-modal').hidden) return; // never toggle the code behind an open modal
  const now = performance.now();
  if (now - lastClickAt < DBL_MS) { // second click/tap → open the large view
    lastClickAt = -Infinity;
    rc.classList.remove('revealed'); // reset to "Room Code" behind the modal so it's hidden on close
    openCodeModal();
    return;
  }
  lastClickAt = now;
  rc.classList.toggle('revealed');
});

// Native double-click also opens the modal — robust on desktop (and the canonical signal). Touch,
// where dblclick doesn't reliably fire, is covered by the click-based detection above. Both paths
// call openCodeModal, which is idempotent, so a desktop double-click triggering both is harmless.
rc.addEventListener('dblclick', () => {
  rc.classList.remove('revealed'); // reset to "Room Code" behind the modal so it's hidden on close
  openCodeModal();
});

rc.addEventListener('contextmenu', (e) => {
  e.preventDefault();
  if (longPressed) return; // long-press already copied
  copyRoomCode();
});

rc.addEventListener('touchstart', () => {
  longPressed = false;
  longPressTimer = setTimeout(() => {
    longPressed = true;
    copyRoomCode();
  }, 500);
}, { passive: true });

const cancelLongPress = () => { if (longPressTimer) { clearTimeout(longPressTimer); longPressTimer = null; } };
rc.addEventListener('touchend', cancelLongPress);
rc.addEventListener('touchmove', cancelLongPress);
rc.addEventListener('touchcancel', cancelLongPress);

// Middle-click opens a new tab that auto-joins this lobby — a quick way to add a test player.
rc.addEventListener('mousedown', (e) => { if (e.button === 1) e.preventDefault(); }); // no autoscroll
rc.addEventListener('auxclick', (e) => {
  if (e.button !== 1 || !lobby) return; // middle button only
  e.preventDefault();
  window.open(joinLink(), '_blank');
});

// Modal controls.
el('rc-modal-copy').addEventListener('click', () => { copyRoomCode(); closeCodeModal(); });
el('rc-modal-copy-link').addEventListener('click', () => { copyJoinLink(); closeCodeModal(); });
el('rc-modal').querySelectorAll('[data-rc-close]').forEach((node) =>
  node.addEventListener('click', closeCodeModal));

// Clear Play Log: the button only deletes after the confirmation modal's "Clear All".
el('playlog-clear').addEventListener('click', openClearModal);
el('pl-clear-confirm').addEventListener('click', () => { clearPlayLog(); closeClearModal(); });
el('pl-clear-modal').querySelectorAll('[data-pl-close]').forEach((node) =>
  node.addEventListener('click', closeClearModal));

document.addEventListener('keydown', (e) => {
  if (e.key === 'Escape') {
    if (!el('rc-modal').hidden) closeCodeModal();
    if (!el('pl-clear-modal').hidden) closeClearModal();
    return;
  }
  // Trap Tab within the open dialog so focus can't wander to controls behind the overlay.
  if (e.key === 'Tab' && activeModal) {
    const f = modalFocusables(activeModal);
    if (!f.length) return;
    const first = f[0], last = f[f.length - 1];
    if (e.shiftKey && document.activeElement === first) { e.preventDefault(); last.focus(); }
    else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first.focus(); }
  }
});

// Dismissing the banner is remembered in localStorage (not sessionStorage) against the announcement's
// id: it survives closing the browser, and an operator editing the notice mints a new id, so the new
// wording comes back for everyone who dismissed the old one.
el('announcement-dismiss').addEventListener('click', () => {
  if (announcement) localStorage.setItem('kb.announcementDismissed', announcement.id);
  renderAnnouncement();
});

applyGate();
renderAnnouncement();
renderPlayLog(); // home view is shown by default on load — populate the Play Log from storage
initHeroTileDrag();

// Start the control socket. On a real page load index.html imports this module and calls bootstrap();
// importing the module on its own no longer opens a socket, so the test suite can drive the exported
// functions (and call connect() itself) without an auto-connect to suppress.
export function bootstrap() {
  applyRandomFavicon();
  connect();
}

// Swap the page's favicon to a random cat on load (recreating the legacy server's per-render pick).
// Reuses the static <link rel="icon"> seeded in index.html so we update one element rather than
// appending a second; falls back to creating it if absent.
function applyRandomFavicon() {
  const href = pickRandomFavicon();
  if (!href) return;
  let link = document.head.querySelector('link[rel="icon"]');
  if (!link) {
    link = document.createElement('link');
    link.rel = 'icon';
    link.type = 'image/png';
    document.head.appendChild(link);
  }
  link.href = href;
}
