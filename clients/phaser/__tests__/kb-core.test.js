import { describe, it, expect } from 'vitest';
import KBCore from '../kb-core.js'; // UMD default export (the helpers object)

const {
  PROTOCOL_VERSION,
  TERMINAL_CLOSE_CODE,
  isTerminalClose,
  reconnectDelay,
  parseLaunchParams,
  defaultEndpoint,
  LOG_LEVELS,
  makeLogger,
  rosterAdd,
  rosterRemove,
} = KBCore;

// kb-core.js is a port of web/kb-core.js (minus the shell-only helpers). These tests pin the
// protocol behavior that MUST stay identical across the web, Godot, and Phaser clients — if any
// of these drift, two clients in the same lobby would disagree.

describe('protocol version', () => {
  it('matches the server wire version (KnockBoxProtocol.Version)', () => {
    expect(PROTOCOL_VERSION).toBe(1);
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

describe('defaultEndpoint', () => {
  it('chooses ws/wss from the page protocol', () => {
    expect(defaultEndpoint('http:', 'localhost:5115')).toBe('ws://localhost:5115/ws');
    expect(defaultEndpoint('https:', 'games.example')).toBe('wss://games.example/ws');
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

  it('exposes exactly the six level methods (identical to the web SDK) and stringifies the message', () => {
    const frames = [];
    const log = makeLogger((f) => frames.push(f));
    expect(Object.keys(log).sort()).toEqual(
      ['critical', 'debug', 'error', 'info', 'trace', 'warn'],
    );
    expect(Object.values(LOG_LEVELS)).toContain('Critical');

    log.trace(42);
    expect(frames[0]).toEqual({ type: 'Log', level: 'Trace', message: '42' });
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
