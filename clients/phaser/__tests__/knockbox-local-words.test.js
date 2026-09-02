import { describe, it, expect, beforeEach } from 'vitest';
import LocalPkg from '../knockbox-local.js';

// Local kb.words emulation (design §12a): the same authority.js that calls kb.words on the server
// must behave identically in the local dev loop, including the pick ordering.
const { KnockBoxLocalPeer, _buildLocalWords, _resetLocalHubs } = LocalPkg;

const flush = () => new Promise((resolve) => setTimeout(resolve, 0));
beforeEach(() => _resetLocalHubs());

// SHARED FIXTURE — must stay byte-identical to the C# test
// KnockBox.Server.Tests/WordPoolTests.Shared_fixture_pick_sequence_matches_the_local_emulation.
// Includes a case dupe (Dog/dog), a fold (CAT), and a non-ASCII word (café, skipped).
const FIXTURE = ['Dog', 'be', 'CAT', 'ax', 'dog', 'eel', 'café'];
const EXPECTED_ORDER = ['ax', 'be', 'cat', 'dog', 'eel']; // length asc, ordinal within

// SHARED FIXTURE for the range primitives — must stay byte-identical to the C# test
// KnockBox.Server.Tests/WordPoolTests.Shared_prefix_fixture_matches_the_local_emulation.
// Two length buckets, each with a run inside a run, so an off-by-one in either bound shows up.
const RANGE_FIXTURE = ['cat', 'cow', 'cub', 'ant', 'zip', 'cake', 'calm', 'cart', 'chip', 'able'];

describe('local kb.words — prefix ranges', () => {
  // len 3 sorted: ant cat cow cub zip     len 4 sorted: able cake calm cart chip
  const CASES = [
    [3, '', [0, 5]],
    [3, 'c', [1, 4]],
    [3, 'ca', [1, 2]],
    [3, 'z', [4, 5]],
    [4, 'c', [1, 5]],
    [4, 'ca', [1, 4]],
    [4, 'ch', [4, 5]],
    [4, 'able', [0, 1]],
  ];

  it('brackets the same words the server does (shared fixture)', () => {
    const w = _buildLocalWords({ en: RANGE_FIXTURE });
    for (const [len, prefix, expected] of CASES) {
      expect(w.rangeOfPrefix('en', len, prefix), `len ${len} prefix "${prefix}"`).toEqual(expected);
    }
  });

  it('agrees with walking the bucket by hand', () => {
    // The property a subtle binary-search bug breaks: the bounds are exactly the words a linear
    // scan would have accepted, in the same order.
    const w = _buildLocalWords({ en: RANGE_FIXTURE });
    for (const len of [3, 4]) {
      const all = [];
      for (let i = 0; i < w.countOfLength('en', len); i++) all.push(w.pickOfLength('en', len, i));
      for (const prefix of ['a', 'c', 'ca', 'ch', 'z', 'q', '']) {
        const [start, end] = w.rangeOfPrefix('en', len, prefix);
        expect(all.slice(start, end)).toEqual(all.filter((x) => x.startsWith(prefix)));
      }
    }
  });

  it('reports an empty range rather than a wrong one for a prefix with no words', () => {
    // Empty is start === end, not [0, 0]: a prefix that sorts between two runs lands at its
    // insertion point. Callers rely on `for (let i = start; i < end; i++)`, which is correct either way.
    const w = _buildLocalWords({ en: RANGE_FIXTURE });
    for (const [len, prefix] of [[3, 'q'], [9, 'c'], [3, 'cats'], [3, 'é']]) {
      const [start, end] = w.rangeOfPrefix('en', len, prefix);
      expect(start, `len ${len} prefix "${prefix}"`).toBe(end);
    }
  });

  it('folds case exactly as has() does', () => {
    expect(_buildLocalWords({ en: RANGE_FIXTURE }).rangeOfPrefix('en', 4, 'CA')).toEqual([1, 4]);
    const sensitive = _buildLocalWords({ en: { words: RANGE_FIXTURE, caseInsensitive: false } });
    const [start, end] = sensitive.rangeOfPrefix('en', 4, 'CA');
    expect(start).toBe(end);
  });

  it('returns null for an unknown dictionary or a non-string prefix, like the server', () => {
    const w = _buildLocalWords({ en: RANGE_FIXTURE });
    expect(w.rangeOfPrefix('nope', 3, 'c')).toBeNull();
    expect(w.rangeOfPrefix('en', 3, 42)).toBeNull();
  });
});

describe('local kb.words — pickRange', () => {
  it('returns the slice and clamps to the bucket', () => {
    const w = _buildLocalWords({ en: RANGE_FIXTURE });
    expect(w.pickRange('en', 4, 1, 3)).toEqual(['cake', 'calm', 'cart']);
    expect(w.pickRange('en', 4, 3, 99)).toEqual(['cart', 'chip']); // past the end yields what exists
    expect(w.pickRange('en', 4, 99, 5)).toEqual([]);
    expect(w.pickRange('en', 4, 0, 0)).toEqual([]);
    expect(w.pickRange('en', 9, 0, 5)).toEqual([]);               // no such length
    expect(w.pickRange('nope', 4, 0, 1)).toBeNull();
  });

  it('composes with rangeOfPrefix the way a module would use them', () => {
    const w = _buildLocalWords({ en: RANGE_FIXTURE });
    const [start, end] = w.rangeOfPrefix('en', 4, 'ca');
    expect(w.pickRange('en', 4, start, end - start)).toEqual(['cake', 'calm', 'cart']);
  });
});

describe('local kb.words — capability', () => {
  it('pick sequence matches the server (shared fixture)', () => {
    const w = _buildLocalWords({ en: FIXTURE });
    expect(w.count('en')).toBe(EXPECTED_ORDER.length);
    expect(EXPECTED_ORDER.map((_, i) => w.pick('en', i))).toEqual(EXPECTED_ORDER);
  });

  it('has is case-insensitive by default and rejects non-ASCII', () => {
    const w = _buildLocalWords({ en: ['apple'] });
    expect(w.has('en', 'APPLE')).toBe(true);
    expect(w.has('en', 'Apple')).toBe(true);
    expect(w.has('en', 'zzz')).toBe(false);
    expect(w.has('en', 'applé')).toBe(false);
  });

  it('case-sensitive dictionary matches exactly', () => {
    const w = _buildLocalWords({ en: { words: ['Apple'], caseInsensitive: false } });
    expect(w.has('en', 'Apple')).toBe(true);
    expect(w.has('en', 'apple')).toBe(false);
  });

  it('length-specific count and pick', () => {
    const w = _buildLocalWords({ en: FIXTURE });
    expect(w.countOfLength('en', 3)).toBe(3);
    expect(w.pickOfLength('en', 3, 0)).toBe('cat');
    expect(w.pickOfLength('en', 9, 0)).toBeNull();
  });

  it('unknown key and out-of-range index are safe', () => {
    const w = _buildLocalWords({ en: ['apple'] });
    expect(w.has('nope', 'apple')).toBe(false);
    expect(w.count('nope')).toBe(0);
    expect(w.pick('nope', 0)).toBeNull();
    expect(w.pick('en', 99)).toBeNull();
    expect(w.pick('en', -1)).toBeNull();
  });

  it('supports words longer than 64 chars (no length limit, matches the server)', () => {
    const word = 'a'.repeat(80);
    const w = _buildLocalWords({ en: [word, 'cat'] });
    expect(w.has('en', word)).toBe(true);
    expect(w.has('en', 'A'.repeat(80))).toBe(true); // case-folded
    expect(w.has('en', 'b'.repeat(80))).toBe(false);
    expect(w.countOfLength('en', 80)).toBe(1);
    expect(w.pickOfLength('en', 80, 0)).toBe(word);
    expect(w.pick('en', 1)).toBe(word); // length asc: 'cat' (3) then the 80-char word
  });
});

describe('local kb.words — over a live authority peer', () => {
  function wordAuthority(kb) {
    let state = null;
    return {
      init() { state = { total: kb.words.count('en'), valid: null, picked: null }; },
      applyIntent(fromId, action) {
        if (action.kind === 'check') { state.valid = kb.words.has('en', action.word); return { valid: state.valid }; }
        if (action.kind === 'pick') { state.picked = kb.words.pick('en', action.i); return { picked: state.picked }; }
        return null;
      },
      snapshot() { return state; },
    };
  }

  it('exposes kb.words to the real module through the actor', async () => {
    const a = new KnockBoxLocalPeer({
      mode: 'process', channel: 'w', playerId: 'a',
      authority: wordAuthority, words: { en: FIXTURE },
    });
    const msgs = [];
    a.events.on('message', (m) => msgs.push(m));
    a.start();
    await flush();

    a.sendToHost({ _kb: 'intent', action: { kind: 'check', word: 'CAT' } });
    a.sendToHost({ _kb: 'intent', action: { kind: 'pick', i: 0 } });
    await flush();

    const deltas = msgs.filter((m) => m.from === 'server' && m.payload && m.payload._kb === 'delta');
    expect(deltas.some((m) => m.payload.patch.valid === true)).toBe(true);   // 'CAT' folds to a hit
    expect(deltas.some((m) => m.payload.patch.picked === 'ax')).toBe(true);  // global index 0
  });
});
