import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, statSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { tmpdir } from "node:os";
import {
  AUTHORITY_MAX_SCRIPT_BYTES,
  AUTHORITY_MAX_WORD_FILE_BYTES,
  pack,
  PackError,
  parseArgs,
  scanAuthorityImports,
  validate,
} from "./pack-game.mjs";
import { readKbg } from "./kbg.mjs";

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

describe("pack → .kbg (default output)", () => {
  it("writes <id>.kbg containing the built files, manifest, and thumbnail", async () => {
    const { target, stats, header } = await pack({ in: work.src, manifest: manifest(VALID), out: work.out + "/" });

    expect(target).toBe(join(work.out, "demo.kbg"));
    const { files, header: read } = readKbg(readFileSync(target));
    expect([...files.keys()].sort()).toEqual(["GAME.json", "index.html", "thumb.svg"]);
    // The manifest travels verbatim, so the server sees exactly what the author wrote.
    expect(JSON.parse(files.get("GAME.json").toString("utf8"))).toEqual(VALID);
    expect(read.id).toBe("demo");
    expect(read.formatVersion).toBe(1);
    expect(read.packedBy).toMatch(/^knockbox-pack /);
    expect(stats.raw).toBeGreaterThan(0);
    expect(header.files).toHaveLength(3);
  });

  it("stamps --version into the package for operator visibility", async () => {
    const { target } = await pack({ in: work.src, manifest: manifest(VALID), out: work.out + "/", version: "4.5.6" });
    expect(readKbg(readFileSync(target)).header.version).toBe("4.5.6");
  });

  it("defaults the stamped version to GAME.json's own 'version'", async () => {
    // The two must agree: a marketplace compares its catalog version (taken from GAME.json) with
    // what is installed, so a header claiming a different build would misreport staleness.
    const { target } = await pack({ in: work.src, manifest: manifest({ ...VALID, version: "1.2.3" }), out: work.out + "/" });
    expect(readKbg(readFileSync(target)).header.version).toBe("1.2.3");
  });

  it("lets an explicit --version override GAME.json's 'version'", async () => {
    const { target } = await pack({
      in: work.src, manifest: manifest({ ...VALID, version: "1.2.3" }), out: work.out + "/", version: "9.9.9",
    });
    expect(readKbg(readFileSync(target)).header.version).toBe("9.9.9");
  });

  it("omits the version when neither the manifest nor --version supplies one", async () => {
    const { target } = await pack({ in: work.src, manifest: manifest(VALID), out: work.out + "/" });
    expect(readKbg(readFileSync(target)).header.version).toBeUndefined();
  });

  it("accepts --out naming the file itself", async () => {
    const explicit = join(work.root, "custom-name.kbg");
    const { target } = await pack({ in: work.src, manifest: manifest(VALID), out: explicit });
    expect(target).toBe(explicit);
    // The installed folder is named from the id, never from the archive filename.
    expect(readKbg(readFileSync(explicit)).header.id).toBe("demo");
  });

  it("rejects an --out that is neither a .kbg file nor an existing directory", async () => {
    await expect(pack({ in: work.src, manifest: manifest(VALID), out: join(work.root, "typo") }))
      .rejects.toThrow(/must name a .kbg file or an existing directory/);
  });

  it("rejects --out together with --dir", async () => {
    await expect(pack({ in: work.src, manifest: manifest(VALID), out: "a.kbg", dir: "b" }))
      .rejects.toThrow(/mutually exclusive/);
  });

  it("rejects --no-clean for .kbg output, where it has no meaning", async () => {
    await expect(pack({ in: work.src, manifest: manifest(VALID), out: work.out + "/", clean: false }))
      .rejects.toThrow(/only applies to --dir/);
  });

  it("rejects an out-of-range --quality", async () => {
    for (const quality of [-1, 12, 1.5]) {
      await expect(pack({ in: work.src, manifest: manifest(VALID), out: work.out + "/", quality }))
        .rejects.toThrow(/--quality must be an integer from 0 to 11/);
    }
  });

  it("is deterministic apart from the packedAt timestamp", async () => {
    // Byte-for-byte reproducibility lets an operator diff or checksum two builds; only the
    // deliberately-recorded pack time may differ between runs.
    const a = await pack({ in: work.src, manifest: manifest(VALID), out: join(work.root, "a.kbg"), version: "1" });
    const b = await pack({ in: work.src, manifest: manifest(VALID), out: join(work.root, "b.kbg"), version: "1" });
    const strip = (h) => ({ ...h, packedAt: undefined });
    expect(strip(readKbg(readFileSync(a.target)).header)).toEqual(strip(readKbg(readFileSync(b.target)).header));
  });
});

describe("pack → folder (--dir, debug output)", () => {
  it("assembles <dir>/<id>/ with built files, manifest, and thumbnail", async () => {
    const manifestPath = manifest(VALID);
    const { target } = await pack({ in: work.src, manifest: manifestPath, dir: work.out });

    expect(target).toBe(join(work.out, "demo"));
    expect(existsSync(join(target, "index.html"))).toBe(true);
    expect(existsSync(join(target, "GAME.json"))).toBe(true);
    expect(existsSync(join(target, "thumb.svg"))).toBe(true);
    // Manifest is copied verbatim.
    expect(JSON.parse(readFileSync(join(target, "GAME.json"), "utf8"))).toEqual(VALID);
  });

  it("works with no thumbnail declared", async () => {
    const { id, name, entry, maxPlayers } = VALID;
    const { target } = await pack({ in: work.src, manifest: manifest({ id, name, entry, maxPlayers }), dir: work.out });
    expect(existsSync(join(target, "index.html"))).toBe(true);
    expect(existsSync(join(target, "thumb.svg"))).toBe(false);
  });

  it("re-packs idempotently, removing stale files from a prior pack", async () => {
    const manifestPath = manifest(VALID);
    const first = await pack({ in: work.src, manifest: manifestPath, dir: work.out });
    writeFileSync(join(first.target, "stale.txt"), "old"); // simulate a leftover
    await pack({ in: work.src, manifest: manifestPath, dir: work.out });
    expect(existsSync(join(first.target, "stale.txt"))).toBe(false);
    expect(existsSync(join(first.target, "index.html"))).toBe(true);
  });

  it("keeps existing files when --no-clean (clean: false)", async () => {
    const manifestPath = manifest(VALID);
    const { target } = await pack({ in: work.src, manifest: manifestPath, dir: work.out });
    writeFileSync(join(target, "keep.txt"), "keep");
    await pack({ in: work.src, manifest: manifestPath, dir: work.out, clean: false });
    expect(existsSync(join(target, "keep.txt"))).toBe(true);
  });

  it("preserves nested build directories", async () => {
    mkdirSync(join(work.src, "assets", "deep"), { recursive: true });
    writeFileSync(join(work.src, "assets", "deep", "data.bin"), "payload");
    const { target } = await pack({ in: work.src, manifest: manifest(VALID), dir: work.out });
    expect(readFileSync(join(target, "assets", "deep", "data.bin"), "utf8")).toBe("payload");
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
      await expect(pack({ in: work.src, manifest: manifest(obj), dir: work.out })).rejects.toThrow(PackError);
    });
  }

  it("rejects crossOriginIsolated that is not a boolean", async () => {
    await expect(pack({ in: work.src, manifest: manifest({ ...VALID, crossOriginIsolated: "yes" }), dir: work.out }))
      .rejects.toThrow(PackError);
  });

  it("rejects a version that is not a string", async () => {
    // 1.2 as a bare number would be copied into KBG.json as a JSON number, where the server's
    // string-typed header field cannot read it — the whole package would fail to install.
    await expect(pack({ in: work.src, manifest: manifest({ ...VALID, version: 1.2 }), dir: work.out }))
      .rejects.toThrow(/'version' must be a non-empty string/);
  });

  it("rejects an entry file that does not exist in --in", async () => {
    await expect(pack({ in: work.src, manifest: manifest({ ...VALID, entry: "missing.html" }), dir: work.out }))
      .rejects.toThrow(/entry file not found/);
  });

  it("rejects an entry that escapes the built folder", async () => {
    await expect(pack({ in: work.src, manifest: manifest({ ...VALID, entry: "../secret.html" }), dir: work.out }))
      .rejects.toThrow(/escapes the built folder/);
  });

  it("rejects a declared thumbnail that is missing", async () => {
    await expect(pack({ in: work.src, manifest: manifest({ ...VALID, thumbnail: "nope.svg" }), dir: work.out }))
      .rejects.toThrow(/thumbnail .* not found/);
  });

  it("rejects a thumbnail name that escapes the game folder", async () => {
    // The source exists, but the OUTPUT name would write outside <id>/.
    writeFileSync(join(work.root, "evil.svg"), "<svg/>");
    await expect(pack({ in: work.src, manifest: manifest({ ...VALID, thumbnail: "../evil.svg" }), dir: work.out }))
      .rejects.toThrow(/escapes the game folder/);
  });

  it("validation runs for .kbg output too, before anything is written", async () => {
    const out = join(work.root, "bad.kbg");
    await expect(pack({ in: work.src, manifest: manifest({ ...VALID, id: "a/b" }), out })).rejects.toThrow(PackError);
    expect(existsSync(out)).toBe(false);
  });
});

describe("pack (build + thumbnail override)", () => {
  it("runs --build before assembling", async () => {
    // The build writes the entry the manifest points at; without it, validation fails.
    const built = join(work.root, "built");
    const entry = join(built, "index.html");
    const cmd = `node -e "const fs=require('fs');fs.mkdirSync('${built.replace(/\\/g, "\\\\")}',{recursive:true});fs.writeFileSync('${entry.replace(/\\/g, "\\\\")}','<html></html>')"`;
    const { target } = await pack({ in: built, manifest: manifest(VALID), dir: work.out, build: cmd });
    expect(existsSync(join(target, "index.html"))).toBe(true);
  });

  it("copies a nested thumbnail name, creating the parent dir", async () => {
    // The declared name is nested and that dir isn't part of the build — pack must mkdir it.
    mkdirSync(join(work.meta, "assets"));
    writeFileSync(join(work.meta, "assets", "thumb.svg"), "<svg/>");
    const { target } = await pack({ in: work.src, manifest: manifest({ ...VALID, thumbnail: "assets/thumb.svg" }), dir: work.out });
    expect(existsSync(join(target, "assets", "thumb.svg"))).toBe(true);
  });

  it("--thumbnail overrides the source but keeps the declared output name", async () => {
    const override = join(work.root, "custom.svg");
    writeFileSync(override, "<svg id='custom'/>");
    const { target } = await pack({ in: work.src, manifest: manifest(VALID), dir: work.out, thumbnail: override });
    expect(readFileSync(join(target, "thumb.svg"), "utf8")).toContain("custom");
  });

  it("--thumbnail also applies to .kbg output", async () => {
    const override = join(work.root, "custom.svg");
    writeFileSync(override, "<svg id='custom'/>");
    const { target } = await pack({ in: work.src, manifest: manifest(VALID), out: work.out + "/", thumbnail: override });
    const { files } = readKbg(readFileSync(target));
    expect(files.get("thumb.svg").toString("utf8")).toContain("custom");
  });

  it("rejects --thumbnail when no thumbnail is declared", async () => {
    const { id, name, entry, maxPlayers } = VALID;
    const override = join(work.root, "custom.svg");
    writeFileSync(override, "<svg/>");
    await expect(pack({ in: work.src, manifest: manifest({ id, name, entry, maxPlayers }), dir: work.out, thumbnail: override }))
      .rejects.toThrow(/declares no 'thumbnail'/);
  });

  it("an explicit --manifest beats a stale GAME.json inside the build", async () => {
    writeFileSync(join(work.src, "GAME.json"), JSON.stringify({ id: "stale", name: "Stale", entry: "index.html", maxPlayers: 1 }));
    const { target } = await pack({ in: work.src, manifest: manifest(VALID), dir: work.out });
    expect(JSON.parse(readFileSync(join(target, "GAME.json"), "utf8")).id).toBe("demo");
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
    const { target } = await pack({ in: work.src, manifest: manifest(withAuthority), dir: work.out });
    expect(existsSync(join(target, "authority.js"))).toBe(true);
  });

  it("ships the module inside the .kbg, where the server extracts but never serves it", async () => {
    // The module has to travel with the game — a packaged server-authority game is useless without
    // it — and the secrecy guarantee is the game origin's (GameOriginAssetGate), not the packer's.
    writeFileSync(join(work.src, "authority.js"), GOOD_MODULE);
    const { target } = await pack({ in: work.src, manifest: manifest(withAuthority), out: work.out + "/" });
    const { files } = readKbg(readFileSync(target));
    expect(files.get("authority.js").toString("utf8")).toBe(GOOD_MODULE);
  });

  it("rejects a non-string serverAuthority", async () => {
    await expect(pack({ in: work.src, manifest: manifest({ ...VALID, serverAuthority: 5 }), dir: work.out }))
      .rejects.toThrow(/non-empty string/);
  });

  it("rejects a .wasm module (backend not yet supported)", async () => {
    writeFileSync(join(work.src, "authority.wasm"), "\0asm");
    await expect(pack({ in: work.src, manifest: manifest({ ...VALID, serverAuthority: "authority.wasm" }), dir: work.out }))
      .rejects.toThrow(/WASM backend/);
  });

  it("rejects a module path that escapes the built folder", async () => {
    writeFileSync(join(work.root, "evil.js"), GOOD_MODULE);
    await expect(pack({ in: work.src, manifest: manifest({ ...VALID, serverAuthority: "../evil.js" }), dir: work.out }))
      .rejects.toThrow(/escapes the built folder/);
  });

  it("rejects a missing module", async () => {
    await expect(pack({ in: work.src, manifest: manifest(withAuthority), dir: work.out }))
      .rejects.toThrow(/serverAuthority module not found/);
  });

  it("rejects an oversize module", async () => {
    writeFileSync(join(work.src, "authority.js"), GOOD_MODULE + "//" + "x".repeat(AUTHORITY_MAX_SCRIPT_BYTES));
    await expect(pack({ in: work.src, manifest: manifest(withAuthority), dir: work.out }))
      .rejects.toThrow(/max \d+/);
  });

  it("rejects a module with a relative import (single-file rule)", async () => {
    writeFileSync(join(work.src, "authority.js"), "import './helpers.js';\n" + GOOD_MODULE);
    await expect(pack({ in: work.src, manifest: manifest(withAuthority), dir: work.out }))
      .rejects.toThrow(/single-file/);
  });

  it("rejects a module that does not export createAuthority (load check)", async () => {
    writeFileSync(join(work.src, "authority.js"), "export const config = {};");
    await expect(pack({ in: work.src, manifest: manifest(withAuthority), dir: work.out }))
      .rejects.toThrow(/createAuthority/);
  });

  it("rejects a malformed config export (load check)", async () => {
    writeFileSync(join(work.src, "authority.js"),
      "export function createAuthority(kb) { return {}; }\nexport const config = { tickHz: 'fast' };");
    await expect(pack({ in: work.src, manifest: manifest(withAuthority), dir: work.out }))
      .rejects.toThrow(/tickHz/);
  });

  it("does not attempt a load when serverAuthority is absent", async () => {
    // An unfortunately-named client asset with a syntax error must not break packing.
    writeFileSync(join(work.src, "authority.js"), "this is not javascript ((");
    const { target } = await pack({ in: work.src, manifest: manifest(VALID), dir: work.out });
    expect(existsSync(join(target, "authority.js"))).toBe(true);
  });
});

describe("pack (authorityWords — mirrors GameCatalog.ValidateAuthorityWords)", () => {
  const GOOD_MODULE =
    "export function createAuthority(kb) {\n" +
    "  return { init() {}, applyIntent() { return null; }, snapshot() { return {}; } };\n" +
    "}\n";

  const withWords = {
    ...VALID,
    serverAuthority: "authority.js",
    authorityWords: { en: { file: "words.txt", caseInsensitive: true } },
  };

  function writeWordGame(extra = {}) {
    writeFileSync(join(work.src, "authority.js"), GOOD_MODULE);
    writeFileSync(join(work.src, "words.txt"), "apple\nbrave\ncrane\n");
    return manifest({ ...withWords, ...extra });
  }

  it("accepts valid authorityWords and packs the dictionary into the game folder", async () => {
    const { target } = await pack({ in: work.src, manifest: writeWordGame(), dir: work.out });
    expect(existsSync(join(target, "words.txt"))).toBe(true);
  });

  it("ships the dictionary inside the .kbg alongside the module", async () => {
    const { target } = await pack({ in: work.src, manifest: writeWordGame(), out: work.out + "/" });
    const { files } = readKbg(readFileSync(target));
    expect([...files.keys()]).toContain("words.txt");
    expect([...files.keys()]).toContain("authority.js");
  });

  it("rejects authorityWords without serverAuthority", async () => {
    writeFileSync(join(work.src, "words.txt"), "apple\n");
    const m = manifest({ ...VALID, authorityWords: { en: { file: "words.txt" } } });
    await expect(pack({ in: work.src, manifest: m, dir: work.out })).rejects.toThrow(/requires 'serverAuthority'/);
  });

  it("rejects a word file that does not exist", async () => {
    writeFileSync(join(work.src, "authority.js"), GOOD_MODULE);
    const m = manifest(withWords); // words.txt not written
    await expect(pack({ in: work.src, manifest: m, dir: work.out })).rejects.toThrow(/file not found/);
  });

  it("rejects a word file that escapes the built folder", async () => {
    writeFileSync(join(work.root, "evil.txt"), "apple\n");
    const m = writeWordGame({ authorityWords: { en: { file: "../evil.txt" } } });
    await expect(pack({ in: work.src, manifest: m, dir: work.out })).rejects.toThrow(/escapes the built folder/);
  });

  it("rejects an oversize word file", async () => {
    writeFileSync(join(work.src, "authority.js"), GOOD_MODULE);
    writeFileSync(join(work.src, "words.txt"), "a".repeat(AUTHORITY_MAX_WORD_FILE_BYTES + 1));
    await expect(pack({ in: work.src, manifest: manifest(withWords), dir: work.out })).rejects.toThrow(/max \d+/);
  });

  it("rejects a non-boolean caseInsensitive", async () => {
    const m = writeWordGame({ authorityWords: { en: { file: "words.txt", caseInsensitive: "yes" } } });
    await expect(pack({ in: work.src, manifest: m, dir: work.out })).rejects.toThrow(/caseInsensitive/);
  });

  it("rejects an entry missing its file", async () => {
    const m = writeWordGame({ authorityWords: { en: {} } });
    await expect(pack({ in: work.src, manifest: m, dir: work.out })).rejects.toThrow(/non-empty 'file'/);
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

describe("parseArgs", () => {
  it("parses the flags the docs advertise", () => {
    expect(parseArgs([
      "--in", "dist", "--manifest", "export/GAME.json", "--out", "x.kbg",
      "--version", "1.2.3", "--quality", "5",
    ])).toEqual({ in: "dist", manifest: "export/GAME.json", out: "x.kbg", version: "1.2.3", quality: 5 });
  });

  it("parses --dir and --no-clean", () => {
    expect(parseArgs(["--dir", "out", "--no-clean"])).toEqual({ dir: "out", clean: false });
  });

  it("throws on an unknown flag", () => {
    expect(() => parseArgs(["--nope"])).toThrow(PackError);
  });
});

describe("validate", () => {
  it("returns the resolved thumbnail source for a valid manifest", () => {
    const manifestPath = manifest(VALID);
    expect(validate(VALID, manifestPath, work.src)).toBe(join(work.meta, "thumb.svg"));
  });

  it("returns null when no thumbnail is declared", () => {
    const { id, name, entry, maxPlayers } = VALID;
    const obj = { id, name, entry, maxPlayers };
    expect(validate(obj, manifest(obj), work.src)).toBeNull();
  });
});

describe("packaged output shape", () => {
  it("a .kbg is smaller than the folder it replaces, for compressible content", async () => {
    // 8 KiB of highly compressible text clears the 1024-byte floor and is not on the denylist.
    writeFileSync(join(work.src, "big.js"), "console.log('x');".repeat(500));
    const { target, stats } = await pack({ in: work.src, manifest: manifest(VALID), out: work.out + "/" });
    expect(stats.compressed).toBeGreaterThan(0);
    expect(statSync(target).size).toBeLessThan(stats.raw);
  });
});
