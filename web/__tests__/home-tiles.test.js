// Home-page tile sizing guardrails.
//
// The tiles once stopped shrinking on narrow phones: as a grid item, `.game-tile` (a
// <button>) defaulted to min-width: auto, i.e. its min-content width, so author-controlled
// chin content (unvalidated tags/version strings, the nowrap capacity badge) forced the
// track wider than the viewport — with body overflow-x hidden masking the overflow as a
// "floor" instead of a sideways scroll. jsdom does no layout, so these tests pin the CSS
// declarations that keep tiles floorless, parsed as text (the client-parity.test.js pattern).
import { describe, it, expect } from 'vitest';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const css = readFileSync(
  join(dirname(fileURLToPath(import.meta.url)), '..', 'home.css'),
  'utf8',
);

/** First `.selector { ... }` block — the base rule, which precedes any pseudo/compound variants. */
function baseRule(selector) {
  const escaped = selector.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const match = css.match(new RegExp(`${escaped}\\s*\\{([^}]*)\\}`));
  expect(match, `expected a ${selector} rule in home.css`).not.toBeNull();
  return match[1];
}

/** The `@media ...` section from its marker up to (but excluding) the next one. */
function mediaBlock(marker) {
  const start = css.indexOf(marker);
  expect(start, `expected "${marker}" in home.css`).toBeGreaterThanOrEqual(0);
  const next = css.indexOf('@media', start + marker.length);
  return css.slice(start, next < 0 ? undefined : next);
}

describe('game tiles have no minimum width', () => {
  it('.game-tile defeats the grid automatic minimum', () => {
    expect(baseRule('.game-tile')).toMatch(/min-width:\s*0/);
  });

  it('.game-tile-surface does not propagate chin min-content past the tile', () => {
    expect(baseRule('.game-tile-surface')).toMatch(/min-width:\s*0/);
  });

  it('the desktop grid keeps its 240px column floor', () => {
    // Deliberate: only narrow screens go floorless; desktop keeps minmax(240px, 1fr).
    expect(baseRule('.game-grid')).toMatch(/minmax\(240px,\s*1fr\)/);
  });

  it('narrow phones still get a single column', () => {
    expect(mediaBlock('@media (max-width: 600px)')).toMatch(
      /\.game-grid\s*\{[^}]*grid-template-columns:\s*1fr/,
    );
  });
});

describe('very narrow phones shed the chin chips', () => {
  const block = mediaBlock('@media (max-width: 480px)');

  it('hides the genre chips', () => {
    expect(block).toMatch(/\.game-chin-tag\s*\{[^}]*display:\s*none/);
  });

  it('keeps the version chip visible, ordered after the hiding rule', () => {
    // Equal specificity, so source order decides: the re-show must come second.
    const hideAt = block.search(/\.game-chin-tag\s*\{[^}]*display:\s*none/);
    const showAt = block.search(/\.game-chin-tag-version\s*\{[^}]*display:\s*inline-block/);
    expect(showAt).toBeGreaterThan(hideAt);
  });

  it('shrinks the no-art fallback name', () => {
    expect(block).toMatch(/\.game-tile-fallback\s*\{[^}]*font-size:/);
  });
});
