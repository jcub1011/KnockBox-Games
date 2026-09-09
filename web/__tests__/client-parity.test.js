// Cross-client parity for the GAME-FACING protocol surface.
//
// The same protocol core is hand-maintained in three languages:
//   web/kb-protocol.js                          (ESM, the reference)
//   clients/phaser/kb-core.js                   (UMD)
//   clients/godot/addons/knockbox/kb_core.gd    (GDScript)
//
// Nothing structural stops one from gaining a helper the others never get, and that has already
// happened: the server-authority owner contract landed in web/ and clients/phaser/ and never
// reached the Godot addon, so `normalizeReady` — the function that reads `authority`/`ownerId` —
// exists in two of the three. A Godot game therefore cannot see who the lobby owner is.
//
// These tests compare the DECLARED NAMES, not behaviour. That's the drift a reviewer misses:
// behaviour differences show up as failing feature tests in the port that has the feature, while a
// missing export is simply invisible until a game developer needs it.
//
// Parsed as text on purpose. The three files are ESM, UMD and GDScript; importing them uniformly
// is not possible (the UMD wrapper leans on a CommonJS `this`, and GDScript has no JS loader), and
// a name comparison doesn't need evaluation.
import { describe, it, expect } from 'vitest';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const repoRoot = join(dirname(fileURLToPath(import.meta.url)), '..', '..');
const read = (...parts) => readFileSync(join(repoRoot, ...parts), 'utf8');

// Helpers the Godot addon does not have yet. Tracked here rather than by loosening the comparison,
// so the gap is a written-down debt with a name instead of a silence.
//
// `LOG_LEVELS`/`makeLogger` mean a Godot game cannot use KnockBox.log.*; `normalizeReady` means it
// cannot see `authority`, `ownerId` or `isOwner`. Closing these is the tracked Godot-parity task
// (docs/SERVER_AUTHORITY_DESIGN.md: "Godot addon gets the same treatment as a parity follow-up").
// When one lands, delete it from this list — the last test in this file fails if you don't.
const KNOWN_GODOT_GAPS = ['LOG_LEVELS', 'blobBaseUrl', 'makeLogger', 'normalizeReady', 'sha256Hex'];

/** `export const NAME` / `export function name(` — the reference surface. */
function esmExports(source) {
  return [...source.matchAll(/^export (?:const|(?:async\s+)?function)\s+([A-Za-z0-9_$]+)/gm)].map((m) => m[1]);
}

/**
 * The UMD factory's returned object literal (`return { name: name, ... };`). Read from the LAST
 * `return {` block so the small object literals returned by inner helpers can't be mistaken for it.
 */
function umdExports(source) {
  const start = source.lastIndexOf('return {');
  if (start < 0) return [];
  const block = source.slice(start, source.indexOf('};', start));
  return [...block.matchAll(/^\s{4}([A-Za-z0-9_$]+):/gm)].map((m) => m[1]);
}

/** Top-level `const NAME` and `static func name(` in a GDScript file. */
function gdscriptMembers(source) {
  return [
    ...[...source.matchAll(/^const\s+([A-Za-z0-9_]+)/gm)].map((m) => m[1]),
    ...[...source.matchAll(/^static func\s+([A-Za-z0-9_]+)/gm)].map((m) => m[1]),
  ];
}

/** camelCase -> snake_case, leaving CONSTANT_CASE alone (GDScript keeps those verbatim). */
function toSnake(name) {
  if (name === name.toUpperCase()) return name;
  return name.replace(/[A-Z]/g, (c) => `_${c.toLowerCase()}`);
}

const reference = esmExports(read('web', 'kb-protocol.js'));
const phaser = umdExports(read('clients', 'phaser', 'kb-core.js'));
const godot = gdscriptMembers(read('clients', 'godot', 'addons', 'knockbox', 'kb_core.gd'));

describe('web/kb-protocol.js is the reference surface', () => {
  it('exports exactly the game-facing helpers, and nothing shell-only', () => {
    // Pinned deliberately: kb-protocol.js is what ships to every game, so a symbol arriving here
    // is a decision, not a drive-by. Shell-only helpers belong in kb-core.js.
    expect([...reference].sort()).toEqual([
      'LOG_LEVELS',
      'PROTOCOL_VERSION',
      'TERMINAL_CLOSE_CODE',
      'blobBaseUrl',
      'defaultEndpoint',
      'isTerminalClose',
      'makeLogger',
      'normalizeReady',
      'parseLaunchParams',
      'reconnectDelay',
      'rosterAdd',
      'rosterRemove',
      'sha256Hex',
    ]);
  });

  it('does not re-export the shell-only helpers that used to ride along', () => {
    for (const shellOnly of ['FAVICONS', 'launchMessage', 'appendPlayLog', 'announcementText',
      'dominantColorFromPixels', 'buildGameSrc', 'debounce']) {
      expect(reference).not.toContain(shellOnly);
    }
  });
});

describe('clients/phaser/kb-core.js', () => {
  it('exposes the same names as the reference', () => {
    expect([...phaser].sort()).toEqual([...reference].sort());
  });
});

describe('clients/godot/addons/knockbox/kb_core.gd', () => {
  it('exposes the reference surface in snake_case, minus the known gaps', () => {
    const expected = reference
      .filter((name) => !KNOWN_GODOT_GAPS.includes(name))
      .map(toSnake)
      .sort();
    expect([...godot].sort()).toEqual(expected);
  });

  it('has no member the reference lacks', () => {
    const referenceSnake = reference.map(toSnake);
    for (const member of godot) expect(referenceSnake).toContain(member);
  });

  // Keeps the allowlist honest in the direction that would otherwise rot silently: once the Godot
  // port gains one of these, an entry left behind here would quietly permit a NEW regression under
  // the same name.
  it('is still missing every helper the allowlist claims, or the allowlist is stale', () => {
    for (const gap of KNOWN_GODOT_GAPS) {
      expect(godot, `kb_core.gd now has '${toSnake(gap)}' — remove '${gap}' from KNOWN_GODOT_GAPS`)
        .not.toContain(toSnake(gap));
    }
  });
});
