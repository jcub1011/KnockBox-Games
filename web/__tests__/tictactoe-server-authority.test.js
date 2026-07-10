import { describe, it, expect, beforeEach } from 'vitest';
import { createAuthority, config } from '../../games/tictactoe-server/authority.js';

// Tier-1 authority-module tests (design §12a): pure — import createAuthority with a fake kb, feed
// intents, assert patches/snapshots. No server, no transport, no DOM. This fakeKb is the ~10-line
// pattern the developer guide shows: a controllable clock plus recording capability stubs.
function fakeKb() {
  const calls = { setOwner: [], setLobbyOpen: [], log: [] };
  return {
    calls,
    now: () => 1_700_000_000_000,
    setOwner: (id) => calls.setOwner.push(id),
    setLobbyOpen: (open) => calls.setLobbyOpen.push(open),
    log: {
      debug: (m) => calls.log.push(['debug', m]),
      info: (m) => calls.log.push(['info', m]),
      warn: (m) => calls.log.push(['warn', m]),
      error: (m) => calls.log.push(['error', m]),
    },
  };
}

const P = (id) => ({ id, displayName: id.toUpperCase() });

let kb, auth;

function startTwoPlayerGame() {
  kb = fakeKb();
  auth = createAuthority(kb);
  auth.init([P('x'), P('o')]);
}

const move = (from, cell) => auth.applyIntent(from, { kind: 'move', cell });

describe('config', () => {
  it('is broadcast mode with no tick', () => {
    expect(config).toEqual({});
  });
});

describe('seating & the join gate', () => {
  it('seats both initial players, starts the round, and closes the lobby', () => {
    startTwoPlayerGame();
    const s = auth.snapshot();
    expect(s.seats).toEqual(['x', 'o']);
    expect(s.board).toEqual(Array(9).fill(0));
    expect(s.next).toBe('x'); // X (the first seat) moves first
    expect(s.winner).toBeNull();
    expect(kb.calls.setLobbyOpen).toEqual([false]);
  });

  it('waits (and stays open) with a single player, then starts when the opponent joins', () => {
    kb = fakeKb();
    auth = createAuthority(kb);
    auth.init([P('x')]); // like the server: init sees the creator only
    expect(auth.snapshot().next).toBeNull();
    expect(kb.calls.setLobbyOpen).toEqual([true]);

    auth.onPlayerJoined(P('o'));
    const s = auth.snapshot();
    expect(s.seats).toEqual(['x', 'o']);
    expect(s.next).toBe('x');
    expect(kb.calls.setLobbyOpen).toEqual([true, false]);
  });
});

describe('applyIntent (the ported applyMove guards)', () => {
  beforeEach(startTwoPlayerGame);

  it('accepts a legal move: absolute patch with the mark placed and the turn flipped', () => {
    const patch = move('x', 4);
    expect(patch.board[4]).toBe(1);
    expect(patch.next).toBe('o');
    expect(patch.winner).toBeNull();
    expect(patch).toEqual(auth.snapshot()); // the patch IS the full state (absolute)
  });

  it('rejects out-of-turn, occupied, out-of-range, malformed, and non-move intents', () => {
    expect(move('o', 0)).toBeNull();               // not their turn
    move('x', 4);
    expect(move('o', 4)).toBeNull();               // occupied
    expect(move('o', 9)).toBeNull();               // out of range
    expect(move('o', -1)).toBeNull();
    expect(move('o', 1.5)).toBeNull();             // non-integer
    expect(auth.applyIntent('o', { kind: 'chat' })).toBeNull();
    expect(auth.applyIntent('o', null)).toBeNull();
    expect(auth.snapshot().board.filter((v) => v !== 0)).toHaveLength(1); // nothing else mutated
  });

  it('rejects moves while waiting for an opponent', () => {
    kb = fakeKb();
    auth = createAuthority(kb);
    auth.init([P('x')]);
    expect(move('x', 0)).toBeNull();
  });

  it('detects a win, reports it, and freezes the board', () => {
    move('x', 0); move('o', 3);
    move('x', 1); move('o', 4);
    const patch = move('x', 2); // X: 0-1-2
    expect(patch.winner).toBe('x');
    expect(patch.next).toBeNull();
    expect(move('o', 5)).toBeNull(); // game over — no further moves
    expect(kb.calls.log.some(([, m]) => m.includes('x wins'))).toBe(true);
  });

  it('detects a draw', () => {
    // x o x / x o o / o x x — full board, no line.
    move('x', 0); move('o', 1);
    move('x', 2); move('o', 4);
    move('x', 3); move('o', 5);
    move('x', 7); move('o', 6);
    const patch = move('x', 8);
    expect(patch.winner).toBe('draw');
  });
});

describe('snapshot', () => {
  it('round-trips through JSON (the server boundary is strings of JSON)', () => {
    startTwoPlayerGame();
    move('x', 4);
    const s = auth.snapshot();
    expect(JSON.parse(JSON.stringify(s))).toEqual(s);
  });
});

describe('onPlayerLeft — owner succession and round reset', () => {
  it('promotes the longest-standing member when the owner leaves (kb.setOwner)', () => {
    startTwoPlayerGame();
    auth.onPlayerLeft('x'); // x was init's first member — the owner
    expect(kb.calls.setOwner).toEqual(['o']);
  });

  it('does not touch ownership when a non-owner leaves', () => {
    startTwoPlayerGame();
    auth.onPlayerLeft('o');
    expect(kb.calls.setOwner).toEqual([]);
  });

  it('vacates the seat, resets the round, and reopens the lobby', () => {
    startTwoPlayerGame();
    move('x', 4);
    auth.onPlayerLeft('o');
    const s = auth.snapshot();
    expect(s.seats).toContain('x');
    expect(s.seats).toContain(null);
    expect(s.board).toEqual(Array(9).fill(0)); // reset — a seated player left mid-round
    expect(s.next).toBeNull();                 // waiting again
    expect(kb.calls.setLobbyOpen[kb.calls.setLobbyOpen.length - 1]).toBe(true);
  });

  it('a rejoining opponent restarts the round', () => {
    startTwoPlayerGame();
    auth.onPlayerLeft('o');
    auth.onPlayerJoined(P('o2'));
    const s = auth.snapshot();
    expect(s.seats.filter(Boolean).sort()).toEqual(['o2', 'x']);
    expect(s.next).not.toBeNull();
  });
});
