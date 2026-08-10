import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, statSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { tmpdir } from "node:os";
import { pack, PackError, parseArgs, validate } from "./pack-game.mjs";
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
  it("writes <id>.kbg containing the built files, manifest, and thumbnail", () => {
    const { target, stats, header } = pack({ in: work.src, manifest: manifest(VALID), out: work.out + "/" });

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

  it("stamps --version into the package for operator visibility", () => {
    const { target } = pack({ in: work.src, manifest: manifest(VALID), out: work.out + "/", version: "4.5.6" });
    expect(readKbg(readFileSync(target)).header.version).toBe("4.5.6");
  });

  it("accepts --out naming the file itself", () => {
    const explicit = join(work.root, "custom-name.kbg");
    const { target } = pack({ in: work.src, manifest: manifest(VALID), out: explicit });
    expect(target).toBe(explicit);
    // The installed folder is named from the id, never from the archive filename.
    expect(readKbg(readFileSync(explicit)).header.id).toBe("demo");
  });

  it("rejects an --out that is neither a .kbg file nor an existing directory", () => {
    expect(() => pack({ in: work.src, manifest: manifest(VALID), out: join(work.root, "typo") }))
      .toThrow(/must name a .kbg file or an existing directory/);
  });

  it("rejects --out together with --dir", () => {
    expect(() => pack({ in: work.src, manifest: manifest(VALID), out: "a.kbg", dir: "b" }))
      .toThrow(/mutually exclusive/);
  });

  it("rejects --no-clean for .kbg output, where it has no meaning", () => {
    expect(() => pack({ in: work.src, manifest: manifest(VALID), out: work.out + "/", clean: false }))
      .toThrow(/only applies to --dir/);
  });

  it("rejects an out-of-range --quality", () => {
    for (const quality of [-1, 12, 1.5]) {
      expect(() => pack({ in: work.src, manifest: manifest(VALID), out: work.out + "/", quality }))
        .toThrow(/--quality must be an integer from 0 to 11/);
    }
  });

  it("is deterministic apart from the packedAt timestamp", () => {
    // Byte-for-byte reproducibility lets an operator diff or checksum two builds; only the
    // deliberately-recorded pack time may differ between runs.
    const a = pack({ in: work.src, manifest: manifest(VALID), out: join(work.root, "a.kbg"), version: "1" });
    const b = pack({ in: work.src, manifest: manifest(VALID), out: join(work.root, "b.kbg"), version: "1" });
    const strip = (h) => ({ ...h, packedAt: undefined });
    expect(strip(readKbg(readFileSync(a.target)).header)).toEqual(strip(readKbg(readFileSync(b.target)).header));
  });
});

describe("pack → folder (--dir, debug output)", () => {
  it("assembles <dir>/<id>/ with built files, manifest, and thumbnail", () => {
    const manifestPath = manifest(VALID);
    const { target } = pack({ in: work.src, manifest: manifestPath, dir: work.out });

    expect(target).toBe(join(work.out, "demo"));
    expect(existsSync(join(target, "index.html"))).toBe(true);
    expect(existsSync(join(target, "GAME.json"))).toBe(true);
    expect(existsSync(join(target, "thumb.svg"))).toBe(true);
    // Manifest is copied verbatim.
    expect(JSON.parse(readFileSync(join(target, "GAME.json"), "utf8"))).toEqual(VALID);
  });

  it("works with no thumbnail declared", () => {
    const { id, name, entry, maxPlayers } = VALID;
    const { target } = pack({ in: work.src, manifest: manifest({ id, name, entry, maxPlayers }), dir: work.out });
    expect(existsSync(join(target, "index.html"))).toBe(true);
    expect(existsSync(join(target, "thumb.svg"))).toBe(false);
  });

  it("re-packs idempotently, removing stale files from a prior pack", () => {
    const manifestPath = manifest(VALID);
    const first = pack({ in: work.src, manifest: manifestPath, dir: work.out });
    writeFileSync(join(first.target, "stale.txt"), "old"); // simulate a leftover
    pack({ in: work.src, manifest: manifestPath, dir: work.out });
    expect(existsSync(join(first.target, "stale.txt"))).toBe(false);
    expect(existsSync(join(first.target, "index.html"))).toBe(true);
  });

  it("keeps existing files when --no-clean (clean: false)", () => {
    const manifestPath = manifest(VALID);
    const { target } = pack({ in: work.src, manifest: manifestPath, dir: work.out });
    writeFileSync(join(target, "keep.txt"), "keep");
    pack({ in: work.src, manifest: manifestPath, dir: work.out, clean: false });
    expect(existsSync(join(target, "keep.txt"))).toBe(true);
  });

  it("preserves nested build directories", () => {
    mkdirSync(join(work.src, "assets", "deep"), { recursive: true });
    writeFileSync(join(work.src, "assets", "deep", "data.bin"), "payload");
    const { target } = pack({ in: work.src, manifest: manifest(VALID), dir: work.out });
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
    it(label, () => {
      expect(() => pack({ in: work.src, manifest: manifest(obj), dir: work.out })).toThrow(PackError);
    });
  }

  it("rejects crossOriginIsolated that is not a boolean", () => {
    expect(() => pack({ in: work.src, manifest: manifest({ ...VALID, crossOriginIsolated: "yes" }), dir: work.out }))
      .toThrow(PackError);
  });

  it("rejects an entry file that does not exist in --in", () => {
    expect(() => pack({ in: work.src, manifest: manifest({ ...VALID, entry: "missing.html" }), dir: work.out }))
      .toThrow(/entry file not found/);
  });

  it("rejects an entry that escapes the built folder", () => {
    expect(() => pack({ in: work.src, manifest: manifest({ ...VALID, entry: "../secret.html" }), dir: work.out }))
      .toThrow(/escapes the built folder/);
  });

  it("rejects a declared thumbnail that is missing", () => {
    expect(() => pack({ in: work.src, manifest: manifest({ ...VALID, thumbnail: "nope.svg" }), dir: work.out }))
      .toThrow(/thumbnail .* not found/);
  });

  it("rejects a thumbnail name that escapes the game folder", () => {
    // The source exists, but the OUTPUT name would write outside <id>/.
    writeFileSync(join(work.root, "evil.svg"), "<svg/>");
    expect(() => pack({ in: work.src, manifest: manifest({ ...VALID, thumbnail: "../evil.svg" }), dir: work.out }))
      .toThrow(/escapes the game folder/);
  });

  it("validation runs for .kbg output too, before anything is written", () => {
    const out = join(work.root, "bad.kbg");
    expect(() => pack({ in: work.src, manifest: manifest({ ...VALID, id: "a/b" }), out })).toThrow(PackError);
    expect(existsSync(out)).toBe(false);
  });
});

describe("pack (build + thumbnail override)", () => {
  it("runs --build before assembling", () => {
    // The build writes the entry the manifest points at; without it, validation fails.
    const built = join(work.root, "built");
    const entry = join(built, "index.html");
    const cmd = `node -e "const fs=require('fs');fs.mkdirSync('${built.replace(/\\/g, "\\\\")}',{recursive:true});fs.writeFileSync('${entry.replace(/\\/g, "\\\\")}','<html></html>')"`;
    const { target } = pack({ in: built, manifest: manifest(VALID), dir: work.out, build: cmd });
    expect(existsSync(join(target, "index.html"))).toBe(true);
  });

  it("copies a nested thumbnail name, creating the parent dir", () => {
    // The declared name is nested and that dir isn't part of the build — pack must mkdir it.
    mkdirSync(join(work.meta, "assets"));
    writeFileSync(join(work.meta, "assets", "thumb.svg"), "<svg/>");
    const { target } = pack({ in: work.src, manifest: manifest({ ...VALID, thumbnail: "assets/thumb.svg" }), dir: work.out });
    expect(existsSync(join(target, "assets", "thumb.svg"))).toBe(true);
  });

  it("--thumbnail overrides the source but keeps the declared output name", () => {
    const override = join(work.root, "custom.svg");
    writeFileSync(override, "<svg id='custom'/>");
    const { target } = pack({ in: work.src, manifest: manifest(VALID), dir: work.out, thumbnail: override });
    expect(readFileSync(join(target, "thumb.svg"), "utf8")).toContain("custom");
  });

  it("--thumbnail also applies to .kbg output", () => {
    const override = join(work.root, "custom.svg");
    writeFileSync(override, "<svg id='custom'/>");
    const { target } = pack({ in: work.src, manifest: manifest(VALID), out: work.out + "/", thumbnail: override });
    const { files } = readKbg(readFileSync(target));
    expect(files.get("thumb.svg").toString("utf8")).toContain("custom");
  });

  it("rejects --thumbnail when no thumbnail is declared", () => {
    const { id, name, entry, maxPlayers } = VALID;
    const override = join(work.root, "custom.svg");
    writeFileSync(override, "<svg/>");
    expect(() => pack({ in: work.src, manifest: manifest({ id, name, entry, maxPlayers }), dir: work.out, thumbnail: override }))
      .toThrow(/declares no 'thumbnail'/);
  });

  it("an explicit --manifest beats a stale GAME.json inside the build", () => {
    writeFileSync(join(work.src, "GAME.json"), JSON.stringify({ id: "stale", name: "Stale", entry: "index.html", maxPlayers: 1 }));
    const { target } = pack({ in: work.src, manifest: manifest(VALID), dir: work.out });
    expect(JSON.parse(readFileSync(join(target, "GAME.json"), "utf8")).id).toBe("demo");
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
  it("a .kbg is smaller than the folder it replaces, for compressible content", () => {
    // 8 KiB of highly compressible text clears the 1024-byte floor and is not on the denylist.
    writeFileSync(join(work.src, "big.js"), "console.log('x');".repeat(500));
    const { target, stats } = pack({ in: work.src, manifest: manifest(VALID), out: work.out + "/" });
    expect(stats.compressed).toBeGreaterThan(0);
    expect(statSync(target).size).toBeLessThan(stats.raw);
  });
});
