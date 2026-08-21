import { describe, it, expect, vi, afterEach } from 'vitest';
import {
  TERMINAL_CLOSE_CODE,
  isTerminalClose,
  debounce,
  reconnectDelay,
  parseLaunchParams,
  parseJoinParam,
  defaultEndpoint,
  gameWsEndpoint,
  sanitizeGameOrigin,
  buildGameSrc,
  buildJoinLink,
  launchFlipFrom,
  launchMessage,
  luminance,
  pickContrastText,
  dominantColorFromPixels,
  parseRgbComponents,
  LOG_LEVELS,
  makeLogger,
  normalizeReady,
  rosterAdd,
  rosterRemove,
  rotationFromMatrix,
  appendPlayLog,
  PLAY_LOG_MAX,
  PLAY_LOG_STANDARD_KEYS,
  partitionPlayLogMetadata,
  ordinal,
  FAVICONS,
  pickRandomFavicon,
  shouldShowAnnouncement,
  announcementSeverity,
  announcementText,
} from '../kb-core.js';

describe('normalizeReady', () => {
  it('derives owner fields for an old-server host (no authority/ownerId on the wire)', () => {
    const info = normalizeReady({ playerId: 'me', players: [{ id: 'me' }], isHost: true });
    expect(info).toEqual({
      playerId: 'me', players: [{ id: 'me' }], isHost: true,
      authority: 'host', ownerId: 'me', isOwner: true,
    });
  });

  it('leaves an old-server guest ownerless (the owner is unknowable)', () => {
    const info = normalizeReady({ playerId: 'me', players: [], isHost: false });
    expect(info.authority).toBe('host');
    expect(info.ownerId).toBeNull();
    expect(info.isOwner).toBe(false);
  });

  it('passes server-mode fields through and derives isOwner from ownerId', () => {
    const guest = normalizeReady({ playerId: 'me', players: [], isHost: false, authority: 'server', ownerId: 'other' });
    expect(guest).toMatchObject({ authority: 'server', ownerId: 'other', isOwner: false });

    const owner = normalizeReady({ playerId: 'me', players: [], isHost: false, authority: 'server', ownerId: 'me' });
    expect(owner).toMatchObject({ isHost: false, isOwner: true }); // owner ≠ host in server mode
  });

  it('keeps explicit host-mode fields from a new server untouched', () => {
    const info = normalizeReady({ playerId: 'g', players: [], isHost: false, authority: 'host', ownerId: 'h' });
    expect(info).toMatchObject({ authority: 'host', ownerId: 'h', isOwner: false });
  });

  it('defaults a missing players list to empty', () => {
    expect(normalizeReady({ playerId: 'me', isHost: false }).players).toEqual([]);
  });
});

describe('isTerminalClose', () => {
  it('treats the policy-violation code as terminal (invalid ticket / membership ended)', () => {
    expect(isTerminalClose(TERMINAL_CLOSE_CODE)).toBe(true);
    expect(isTerminalClose(1008)).toBe(true);
  });

  it('treats abnormal/normal closes as transient (worth reconnecting)', () => {
    expect(isTerminalClose(1006)).toBe(false); // abnormal (server restart, network blip)
    expect(isTerminalClose(1000)).toBe(false); // normal
  });
});

describe('reconnectDelay', () => {
  it('grows exponentially from the base', () => {
    expect(reconnectDelay(0)).toBe(1000);
    expect(reconnectDelay(1)).toBe(2000);
    expect(reconnectDelay(2)).toBe(4000);
    expect(reconnectDelay(3)).toBe(8000);
  });

  it('is capped at the max so it never runs away', () => {
    expect(reconnectDelay(100)).toBe(30000);
    expect(reconnectDelay(10, 1000, 5000)).toBe(5000);
  });

  it('clamps negative/garbage attempts to the base', () => {
    expect(reconnectDelay(-5)).toBe(1000);
  });
});

describe('debounce', () => {
  afterEach(() => vi.useRealTimers());

  it('collapses a burst of calls into one trailing invocation after the delay', () => {
    vi.useFakeTimers();
    const fn = vi.fn();
    const d = debounce(fn, 250);

    d(); d(); d();
    expect(fn).not.toHaveBeenCalled();      // nothing fires mid-burst
    vi.advanceTimersByTime(249);
    expect(fn).not.toHaveBeenCalled();      // still within the window
    vi.advanceTimersByTime(1);
    expect(fn).toHaveBeenCalledTimes(1);    // exactly one call after the delay
  });

  it('resets the timer on each call (only quiet time counts)', () => {
    vi.useFakeTimers();
    const fn = vi.fn();
    const d = debounce(fn, 250);

    d();
    vi.advanceTimersByTime(200);
    d();                                    // restarts the 250ms window
    vi.advanceTimersByTime(200);
    expect(fn).not.toHaveBeenCalled();      // 400ms elapsed but never 250ms quiet
    vi.advanceTimersByTime(50);
    expect(fn).toHaveBeenCalledTimes(1);
  });

  it('passes the latest arguments through to the wrapped fn', () => {
    vi.useFakeTimers();
    const fn = vi.fn();
    const d = debounce(fn, 250);

    d('a'); d('b');
    vi.advanceTimersByTime(250);
    expect(fn).toHaveBeenCalledWith('b');
  });

  it('cancel() drops a pending invocation', () => {
    vi.useFakeTimers();
    const fn = vi.fn();
    const d = debounce(fn, 250);

    d();
    d.cancel();
    vi.advanceTimersByTime(250);
    expect(fn).not.toHaveBeenCalled();
  });

  it('cancel() is safe when nothing is pending', () => {
    vi.useFakeTimers();
    const fn = vi.fn();
    const d = debounce(fn, 250);

    expect(() => d.cancel()).not.toThrow();
    d();
    vi.advanceTimersByTime(250);
    expect(fn).toHaveBeenCalledTimes(1);     // still works after a no-op cancel
  });
});

describe('pickRandomFavicon', () => {
  it('maps the random value across the list (0 → first, ~1 → last)', () => {
    expect(pickRandomFavicon(FAVICONS, () => 0)).toBe(FAVICONS[0]);
    expect(pickRandomFavicon(FAVICONS, () => 0.99)).toBe(FAVICONS[FAVICONS.length - 1]);
  });

  it('always returns a member of the list', () => {
    for (let i = 0; i < FAVICONS.length; i++) {
      const picked = pickRandomFavicon(FAVICONS, () => i / FAVICONS.length);
      expect(FAVICONS).toContain(picked);
    }
  });

  it('returns null for an empty/missing list', () => {
    expect(pickRandomFavicon([], () => 0)).toBeNull();
    expect(pickRandomFavicon(null, () => 0)).toBeNull();
  });
});

describe('parseLaunchParams', () => {
  it('reads credentials from the URL fragment', () => {
    const { ticket, endpoint } = parseLaunchParams('#kbTicket=abc.def&kbEndpoint=ws%3A%2F%2Fh%2Fws');
    expect(ticket).toBe('abc.def');
    expect(endpoint).toBe('ws://h/ws');
  });

  it('tolerates a missing leading hash and missing params', () => {
    expect(parseLaunchParams('kbTicket=xyz').ticket).toBe('xyz');
    expect(parseLaunchParams('').ticket).toBeNull();
    expect(parseLaunchParams(undefined).endpoint).toBeNull();
  });
});

describe('parseJoinParam', () => {
  it('reads and normalizes the auto-join code from the query string', () => {
    expect(parseJoinParam('?join=ab12')).toBe('AB12');
    expect(parseJoinParam('join=ab12')).toBe('AB12'); // leading "?" is optional for URLSearchParams
    expect(parseJoinParam('?name=Bob&join=cd34')).toBe('CD34');
    expect(parseJoinParam('?join=%20ab12%20')).toBe('AB12'); // trims surrounding space
  });

  it('returns null when the code is absent or blank', () => {
    expect(parseJoinParam('')).toBeNull();
    expect(parseJoinParam(undefined)).toBeNull();
    expect(parseJoinParam('?foo=bar')).toBeNull();
    expect(parseJoinParam('?join=')).toBeNull();
    expect(parseJoinParam('?join=%20%20')).toBeNull();
  });
});

describe('endpoint helpers', () => {
  it('chooses ws/wss from the page protocol', () => {
    expect(defaultEndpoint('http:', 'localhost:5115')).toBe('ws://localhost:5115/ws');
    expect(defaultEndpoint('https:', 'games.example')).toBe('wss://games.example/ws');
  });

  it('derives the data-socket endpoint from a game origin', () => {
    expect(gameWsEndpoint('http://localhost:5115')).toBe('ws://localhost:5115/ws');
    expect(gameWsEndpoint('https://games.example')).toBe('wss://games.example/ws');
  });
});

describe('sanitizeGameOrigin', () => {
  it('returns the normalized origin for valid http(s) URLs', () => {
    expect(sanitizeGameOrigin('http://localhost:5115')).toBe('http://localhost:5115');
    expect(sanitizeGameOrigin('https://games.example.com')).toBe('https://games.example.com');
    // Strips any path/query/fragment down to the bare origin.
    expect(sanitizeGameOrigin('https://games.example.com/foo?x=1#y')).toBe('https://games.example.com');
    // Strips userinfo when normalizing the origin.
    expect(sanitizeGameOrigin('http://user:pass@games.example.com')).toBe('http://games.example.com');
  });

  it('rejects non-http(s) schemes that could become an XSS/redirect sink', () => {
    expect(sanitizeGameOrigin('javascript:alert(1)')).toBeNull();
    expect(sanitizeGameOrigin('data:text/html,<script>1</script>')).toBeNull();
    expect(sanitizeGameOrigin('file:///etc/passwd')).toBeNull();
  });

  it('rejects malformed / empty values', () => {
    expect(sanitizeGameOrigin('not a url')).toBeNull();
    expect(sanitizeGameOrigin('')).toBeNull();
    expect(sanitizeGameOrigin(null)).toBeNull();
    expect(sanitizeGameOrigin(undefined)).toBeNull();
  });
});

describe('buildGameSrc', () => {
  it('throws on an origin that is not a valid http(s) origin (XSS/redirect guard)', () => {
    expect(() => buildGameSrc('javascript:alert(1)', 'g', 'index.html', 't', 'ws://x/ws')).toThrow();
    expect(() => buildGameSrc('not a url', 'g', 'index.html', 't', 'ws://x/ws')).toThrow();
  });

  it('puts the ticket in the fragment, not the query string', () => {
    const src = buildGameSrc('http://localhost:5115', 'ttt', 'index.html', 'tok+/=', 'ws://localhost:5115/ws');
    expect(src.startsWith('http://localhost:5115/games/ttt/index.html#')).toBe(true);
    expect(src.includes('?')).toBe(false); // never a query string — that would leak via Referer/logs
    // The ticket is URL-encoded in the fragment.
    const frag = src.split('#')[1];
    const params = new URLSearchParams(frag);
    expect(params.get('kbTicket')).toBe('tok+/=');
    expect(params.get('kbEndpoint')).toBe('ws://localhost:5115/ws');
  });

  it('percent-encodes the gameId so a hostile id cannot inject path/scheme', () => {
    const src = buildGameSrc('http://localhost:5115', '../../evil', 'index.html', 't', 'ws://x/ws');
    expect(src.startsWith('http://localhost:5115/games/..%2F..%2Fevil/index.html#')).toBe(true);
  });

  it('encodes each entry segment but preserves nested paths', () => {
    const src = buildGameSrc('http://localhost:5115', 'g', 'build/a b.html', 't', 'ws://x/ws');
    expect(src.startsWith('http://localhost:5115/games/g/build/a%20b.html#')).toBe(true);
  });
});

describe('makeLogger', () => {
  it('emits a Log frame per console-like method, mapping to the wire LogLevel name', () => {
    const frames = [];
    const log = makeLogger((f) => frames.push(f));

    log.info('hello');
    log.warn('careful');
    log.error('boom');

    expect(frames).toEqual([
      { type: 'Log', level: 'Information', message: 'hello' },
      { type: 'Log', level: 'Warning', message: 'careful' },
      { type: 'Log', level: 'Error', message: 'boom' },
    ]);
  });

  it('exposes exactly the six level methods and stringifies the message', () => {
    const frames = [];
    const log = makeLogger((f) => frames.push(f));
    expect(Object.keys(log).sort()).toEqual(
      ['critical', 'debug', 'error', 'info', 'trace', 'warn'],
    );
    // levels map to the LogLevel names the server's string-enum converter accepts.
    expect(Object.values(LOG_LEVELS)).toContain('Critical');

    log.debug(42);
    expect(frames[0]).toEqual({ type: 'Log', level: 'Debug', message: '42' });
  });
});

describe('roster reducers', () => {
  const ann = { id: 'p1', displayName: 'Ann' };
  const bob = { id: 'p2', displayName: 'Bob' };

  it('rosterAdd is idempotent by id and immutable', () => {
    const one = rosterAdd([ann], bob);
    expect(one).toEqual([ann, bob]);
    const again = rosterAdd(one, { id: 'p2', displayName: 'Bob (dup)' });
    expect(again).toBe(one); // unchanged reference when already present
  });

  it('rosterRemove drops by id and is immutable', () => {
    const before = [ann, bob];
    const after = rosterRemove(before, 'p1');
    expect(after).toEqual([bob]);
    expect(before).toEqual([ann, bob]); // original untouched
  });
});

// ── Header-theming color math (pure; shell.js delegates the heavy lifting here) ──

describe('luminance', () => {
  it('is 0 for black and 1 for white', () => {
    expect(luminance({ r: 0, g: 0, b: 0 })).toBe(0);
    expect(luminance({ r: 255, g: 255, b: 255 })).toBeCloseTo(1, 5);
  });

  it('weights green more heavily than red or blue (WCAG coefficients)', () => {
    const green = luminance({ r: 0, g: 255, b: 0 });
    const red = luminance({ r: 255, g: 0, b: 0 });
    const blue = luminance({ r: 0, g: 0, b: 255 });
    expect(green).toBeGreaterThan(red);
    expect(red).toBeGreaterThan(blue);
  });
});

describe('pickContrastText', () => {
  it('uses near-black text on light backgrounds', () => {
    expect(pickContrastText({ r: 255, g: 255, b: 255 })).toEqual({ r: 26, g: 26, b: 26 });
  });

  it('uses white text on dark backgrounds', () => {
    expect(pickContrastText({ r: 0, g: 0, b: 0 })).toEqual({ r: 255, g: 255, b: 255 });
  });
});

describe('dominantColorFromPixels', () => {
  // Build a flat RGBA array (the shape of canvas getImageData().data) from [r,g,b,a?] tuples.
  const pixels = (...rgba) => rgba.flatMap(([r, g, b, a = 255]) => [r, g, b, a]);

  it('returns null when nothing usable remains', () => {
    expect(dominantColorFromPixels([])).toBeNull();
    expect(dominantColorFromPixels(pixels([200, 0, 0, 0]))).toBeNull();      // transparent
    expect(dominantColorFromPixels(pixels([255, 250, 248, 255]))).toBeNull(); // near-white
    expect(dominantColorFromPixels(pixels([5, 2, 0, 255]))).toBeNull();       // near-black
  });

  it('lets a vibrant accent outweigh more numerous flat-gray pixels', () => {
    // Three gray pixels (saturation 0 → weight 1 each = 3) vs one pure-red pixel (saturation 1 →
    // weight 4). The red bucket wins despite being outnumbered.
    const data = pixels(
      [128, 128, 128], [128, 128, 128], [128, 128, 128],
      [255, 0, 0],
    );
    expect(dominantColorFromPixels(data)).toEqual({ r: 255, g: 0, b: 0 });
  });

  it('averages pixels that fall in the same 5-bit bucket', () => {
    // 255,0,0 and 250,2,1 share a bucket (top 5 bits per channel match); result is their weighted mean.
    const out = dominantColorFromPixels(pixels([255, 0, 0], [250, 2, 1]));
    expect(out.r).toBeGreaterThan(248);
    expect(out.r).toBeLessThanOrEqual(255);
    expect(out.g).toBeLessThan(3);
  });
});

describe('parseRgbComponents', () => {
  it('parses rgb() and fully-opaque rgba() into numeric channels', () => {
    expect(parseRgbComponents('rgb(255, 0, 128)')).toEqual({ r: 255, g: 0, b: 128 });
    expect(parseRgbComponents('rgba(10, 20, 30, 1)')).toEqual({ r: 10, g: 20, b: 30 });
  });

  it('rejects non-opaque colors so theming falls back instead of painting a wrong tint', () => {
    expect(parseRgbComponents('rgba(0, 0, 0, 0)')).toBeNull();   // transparent
    expect(parseRgbComponents('rgba(10, 20, 30, 0.5)')).toBeNull();
  });

  it('handles the modern space-separated rgb(r g b / a) form, including percentage alpha', () => {
    expect(parseRgbComponents('rgb(255 0 128 / 50%)')).toBeNull();  // 50% → non-opaque
    expect(parseRgbComponents('rgb(255 0 128 / 0.5)')).toBeNull();  // 0.5 → non-opaque
    expect(parseRgbComponents('rgb(255 0 128 / 100%)')).toEqual({ r: 255, g: 0, b: 128 });
    expect(parseRgbComponents('rgb(255 0 128)')).toEqual({ r: 255, g: 0, b: 128 });
  });

  it('returns null for garbage / missing channels', () => {
    expect(parseRgbComponents('')).toBeNull();
    expect(parseRgbComponents(null)).toBeNull();
    expect(parseRgbComponents('not-a-color')).toBeNull();
    expect(parseRgbComponents('rgb(1, 2)')).toBeNull();
  });
});

describe('buildJoinLink', () => {
  it('builds an auto-join URL from the origin and room code', () => {
    expect(buildJoinLink('https://kb.example', 'AB12')).toBe('https://kb.example/?join=AB12');
  });

  it('percent-encodes the code', () => {
    expect(buildJoinLink('http://localhost:5114', 'A B')).toBe('http://localhost:5114/?join=A%20B');
  });
});

describe('launchMessage', () => {
  it('names the game being started', () => {
    expect(launchMessage('Tic Tac Toe')).toBe('Starting Tic Tac Toe…');
  });

  it('trims surrounding whitespace out of the name', () => {
    expect(launchMessage('  Quiplash  ')).toBe('Starting Quiplash…');
  });

  // Join-by-code doesn't know the game until EnterGame lands, so there must be a generic label
  // rather than "Starting …" with a hole in it.
  it('falls back to a generic label when the game is not known yet', () => {
    expect(launchMessage(null)).toBe('Starting game…');
    expect(launchMessage(undefined)).toBe('Starting game…');
    expect(launchMessage('')).toBe('Starting game…');
    expect(launchMessage('   ')).toBe('Starting game…');
  });

  it('ignores a non-string name instead of interpolating it', () => {
    expect(launchMessage(42)).toBe('Starting game…');
    expect(launchMessage({})).toBe('Starting game…');
  });
});

describe('launchFlipFrom', () => {
  // The centred launch tile, 300x200 in a 1000x800 viewport.
  const dest = { left: 350, top: 300, width: 300, height: 200 };

  it('maps the destination back onto the source tile', () => {
    // A grid tile up and to the left, 240x160.
    const flip = launchFlipFrom({ left: 60, top: 100, width: 240, height: 160 }, dest);
    expect(flip.dx).toBe((60 + 120) - (350 + 150));   // -320
    expect(flip.dy).toBe((100 + 80) - (300 + 100));   // -220
    expect(flip.scale).toBe(0.8);
  });

  it('is the identity when the source is already the destination', () => {
    expect(launchFlipFrom({ ...dest }, dest)).toEqual({ dx: 0, dy: 0, scale: 1 });
  });

  // A degenerate rect is a normal outcome — a tile scrolled out of view, or jsdom, where every
  // getBoundingClientRect is zeros. The caller falls back to arriving in place.
  it('declines a degenerate rect rather than producing NaN', () => {
    expect(launchFlipFrom({ left: 0, top: 0, width: 0, height: 0 }, dest)).toBe(null);
    expect(launchFlipFrom({ left: 0, top: 0, width: 240, height: 0 }, dest)).toBe(null);
    expect(launchFlipFrom({ left: 0, top: 0, width: 240, height: 160 }, { ...dest, width: 0 })).toBe(null);
    expect(launchFlipFrom(null, dest)).toBe(null);
    expect(launchFlipFrom({ ...dest }, null)).toBe(null);
  });
});

describe('rotationFromMatrix', () => {
  it('reads the angle out of a 2D matrix', () => {
    // rotate(-1deg): cos(-1°)=0.999848, sin(-1°)=-0.017452
    expect(rotationFromMatrix('matrix(0.999848, -0.0174524, 0.0174524, 0.999848, 0, 0)'))
      .toBeCloseTo(-1, 4);
    expect(rotationFromMatrix('matrix(0, 1, -1, 0, 0, 0)')).toBeCloseTo(90, 6);
  });

  it('reads the angle out of a matrix3d as well', () => {
    expect(rotationFromMatrix('matrix3d(0.999848, -0.0174524, 0, 0, 0.0174524, 0.999848, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1)'))
      .toBeCloseTo(-1, 4);
  });

  // An unrotated tile, or anything unparseable: the flourish is skipped, never a broken transform.
  it('falls back to square for an unrotated or unreadable transform', () => {
    expect(rotationFromMatrix('none')).toBe(0);
    expect(rotationFromMatrix('')).toBe(0);
    expect(rotationFromMatrix(null)).toBe(0);
    expect(rotationFromMatrix(undefined)).toBe(0);
    expect(rotationFromMatrix('matrix(nonsense)')).toBe(0);
  });
});

describe('appendPlayLog', () => {
  it('prepends the newest entry (most-recent-first)', () => {
    const out = appendPlayLog([{ id: 1 }], { id: 2 });
    expect(out).toEqual([{ id: 2 }, { id: 1 }]);
  });

  it('truncates to the cap, dropping the oldest', () => {
    const list = Array.from({ length: PLAY_LOG_MAX }, (_, i) => ({ id: i }));
    const out = appendPlayLog(list, { id: 'new' });
    expect(out).toHaveLength(PLAY_LOG_MAX);
    expect(out[0]).toEqual({ id: 'new' });
    expect(out[out.length - 1]).toEqual({ id: PLAY_LOG_MAX - 2 }); // the last old one fell off
  });

  it('honours a custom max and treats a non-array list as empty', () => {
    expect(appendPlayLog([{ a: 1 }, { a: 2 }], { a: 3 }, 2)).toEqual([{ a: 3 }, { a: 1 }]);
    expect(appendPlayLog(null, { a: 1 })).toEqual([{ a: 1 }]);
  });
});

describe('partitionPlayLogMetadata', () => {
  it('splits recognized standard keys (in display order) from arbitrary extras', () => {
    const { standard, extra } = partitionPlayLogMetadata({
      foo: 'bar', playerCount: '4', placement: '1', zed: 'z',
    });
    // standard keys come back in PLAY_LOG_STANDARD_KEYS order, not insertion order
    expect(standard).toEqual([['placement', '1'], ['playerCount', '4']]);
    expect(extra).toEqual([['foo', 'bar'], ['zed', 'z']]);
    expect(PLAY_LOG_STANDARD_KEYS).toContain('placement');
  });

  it('returns empty arrays for a missing/non-object bag', () => {
    expect(partitionPlayLogMetadata(undefined)).toEqual({ standard: [], extra: [] });
    expect(partitionPlayLogMetadata(null)).toEqual({ standard: [], extra: [] });
  });
});

describe('ordinal', () => {
  it('formats the common cases', () => {
    expect(ordinal(1)).toBe('1st');
    expect(ordinal(2)).toBe('2nd');
    expect(ordinal(3)).toBe('3rd');
    expect(ordinal(4)).toBe('4th');
    expect(ordinal('1')).toBe('1st'); // numeric strings (the wire form) work too
  });

  it('handles the 11–13 exceptions', () => {
    expect(ordinal(11)).toBe('11th');
    expect(ordinal(12)).toBe('12th');
    expect(ordinal(13)).toBe('13th');
    expect(ordinal(21)).toBe('21st');
    expect(ordinal(112)).toBe('112th');
  });

  it('returns non-numeric input unchanged', () => {
    expect(ordinal('win')).toBe('win');
    expect(ordinal('1.5')).toBe('1.5');
  });
});

describe('announcements', () => {
  it('shows a non-empty announcement the player has not dismissed', () => {
    const a = { id: 'a1', text: 'Maintenance soon' };
    expect(shouldShowAnnouncement(a, null)).toBe(true);
    expect(shouldShowAnnouncement(a, 'a0')).toBe(true);
    expect(shouldShowAnnouncement(a, 'a1')).toBe(false);
  });

  it('shows nothing for a missing or blank announcement', () => {
    expect(shouldShowAnnouncement(null, null)).toBe(false);
    expect(shouldShowAnnouncement({ id: 'a1', text: '   ' }, null)).toBe(false);
    expect(shouldShowAnnouncement({ id: 'a1' }, null)).toBe(false);
  });

  it('validates severity to a known value, because it becomes a class name', () => {
    expect(announcementSeverity('warning')).toBe('warning');
    expect(announcementSeverity('info')).toBe('info');
    for (const bad of ['critical', '', null, undefined, 'info warning', 42]) {
      expect(announcementSeverity(bad)).toBe('info');
    }
  });

  it('prefixes a game-scoped announcement with the game title', () => {
    const a = { id: 'a1', text: 'retires on the 15th.' };
    expect(announcementText(a, 'Word Rush')).toBe('Word Rush: retires on the 15th.');
    // No name resolved (a game since uninstalled) → the text alone, never a raw id.
    expect(announcementText(a, null)).toBe('retires on the 15th.');
    expect(announcementText(a, '  ')).toBe('retires on the 15th.');
  });
});
