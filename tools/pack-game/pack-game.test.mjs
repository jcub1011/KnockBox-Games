import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { tmpdir } from "node:os";
import { pack, PackError, validate } from "./pack-game.mjs";

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
  it("assembles <out>/<id>/ with built files, manifest, and thumbnail", () => {
    const manifestPath = manifest(VALID);
    const { target } = pack({ in: work.src, manifest: manifestPath, out: work.out });

    expect(target).toBe(join(work.out, "demo"));
    expect(existsSync(join(target, "index.html"))).toBe(true);
    expect(existsSync(join(target, "GAME.json"))).toBe(true);
    expect(existsSync(join(target, "thumb.svg"))).toBe(true);
    // Manifest is copied verbatim.
    expect(JSON.parse(readFileSync(join(target, "GAME.json"), "utf8"))).toEqual(VALID);
  });

  it("works with no thumbnail declared", () => {
    const { id, name, entry, maxPlayers } = VALID;
    const { target } = pack({ in: work.src, manifest: manifest({ id, name, entry, maxPlayers }), out: work.out });
    expect(existsSync(join(target, "index.html"))).toBe(true);
    expect(existsSync(join(target, "thumb.svg"))).toBe(false);
  });

  it("re-packs idempotently, removing stale files from a prior pack", () => {
    const manifestPath = manifest(VALID);
    const first = pack({ in: work.src, manifest: manifestPath, out: work.out });
    writeFileSync(join(first.target, "stale.txt"), "old"); // simulate a leftover
    pack({ in: work.src, manifest: manifestPath, out: work.out });
    expect(existsSync(join(first.target, "stale.txt"))).toBe(false);
    expect(existsSync(join(first.target, "index.html"))).toBe(true);
  });

  it("keeps existing files when --no-clean (clean: false)", () => {
    const manifestPath = manifest(VALID);
    const { target } = pack({ in: work.src, manifest: manifestPath, out: work.out });
    writeFileSync(join(target, "keep.txt"), "keep");
    pack({ in: work.src, manifest: manifestPath, out: work.out, clean: false });
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
    it(label, () => {
      expect(() => pack({ in: work.src, manifest: manifest(obj), out: work.out })).toThrow(PackError);
    });
  }

  it("rejects crossOriginIsolated that is not a boolean", () => {
    expect(() => pack({ in: work.src, manifest: manifest({ ...VALID, crossOriginIsolated: "yes" }), out: work.out }))
      .toThrow(PackError);
  });

  it("rejects an entry file that does not exist in --in", () => {
    expect(() => pack({ in: work.src, manifest: manifest({ ...VALID, entry: "missing.html" }), out: work.out }))
      .toThrow(/entry file not found/);
  });

  it("rejects an entry that escapes the built folder", () => {
    expect(() => pack({ in: work.src, manifest: manifest({ ...VALID, entry: "../secret.html" }), out: work.out }))
      .toThrow(/escapes the built folder/);
  });

  it("rejects a declared thumbnail that is missing", () => {
    expect(() => pack({ in: work.src, manifest: manifest({ ...VALID, thumbnail: "nope.svg" }), out: work.out }))
      .toThrow(/thumbnail .* not found/);
  });

  it("rejects a thumbnail name that escapes the game folder", () => {
    // The source exists, but the OUTPUT name would write outside <id>/.
    writeFileSync(join(work.root, "evil.svg"), "<svg/>");
    expect(() => pack({ in: work.src, manifest: manifest({ ...VALID, thumbnail: "../evil.svg" }), out: work.out }))
      .toThrow(/escapes the game folder/);
  });
});

describe("pack (build + thumbnail override)", () => {
  it("runs --build before assembling", () => {
    // The build writes the entry the manifest points at; without it, validation fails.
    const built = join(work.root, "built");
    const entry = join(built, "index.html");
    const cmd = `node -e "const fs=require('fs');fs.mkdirSync('${built.replace(/\\/g, "\\\\")}',{recursive:true});fs.writeFileSync('${entry.replace(/\\/g, "\\\\")}','<html></html>')"`;
    const { target } = pack({ in: built, manifest: manifest(VALID), out: work.out, build: cmd });
    expect(existsSync(join(target, "index.html"))).toBe(true);
  });

  it("copies a nested thumbnail name, creating the parent dir", () => {
    // The declared name is nested and that dir isn't part of the build — pack must mkdir it.
    mkdirSync(join(work.meta, "assets"));
    writeFileSync(join(work.meta, "assets", "thumb.svg"), "<svg/>");
    const { target } = pack({ in: work.src, manifest: manifest({ ...VALID, thumbnail: "assets/thumb.svg" }), out: work.out });
    expect(existsSync(join(target, "assets", "thumb.svg"))).toBe(true);
  });

  it("--thumbnail overrides the source but keeps the declared output name", () => {
    const override = join(work.root, "custom.svg");
    writeFileSync(override, "<svg id='custom'/>");
    const { target } = pack({ in: work.src, manifest: manifest(VALID), out: work.out, thumbnail: override });
    expect(readFileSync(join(target, "thumb.svg"), "utf8")).toContain("custom");
  });

  it("rejects --thumbnail when no thumbnail is declared", () => {
    const { id, name, entry, maxPlayers } = VALID;
    const override = join(work.root, "custom.svg");
    writeFileSync(override, "<svg/>");
    expect(() => pack({ in: work.src, manifest: manifest({ id, name, entry, maxPlayers }), out: work.out, thumbnail: override }))
      .toThrow(/declares no 'thumbnail'/);
  });
});

describe("validate", () => {
  it("returns the resolved thumbnail source for a valid manifest", () => {
    const manifestPath = manifest(VALID);
    expect(validate(VALID, manifestPath, work.src)).toBe(join(work.meta, "thumb.svg"));
  });
});
