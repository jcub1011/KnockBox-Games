// @vitest-environment jsdom
//
// Integration coverage for web/shell.js. The shell is a side-effecting module (it wires the DOM and
// would open the control socket at import), so each test loads the real index.html markup, installs
// a FakeWebSocket, sets `globalThis.__KB_TEST__` (which suppresses the auto-connect), then imports a
// fresh copy of the module and drives it through (a) the exported functions and (b) the WebSocket
// protocol + DOM events. Assertions read observable state: sent frames, DOM text/visibility, storage.
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { loadShellDom, FakeWebSocket, installFakeWebSocket, stubClipboard, tick } from './helpers.js';

const el = (id) => document.getElementById(id);

let getWs;   // () => latest FakeWebSocket the module created
let shell;   // the freshly-imported module namespace

// Import shell.js fresh after preconditions (name, location) are in place.
async function importShell() {
  shell = await import('../shell.js');
  return shell;
}

// connect() + a confirmed session with a game catalog. Returns the live fake socket.
async function bootWithGames(games = [{ id: 'ttt', name: 'Tic Tac Toe', entry: 'index.html', maxPlayers: 2 }]) {
  shell.connect();
  const ws = getWs();
  ws._open();
  ws._recv({ type: 'Welcome', playerId: 'p1', token: 'tok', gameOrigin: 'http://games.test' });
  const list = ws.sent.find((f) => f.type === 'ListGames');
  ws._recv({ cid: list.cid, games });
  await tick();
  return ws;
}

// Drive createLobby to its success reply so the module is "in a lobby" (lobby state + room view).
async function createLobbySuccess(ws, { gameId = 'ttt', lobbyId = 'AB12' } = {}) {
  const p = shell.createLobby(gameId);
  const frame = ws.sent.find((f) => f.type === 'CreateLobby');
  ws._recv({ cid: frame.cid, type: 'LobbyCreated', lobbyId });
  await p;
}

beforeEach(() => {
  vi.resetModules();
  globalThis.__KB_TEST__ = true;
  localStorage.clear();
  sessionStorage.clear();
  loadShellDom();
  getWs = installFakeWebSocket();
});

afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  delete globalThis.__KB_TEST__;
});

describe('handshake & identity', () => {
  it('sends Hello (name/token/proto) on open and reports connection status', async () => {
    localStorage.setItem('kb.displayName', 'Alice');
    sessionStorage.setItem('kb.token', 'old-token');
    await importShell();

    shell.connect();
    const ws = getWs();
    expect(ws.url).toMatch(/^ws:\/\/.+\/ws$/);
    ws._open();

    expect(el('conn').textContent).toBe('online');
    expect(ws.sent[0]).toEqual({ type: 'Hello', displayName: 'Alice', token: 'old-token', proto: 1 });
  });

  it('persists the server token and game origin on Welcome, then lists games', async () => {
    await importShell();
    const ws = await bootWithGames();

    expect(sessionStorage.getItem('kb.token')).toBe('tok');
    expect(ws.sent.some((f) => f.type === 'ListGames')).toBe(true);
    // a tile was rendered for the discovered game
    expect(el('games').querySelectorAll('.game-tile')).toHaveLength(1);
  });

  it('does not auto-connect at import under the test flag', async () => {
    await importShell();
    expect(FakeWebSocket.instances).toHaveLength(0);
  });
});

describe('auto-join (?join=CODE)', () => {
  it('clears the inherited identity, then joins the URL code once connected', async () => {
    sessionStorage.setItem('kb.token', 'inherited');
    sessionStorage.setItem('kb.lobbyId', 'OLD1');
    vi.stubGlobal('location', {
      search: '?join=ab12', pathname: '/', origin: 'http://localhost:5114',
      host: 'localhost:5114', protocol: 'http:', hash: '',
    });
    await importShell();

    // inherited identity scrubbed so this tab becomes a distinct player
    expect(sessionStorage.getItem('kb.token')).toBeNull();
    expect(sessionStorage.getItem('kb.lobbyId')).toBeNull();

    const ws = await bootWithGames();
    // auto-join issues a JoinLobby for the normalized (upper-cased) code
    const join = ws.sent.find((f) => f.type === 'JoinLobby');
    expect(join).toBeTruthy();
    expect(join.lobbyId).toBe('AB12');
  });
});

describe('game catalog rendering', () => {
  it('shows an empty-state message when no games are discovered', async () => {
    await importShell();
    await bootWithGames([]);
    expect(el('games').querySelector('.games-empty')).toBeTruthy();
  });

  it('falls back to the name surface when a thumbnail fails to load', async () => {
    await importShell();
    await bootWithGames([{ id: 'ttt', name: 'Tic Tac Toe', thumbnail: 'thumb.png' }]);
    const img = el('games').querySelector('img.game-tile-img');
    expect(img).toBeTruthy();
    img.onerror(); // simulate a broken image
    const fallback = el('games').querySelector('.game-tile-fallback');
    expect(fallback.textContent).toBe('Tic Tac Toe');
  });
});

describe('name gate', () => {
  it('enables the Join button and game tiles only once a name is entered', async () => {
    await importShell();          // no saved name
    await bootWithGames();
    expect(el('join-btn').disabled).toBe(true);
    expect(el('games').querySelector('.game-tile').disabled).toBe(true);

    const name = el('player-name-input');
    name.value = 'Bob';
    name.dispatchEvent(new Event('input', { bubbles: true }));

    expect(el('join-btn').disabled).toBe(false);
    expect(el('games').querySelector('.game-tile').disabled).toBe(false);
    expect(localStorage.getItem('kb.displayName')).toBe('Bob');
  });
});

describe('createLobby', () => {
  beforeEach(() => localStorage.setItem('kb.displayName', 'Alice'));

  it('persists the lobby id and shows the room on success', async () => {
    await importShell();
    const ws = await bootWithGames();
    await createLobbySuccess(ws, { lobbyId: 'AB12' });

    expect(sessionStorage.getItem('kb.lobbyId')).toBe('AB12');
    expect(el('game-view').style.display).toBe('block');
    expect(el('lobby-view').style.display).toBe('none');
    expect(el('lobby-code').textContent).toBe('AB12');
  });

  it('surfaces an error toast when the server rejects the create', async () => {
    await importShell();
    const ws = await bootWithGames();
    const p = shell.createLobby('ttt');
    const frame = ws.sent.find((f) => f.type === 'CreateLobby');
    ws._recv({ cid: frame.cid, type: 'Error', reason: 'Rate limited.' });
    await p;

    expect(document.querySelector('.home-error-toast').textContent).toContain('Rate limited.');
    expect(sessionStorage.getItem('kb.lobbyId')).toBeNull();
  });

  it('refuses to create without a name', async () => {
    localStorage.clear();
    await importShell();
    const ws = await bootWithGames();
    await shell.createLobby('ttt');
    expect(ws.sent.some((f) => f.type === 'CreateLobby')).toBe(false);
    expect(document.querySelector('.home-error-toast')).toBeTruthy();
  });
});

describe('joinByCode', () => {
  beforeEach(() => localStorage.setItem('kb.displayName', 'Alice'));

  it('upper-cases the code and shows the room on success', async () => {
    await importShell();
    const ws = await bootWithGames();
    el('room-code-input').value = 'ab12';

    el('join-form').dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
    const join = ws.sent.find((f) => f.type === 'JoinLobby');
    expect(join.lobbyId).toBe('AB12');
    ws._recv({ cid: join.cid, type: 'Joined', lobbyId: 'AB12' });
    await tick();

    expect(sessionStorage.getItem('kb.lobbyId')).toBe('AB12');
    expect(el('game-view').style.display).toBe('block');
  });

  it('clears lobby state and toasts when the join is rejected', async () => {
    await importShell();
    const ws = await bootWithGames();
    el('room-code-input').value = 'ZZ99';
    el('join-form').dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
    const join = ws.sent.find((f) => f.type === 'JoinLobby');
    ws._recv({ cid: join.cid, type: 'Error', reason: 'No such lobby.' });
    await tick();

    expect(document.querySelector('.home-error-toast').textContent).toContain('No such lobby.');
    expect(el('lobby-view').style.display).not.toBe('none'); // never flashed the room
  });

  it('refuses to join with an empty code', async () => {
    await importShell();
    const ws = await bootWithGames();
    el('room-code-input').value = '   ';
    el('join-form').dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
    expect(ws.sent.some((f) => f.type === 'JoinLobby')).toBe(false);
    expect(document.querySelector('.home-error-toast')).toBeTruthy();
  });
});

describe('roster updates', () => {
  beforeEach(() => localStorage.setItem('kb.displayName', 'Alice'));

  it('reflects PlayerJoined / PlayerLeft for the current lobby only', async () => {
    await importShell();
    const ws = await bootWithGames();
    await createLobbySuccess(ws, { lobbyId: 'AB12' });

    // scoped to a different lobby — ignored (no throw, no state change)
    shell.handle({ type: 'PlayerJoined', lobbyId: 'OTHER', player: { id: 'x', displayName: 'X' } });
    // current lobby — applied
    shell.handle({ type: 'PlayerJoined', lobbyId: 'AB12', player: { id: 'p2', displayName: 'Bob' } });
    shell.handle({ type: 'PlayerLeft', lobbyId: 'AB12', playerId: 'p2' });
    expect(el('waiting').textContent).toBe('Loading game…'); // updateWaiting ran without error
  });
});

describe('enterGame (GameStarting)', () => {
  beforeEach(() => localStorage.setItem('kb.displayName', 'Alice'));

  it('rejects a GameStarting for an unknown game id', async () => {
    await importShell();
    const ws = await bootWithGames();
    shell.handle({ type: 'GameStarting', lobbyId: 'AB12', gameId: 'nope', hostId: 'p1', players: [] });
    await tick();
    expect(document.querySelector('.home-error-toast').textContent).toContain('Unknown game.');
    expect(ws.sent.some((f) => f.type === 'RequestGameTicket')).toBe(false);
  });

  it('requests a ticket and embeds the game iframe (ticket in the fragment)', async () => {
    await importShell();
    const ws = await bootWithGames();

    shell.enterGame({ type: 'GameStarting', lobbyId: 'AB12', gameId: 'ttt', hostId: 'p1', players: [] });
    const req = ws.sent.find((f) => f.type === 'RequestGameTicket');
    expect(req.lobbyId).toBe('AB12');
    ws._recv({ cid: req.cid, type: 'GameTicket', ticket: 'tok+/=' });
    await tick();

    const frame = el('game-frame');
    expect(frame).toBeTruthy();
    expect(frame.src.startsWith('http://games.test/games/ttt/')).toBe(true);
    expect(frame.src.includes('#')).toBe(true);
    expect(frame.src.includes('?')).toBe(false); // creds in the fragment, never the query string
    expect(el('waiting').style.display).toBe('none');
  });

  it('sets the cross-origin-isolated allow attribute when the manifest asks for it', async () => {
    await importShell();
    const ws = await bootWithGames([{ id: 'coi', name: 'Threaded', crossOriginIsolated: true, entry: 'index.html' }]);
    shell.enterGame({ type: 'GameStarting', lobbyId: 'AB12', gameId: 'coi', hostId: 'p1', players: [] });
    const req = ws.sent.find((f) => f.type === 'RequestGameTicket');
    ws._recv({ cid: req.cid, type: 'GameTicket', ticket: 't' });
    await tick();
    expect(el('game-frame').allow).toBe('cross-origin-isolated');
  });
});

describe('control-plane messages', () => {
  beforeEach(() => localStorage.setItem('kb.displayName', 'Alice'));

  it('Kicked clears the lobby, returns home, and toasts', async () => {
    await importShell();
    const ws = await bootWithGames();
    await createLobbySuccess(ws, { lobbyId: 'AB12' });

    shell.handle({ type: 'Kicked', lobbyId: 'AB12' });
    expect(sessionStorage.getItem('kb.lobbyId')).toBeNull();
    expect(el('lobby-view').style.display).toBe('block');
    expect(document.querySelector('.home-error-toast').textContent).toContain('kicked');
  });

  it('RejoinFailed forgets the saved lobby and shows the lobby view', async () => {
    sessionStorage.setItem('kb.lobbyId', 'AB12');
    await importShell();
    shell.handle({ type: 'RejoinFailed' });
    expect(sessionStorage.getItem('kb.lobbyId')).toBeNull();
    expect(el('lobby-view').style.display).toBe('block');
  });

  it('Error shows a toast with the supplied reason', async () => {
    await importShell();
    shell.handle({ type: 'Error', reason: 'Boom.' });
    expect(document.querySelector('.home-error-toast').textContent).toContain('Boom.');
  });

  it('tryRejoin sends Rejoin for a saved lobby on reconnect', async () => {
    sessionStorage.setItem('kb.lobbyId', 'AB12');
    await importShell();
    const ws = await bootWithGames(); // Welcome → refreshGames → tryRejoin
    expect(ws.sent.some((f) => f.type === 'Rejoin' && f.lobbyId === 'AB12')).toBe(true);
  });
});

describe('leaving the game', () => {
  beforeEach(() => localStorage.setItem('kb.displayName', 'Alice'));

  it('leaveGame sends LeaveLobby, clears the lobby, and returns home', async () => {
    await importShell();
    const ws = await bootWithGames();
    await createLobbySuccess(ws, { lobbyId: 'AB12' });

    el('leave').click();
    expect(ws.sent.some((f) => f.type === 'LeaveLobby' && f.lobbyId === 'AB12')).toBe(true);
    expect(sessionStorage.getItem('kb.lobbyId')).toBeNull();
    expect(el('lobby-view').style.display).toBe('block');
  });

  it('clicking the game title also leaves (in-SPA home link)', async () => {
    await importShell();
    const ws = await bootWithGames();
    await createLobbySuccess(ws, { lobbyId: 'AB12' });
    el('game-title').dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
    expect(ws.sent.some((f) => f.type === 'LeaveLobby')).toBe(true);
  });
});

describe('game→shell postMessage (session-ended)', () => {
  beforeEach(() => localStorage.setItem('kb.displayName', 'Alice'));

  it('returns home only for a message from the trusted game origin', async () => {
    await importShell();
    const ws = await bootWithGames(); // gameOrigin = http://games.test
    await createLobbySuccess(ws, { lobbyId: 'AB12' });

    // wrong origin → ignored
    window.dispatchEvent(new MessageEvent('message', { origin: 'http://evil.test', data: { kb: 'session-ended' } }));
    expect(el('game-view').style.display).toBe('block');

    // trusted origin → leaves the game view
    window.dispatchEvent(new MessageEvent('message', { origin: 'http://games.test', data: { kb: 'session-ended' } }));
    expect(sessionStorage.getItem('kb.lobbyId')).toBeNull();
    expect(el('lobby-view').style.display).toBe('block');
    expect(document.querySelector('.home-error-toast').textContent).toContain('session ended');
  });
});

describe('reconnect & backoff', () => {
  it('schedules a reconnect after a transient close', async () => {
    vi.useFakeTimers();
    await importShell();
    shell.connect();
    getWs()._open();
    getWs()._close(1006); // abnormal (server restart)
    expect(el('conn').textContent).toContain('reconnecting');

    expect(FakeWebSocket.instances).toHaveLength(1);
    vi.advanceTimersByTime(1000); // reconnectDelay(0)
    expect(FakeWebSocket.instances).toHaveLength(2);
  });

  it('resets the backoff after a confirmed session', async () => {
    vi.useFakeTimers();
    await importShell();
    shell.connect();                 // ws #1
    getWs()._open();
    getWs()._close(1006);            // attempt → 1, schedule at 1000ms
    vi.advanceTimersByTime(1000);
    expect(FakeWebSocket.instances).toHaveLength(2);

    // ws #2: confirm a session → backoff resets to attempt 0
    getWs()._open();
    getWs()._recv({ type: 'Welcome', playerId: 'p1', token: 't', gameOrigin: 'http://g' });
    getWs()._close(1006);            // if reset, next delay is again 1000ms
    vi.advanceTimersByTime(1000);
    expect(FakeWebSocket.instances).toHaveLength(3); // a 2000ms delay would NOT have fired yet
  });
});

describe('room-code button gestures', () => {
  beforeEach(() => localStorage.setItem('kb.displayName', 'Alice'));

  async function enterRoom() {
    await importShell();
    const ws = await bootWithGames();
    await createLobbySuccess(ws, { lobbyId: 'AB12' });
    return ws;
  }

  it('single click toggles the revealed state', async () => {
    await enterRoom();
    const btn = el('room-code-btn');
    expect(btn.classList.contains('revealed')).toBe(false);
    btn.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    expect(btn.classList.contains('revealed')).toBe(true);
  });

  it('double click opens the modal with the code', async () => {
    await enterRoom();
    el('room-code-btn').dispatchEvent(new MouseEvent('dblclick', { bubbles: true }));
    expect(el('rc-modal').hidden).toBe(false);
    expect(el('rc-modal-code').textContent).toBe('AB12');
  });

  it('right-click copies the code to the clipboard', async () => {
    const writeText = stubClipboard();
    await enterRoom();
    el('room-code-btn').dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true }));
    await tick();
    expect(writeText).toHaveBeenCalledWith('AB12');
    expect(document.querySelector('.home-copy-toast')).toBeTruthy();
  });

  it('middle-click opens an auto-join tab', async () => {
    const openSpy = vi.spyOn(window, 'open').mockReturnValue(null);
    await enterRoom();
    el('room-code-btn').dispatchEvent(new MouseEvent('auxclick', { button: 1, bubbles: true, cancelable: true }));
    expect(openSpy).toHaveBeenCalledTimes(1);
    expect(openSpy.mock.calls[0][0]).toContain('?join=AB12');
  });

  it('long-press copies the code (touch)', async () => {
    const writeText = stubClipboard();
    await importShell();
    const ws = await bootWithGames();
    await createLobbySuccess(ws, { lobbyId: 'AB12' });

    // Switch to fake timers only now — bootWithGames awaits a real macrotask (tick) to settle.
    vi.useFakeTimers();
    el('room-code-btn').dispatchEvent(new Event('touchstart'));
    vi.advanceTimersByTime(500);
    expect(writeText).toHaveBeenCalledWith('AB12');
  });
});

describe('clipboard failures & join link', () => {
  beforeEach(() => localStorage.setItem('kb.displayName', 'Alice'));

  it('toasts an error when the clipboard write is denied', async () => {
    const writeText = stubClipboard({ fail: true });
    await importShell();
    const ws = await bootWithGames();
    await createLobbySuccess(ws, { lobbyId: 'AB12' });

    el('room-code-btn').dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true }));
    await tick();
    expect(writeText).toHaveBeenCalled();
    expect(document.querySelector('.home-error-toast').textContent).toContain('Could not copy');
  });

  it('the modal "copy join link" button copies a shareable URL and closes the modal', async () => {
    const writeText = stubClipboard();
    await importShell();
    const ws = await bootWithGames();
    await createLobbySuccess(ws, { lobbyId: 'AB12' });

    el('room-code-btn').dispatchEvent(new MouseEvent('dblclick', { bubbles: true }));
    el('rc-modal-copy-link').click();
    await tick();
    expect(writeText.mock.calls[0][0]).toContain('?join=AB12');
    expect(el('rc-modal').hidden).toBe(true);
  });
});

describe('room-code modal', () => {
  beforeEach(() => localStorage.setItem('kb.displayName', 'Alice'));

  it('Escape closes the modal when it is open', async () => {
    await importShell();
    const ws = await bootWithGames();
    await createLobbySuccess(ws, { lobbyId: 'AB12' });
    el('room-code-btn').dispatchEvent(new MouseEvent('dblclick', { bubbles: true }));
    expect(el('rc-modal').hidden).toBe(false);

    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    expect(el('rc-modal').hidden).toBe(true);
  });

  it('the backdrop close target hides the modal', async () => {
    await importShell();
    const ws = await bootWithGames();
    await createLobbySuccess(ws, { lobbyId: 'AB12' });
    el('room-code-btn').dispatchEvent(new MouseEvent('dblclick', { bubbles: true }));
    el('rc-modal').querySelector('[data-rc-close]').dispatchEvent(new MouseEvent('click', { bubbles: true }));
    expect(el('rc-modal').hidden).toBe(true);
  });
});

describe('header theming', () => {
  it('applies an explicit manifest themeColor via CSS custom properties', async () => {
    await importShell();
    await shell.themeHeader({ themeColor: 'rgb(20, 40, 60)' });
    const header = document.querySelector('.game-header');
    expect(header.style.getPropertyValue('--gh-bg')).toBe('rgb(20, 40, 60)');
    // light-on-dark: a dark background gets white text
    expect(header.style.getPropertyValue('--gh-fg')).toBe('rgb(255, 255, 255)');
  });

  it('resetHeaderTheme removes the custom properties', async () => {
    await importShell();
    await shell.themeHeader({ themeColor: 'rgb(20, 40, 60)' });
    shell.resetHeaderTheme();
    const header = document.querySelector('.game-header');
    expect(header.style.getPropertyValue('--gh-bg')).toBe('');
  });

  it('samples the thumbnail when no themeColor is given', async () => {
    // Stub Image (fires onload) and canvas (returns a vivid-red pixel block) so the sampling path runs.
    class FakeImage {
      set src(v) { this._src = v; queueMicrotask(() => this.onload && this.onload()); }
    }
    vi.stubGlobal('Image', FakeImage);
    const redPixels = new Uint8ClampedArray(4 * 16).fill(0);
    for (let i = 0; i < redPixels.length; i += 4) { redPixels[i] = 255; redPixels[i + 3] = 255; }
    const realCreate = document.createElement.bind(document);
    vi.spyOn(document, 'createElement').mockImplementation((tag) => {
      if (tag === 'canvas') {
        return { width: 0, height: 0, getContext: () => ({ drawImage() {}, getImageData: () => ({ data: redPixels }) }) };
      }
      return realCreate(tag);
    });

    await importShell();
    await shell.themeHeader({ id: 'ttt', thumbnail: 'thumb.png' });
    const header = document.querySelector('.game-header');
    expect(header.style.getPropertyValue('--gh-bg')).toBe('rgb(255, 0, 0)');
  });

  it('applyHeaderColors sets the full set of --gh-* variables', async () => {
    await importShell();
    shell.applyHeaderColors({ r: 10, g: 20, b: 30 }, { r: 255, g: 255, b: 255 });
    const header = document.querySelector('.game-header');
    expect(header.style.getPropertyValue('--gh-bg')).toBe('rgb(10, 20, 30)');
    expect(header.style.getPropertyValue('--gh-fg-muted')).toBe('rgba(255, 255, 255, 0.65)');
    expect(header.style.getPropertyValue('--gh-btn-bg')).toBe('rgba(255, 255, 255, 0.14)');
  });
});

describe('colorToRgb (CSSOM validation)', () => {
  // The numeric/opacity edge cases are exhaustively covered by parseRgbComponents in kb-core.test.js;
  // here we only confirm the CSSOM probe accepts valid colors and rejects invalid/non-string input.
  it('normalizes valid colors (hex, named, rgb) to {r,g,b}', async () => {
    await importShell();
    expect(shell.colorToRgb('#ff8800')).toEqual({ r: 255, g: 136, b: 0 });
    expect(shell.colorToRgb('rgb(1, 2, 3)')).toEqual({ r: 1, g: 2, b: 3 });
    expect(shell.colorToRgb('rebeccapurple')).toEqual({ r: 102, g: 51, b: 153 });
  });

  it('returns null for invalid or non-string values', async () => {
    await importShell();
    expect(shell.colorToRgb('totally-bogus')).toBeNull();
    expect(shell.colorToRgb('')).toBeNull();
    expect(shell.colorToRgb(null)).toBeNull();
    expect(shell.colorToRgb('transparent')).toBeNull(); // non-opaque → unset
  });
});
