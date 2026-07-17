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
