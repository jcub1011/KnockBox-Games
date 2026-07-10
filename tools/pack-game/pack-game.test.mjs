import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { tmpdir } from "node:os";
import {
  AUTHORITY_MAX_SCRIPT_BYTES,
  pack,
  PackError,
  scanAuthorityImports,
  validate,
} from "./pack-game.mjs";

let work; // a fresh temp workspace per test: { root, src, meta, out }

beforeEach(() => {
  const root = mkdtempSync(join(tmpdir(), "kb-pack-"));
  const src = join(root, "dist");
  const meta = join(root, "export");
  const out = join(root, "games");
  mkdirSync(src, { recursive: true });
  mkdirSync(meta, { recursive: true });
  // A minimal built game + metadata laid out like Alpha-Chain (manifest/thumb in export/).
  writeFileSync(join(src, "index.html"), "<!doctype html><title>x</title>");
  writeFileSync(join(meta, "thumb.svg"), "<svg/>");
  work = { root, src, meta, out };
});

afterEach(() => rmSync(work.root, { recursive: true, force: true }));

/** Write export/GAME.json with the given manifest object and return its path. */
function manifest(obj) {
  const p = join(work.meta, "GAME.json");
  writeFileSync(p, JSON.stringify(obj));
  return p;
}

const VALID = { id: "demo", name: "Demo", entry: "index.html", thumbnail: "thumb.svg", maxPlayers: 4 };

describe("pack (happy path)", () => {
  it("assembles <out>/<id>/ with built files, manifest, and thumbnail", async () => {
    const manifestPath = manifest(VALID);
    const { target } = await pack({ in: work.src, manifest: manifestPath, out: work.out });

    expect(target).toBe(join(work.out, "demo"));
    expect(existsSync(join(target, "index.html"))).toBe(true);
    expect(existsSync(join(target, "GAME.json"))).toBe(true);
    expect(existsSync(join(target, "thumb.svg"))).toBe(true);
    // Manifest is copied verbatim.
    expect(JSON.parse(readFileSync(join(target, "GAME.json"), "utf8"))).toEqual(VALID);
  });

  it("works with no thumbnail declared", async () => {
    const { id, name, entry, maxPlayers } = VALID;
    const { target } = await pack({ in: work.src, manifest: manifest({ id, name, entry, maxPlayers }), out: work.out });
    expect(existsSync(join(target, "index.html"))).toBe(true);
    expect(existsSync(join(target, "thumb.svg"))).toBe(false);
  });

  it("re-packs idempotently, removing stale files from a prior pack", async () => {
    const manifestPath = manifest(VALID);
    const first = await pack({ in: work.src, manifest: manifestPath, out: work.out });
    writeFileSync(join(first.target, "stale.txt"), "old"); // simulate a leftover
    await pack({ in: work.src, manifest: manifestPath, out: work.out });
    expect(existsSync(join(first.target, "stale.txt"))).toBe(false);
    expect(existsSync(join(first.target, "index.html"))).toBe(true);
  });

  it("keeps existing files when --no-clean (clean: false)", async () => {
    const manifestPath = manifest(VALID);
    const { target } = await pack({ in: work.src, manifest: manifestPath, out: work.out });
    writeFileSync(join(target, "keep.txt"), "keep");
    await pack({ in: work.src, manifest: manifestPath, out: work.out, clean: false });
    expect(existsSync(join(target, "keep.txt"))).toBe(true);
  });
});

describe("pack (contract validation — mirrors GameCatalog)", () => {
  const cases = {
    "rejects missing id": { ...VALID, id: "" },
    "rejects id with a path separator": { ...VALID, id: "a/b" },
    "rejects id containing ..": { ...VALID, id: ".." },
    "rejects missing name": { ...VALID, name: "" },
    "rejects missing entry": { ...VALID, entry: "" },
    "rejects non-positive maxPlayers": { ...VALID, maxPlayers: 0 },
    "rejects non-integer maxPlayers": { ...VALID, maxPlayers: 2.5 },
  };
  for (const [label, obj] of Object.entries(cases)) {
    it(label, async () => {
      await expect(pack({ in: work.src, manifest: manifest(obj), out: work.out })).rejects.toThrow(PackError);
    });
  }

  it("rejects crossOriginIsolated that is not a boolean", async () => {
    await expect(pack({ in: work.src, manifest: manifest({ ...VALID, crossOriginIsolated: "yes" }), out: work.out }))
      .rejects.toThrow(PackError);
  });

  it("rejects an entry file that does not exist in --in", async () => {
    await expect(pack({ in: work.src, manifest: manifest({ ...VALID, entry: "missing.html" }), out: work.out }))
      .rejects.toThrow(/entry file not found/);
  });

  it("rejects an entry that escapes the built folder", async () => {
    await expect(pack({ in: work.src, manifest: manifest({ ...VALID, entry: "../secret.html" }), out: work.out }))
      .rejects.toThrow(/escapes the built folder/);
  });

  it("rejects a declared thumbnail that is missing", async () => {
    await expect(pack({ in: work.src, manifest: manifest({ ...VALID, thumbnail: "nope.svg" }), out: work.out }))
      .rejects.toThrow(/thumbnail .* not found/);
  });

  it("rejects a thumbnail name that escapes the game folder", async () => {
    // The source exists, but the OUTPUT name would write outside <id>/.
    writeFileSync(join(work.root, "evil.svg"), "<svg/>");
    await expect(pack({ in: work.src, manifest: manifest({ ...VALID, thumbnail: "../evil.svg" }), out: work.out }))
      .rejects.toThrow(/escapes the game folder/);
  });
});

describe("pack (serverAuthority — mirrors GameCatalog + the load check)", () => {
  const GOOD_MODULE =
    "export function createAuthority(kb) {\n" +
    "  return { init() {}, applyIntent() { return null; }, snapshot() { return {}; } };\n" +
    "}\n" +
    "export const config = { perRecipient: false, tickHz: 0 };\n";

  const withAuthority = { ...VALID, serverAuthority: "authority.js" };

  it("accepts a valid module and packs it into the game folder", async () => {
    writeFileSync(join(work.src, "authority.js"), GOOD_MODULE);
    const { target } = await pack({ in: work.src, manifest: manifest(withAuthority), out: work.out });
    expect(existsSync(join(target, "authority.js"))).toBe(true);
  });

  it("rejects a non-string serverAuthority", async () => {
    await expect(pack({ in: work.src, manifest: manifest({ ...VALID, serverAuthority: 5 }), out: work.out }))
      .rejects.toThrow(/non-empty string/);
  });

  it("rejects a .wasm module (backend not yet supported)", async () => {
    writeFileSync(join(work.src, "authority.wasm"), "\0asm");
    await expect(pack({ in: work.src, manifest: manifest({ ...VALID, serverAuthority: "authority.wasm" }), out: work.out }))
      .rejects.toThrow(/WASM backend/);
  });

  it("rejects a module path that escapes the built folder", async () => {
    writeFileSync(join(work.root, "evil.js"), GOOD_MODULE);
    await expect(pack({ in: work.src, manifest: manifest({ ...VALID, serverAuthority: "../evil.js" }), out: work.out }))
      .rejects.toThrow(/escapes the built folder/);
  });

  it("rejects a missing module", async () => {
    await expect(pack({ in: work.src, manifest: manifest(withAuthority), out: work.out }))
      .rejects.toThrow(/serverAuthority module not found/);
  });

  it("rejects an oversize module", async () => {
    writeFileSync(join(work.src, "authority.js"), GOOD_MODULE + "//" + "x".repeat(AUTHORITY_MAX_SCRIPT_BYTES));
    await expect(pack({ in: work.src, manifest: manifest(withAuthority), out: work.out }))
      .rejects.toThrow(/max \d+/);
  });

  it("rejects a module with a relative import (single-file rule)", async () => {
    writeFileSync(join(work.src, "authority.js"), "import './helpers.js';\n" + GOOD_MODULE);
    await expect(pack({ in: work.src, manifest: manifest(withAuthority), out: work.out }))
      .rejects.toThrow(/single-file/);
  });

  it("rejects a module that does not export createAuthority (load check)", async () => {
    writeFileSync(join(work.src, "authority.js"), "export const config = {};");
    await expect(pack({ in: work.src, manifest: manifest(withAuthority), out: work.out }))
      .rejects.toThrow(/createAuthority/);
  });

  it("rejects a malformed config export (load check)", async () => {
    writeFileSync(join(work.src, "authority.js"),
      "export function createAuthority(kb) { return {}; }\nexport const config = { tickHz: 'fast' };");
    await expect(pack({ in: work.src, manifest: manifest(withAuthority), out: work.out }))
      .rejects.toThrow(/tickHz/);
  });

  it("does not attempt a load when serverAuthority is absent", async () => {
    // An unfortunately-named client asset with a syntax error must not break packing.
    writeFileSync(join(work.src, "authority.js"), "this is not javascript ((");
    const { target } = await pack({ in: work.src, manifest: manifest(VALID), out: work.out });
    expect(existsSync(join(target, "authority.js"))).toBe(true);
  });
});

describe("scanAuthorityImports", () => {
  it("throws on top-level import statements and export … from re-exports", () => {
    expect(() => scanAuthorityImports("import x from './y.js';")).toThrow(/single-file/);
    expect(() => scanAuthorityImports("import './side-effect.js';")).toThrow(/single-file/);
    expect(() => scanAuthorityImports("export { a } from './b.js';")).toThrow(/single-file/);
    expect(() => scanAuthorityImports("export * from './b.js';")).toThrow(/single-file/);
  });

  it("allows plain exports", () => {
    expect(() => scanAuthorityImports(
      "export function createAuthority(kb) {}\nexport const config = {};")).not.toThrow();
  });
});

describe("validate", () => {
  it("returns the resolved thumbnail source for a valid manifest", () => {
    const manifestPath = manifest(VALID);
    expect(validate(VALID, manifestPath, work.src)).toBe(join(work.meta, "thumb.svg"));
  });
});
