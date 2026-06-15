import { describe, it, expect } from 'vitest';
import {
  TERMINAL_CLOSE_CODE,
  isTerminalClose,
  reconnectDelay,
  parseLaunchParams,
  defaultEndpoint,
  gameWsEndpoint,
  buildGameSrc,
  rosterAdd,
  rosterRemove,
} from '../kb-core.js';

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

describe('buildGameSrc', () => {
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
