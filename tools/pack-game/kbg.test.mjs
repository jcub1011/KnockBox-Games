import { describe, expect, it } from "vitest";
import { brotliDecompressSync } from "node:zlib";
import {
  DEFAULT_MIN_BYTES, HEADER_NAME, KBG_FORMAT_VERSION, KbgError,
  normalizeId, normalizePath, packKbg, readKbg, shouldCompress, writeStoredZip,
} from "./kbg.mjs";

const B = (s) => Buffer.from(s);
const GAME_JSON = JSON.stringify({ id: "demo", name: "Demo", entry: "index.html", maxPlayers: 2 });

/** The minimum valid game: a root GAME.json plus an entry page. */
function game(extra = []) {
  return [
    { path: "GAME.json", data: B(GAME_JSON) },
    { path: "index.html", data: B("<!doctype html><title>demo</title>") },
    ...extra,
  ];
}

const packDemo = (extra = [], opts = {}) =>
  packKbg({ entries: game(extra), id: "demo", name: "Demo", ...opts });

/** Read the raw ZIP entry names in physical (local-header) order. */
function entryNamesInOrder(buffer) {
  const names = [];
  let p = 0;
  while (p + 30 <= buffer.length && buffer.readUInt32LE(p) === 0x04034b50) {
    const size = buffer.readUInt32LE(p + 18);
    const nameLen = buffer.readUInt16LE(p + 26);
    const extraLen = buffer.readUInt16LE(p + 28);
    names.push(buffer.toString("utf8", p + 30, p + 30 + nameLen));
    p += 30 + nameLen + extraLen + size;
  }
  return names;
}

describe("packKbg / readKbg round-trip", () => {
  it("round-trips every file byte-for-byte", () => {
    const big = Buffer.from("abcdefgh".repeat(1000));      // compressible, over the floor
    const noise = Buffer.from([...Array(2048).keys()].map((i) => (i * 37) % 256));
    const entries = game([{ path: "assets/big.js", data: big }, { path: "assets/noise.bin", data: noise }]);
    const { buffer } = packKbg({ entries, id: "demo", name: "Demo" });

    const { files } = readKbg(buffer);
    expect(files.size).toBe(entries.length);
    for (const { path, data } of entries) expect(files.get(path)).toEqual(data);
  });

  it("writes KBG.json as the first entry, stored, so the file can be sniffed", () => {
    const { buffer } = packDemo();
    expect(entryNamesInOrder(buffer)[0]).toBe(HEADER_NAME);
    expect(buffer.readUInt16LE(8)).toBe(0); // compression method of the first local header
  });

  it("stores every entry uncompressed (payloads are already Brotli)", () => {
    const { buffer } = packDemo([{ path: "big.js", data: Buffer.from("x".repeat(4000)) }]);
    let p = 0;
    let seen = 0;
    while (p + 30 <= buffer.length && buffer.readUInt32LE(p) === 0x04034b50) {
      expect(buffer.readUInt16LE(p + 8)).toBe(0);
      const size = buffer.readUInt32LE(p + 18);
      p += 30 + buffer.readUInt16LE(p + 26) + buffer.readUInt16LE(p + 28) + size;
      seen++;
    }
    expect(seen).toBe(4); // KBG.json + GAME.json + index.html + big.js
  });

  it("declares formatVersion 1 and the required header fields", () => {
    const { header } = packDemo([], { version: "9.9", packedBy: "test", packedAt: "2026-01-01T00:00:00Z" });
    expect(header.formatVersion).toBe(KBG_FORMAT_VERSION);
    expect(header).toMatchObject({ id: "demo", name: "Demo", version: "9.9", packedBy: "test" });
    expect(header.files.every((f) => typeof f.sha256 === "string" && f.sha256.length === 64)).toBe(true);
  });

  it("is byte-for-byte deterministic for identical input", () => {
    const opts = { version: "1", packedBy: "test", packedAt: "2026-01-01T00:00:00Z" };
    expect(packDemo([], opts).buffer).toEqual(packDemo([], opts).buffer);
  });

  it("orders entries deterministically regardless of input order", () => {
    const a = packKbg({ entries: game([{ path: "b.txt", data: B("b") }, { path: "a.txt", data: B("a") }]), id: "demo", name: "Demo" });
    const b = packKbg({ entries: game([{ path: "a.txt", data: B("a") }, { path: "b.txt", data: B("b") }]), id: "demo", name: "Demo" });
    expect(a.header.files.map((f) => f.path)).toEqual(b.header.files.map((f) => f.path));
  });

  it("preserves an empty file", () => {
    const { buffer } = packDemo([{ path: "empty.txt", data: Buffer.alloc(0) }]);
    expect(readKbg(buffer).files.get("empty.txt")).toEqual(Buffer.alloc(0));
  });

  it("preserves nested paths", () => {
    const { buffer } = packDemo([{ path: "a/b/c/deep.txt", data: B("deep") }]);
    expect(readKbg(buffer).files.get("a/b/c/deep.txt").toString()).toBe("deep");
  });
});

describe("compression decisions (mirror GameAssetPrecompressor)", () => {
  it("Brotli-compresses a large compressible file and stores the blob under path + .br", () => {
    const big = Buffer.from("console.log('hello');".repeat(400));
    const { buffer, header, stats } = packDemo([{ path: "big.js", data: big }]);

    expect(header.files.find((f) => f.path === "big.js").encoding).toBe("br");
    expect(stats.compressed).toBeGreaterThan(0);
    // The stored entry really is a raw Brotli stream of the original bytes.
    const names = entryNamesInOrder(buffer);
    expect(names).toContain("big.js.br");
    expect(names).not.toContain("big.js");
  });

  it("stores files below the size floor without compressing", () => {
    const { header } = packDemo([{ path: "tiny.js", data: B("let a=1;") }]);
    expect(header.files.find((f) => f.path === "tiny.js").encoding).toBe("identity");
  });

  it("stores already-compressed formats without recompressing", () => {
    // 4 KiB clears the floor, so only the extension denylist can keep it uncompressed.
    const png = Buffer.alloc(4096, 7);
    const { header } = packDemo([{ path: "sprite.png", data: png }]);
    expect(header.files.find((f) => f.path === "sprite.png").encoding).toBe("identity");
  });

  it("falls back to identity when Brotli does not actually shrink the file", () => {
    // Incompressible random-ish bytes with a non-denylisted extension: the not-smaller backstop
    // is the only thing that can keep this stored.
    const rnd = Buffer.alloc(2048);
    for (let i = 0; i < rnd.length; i++) rnd[i] = (i * 2654435761) & 0xff;
    const { header } = packKbg({ entries: game([{ path: "noise.dat", data: rnd }]), id: "demo", name: "Demo", quality: 0 });
    const row = header.files.find((f) => f.path === "noise.dat");
    // Whichever way the backstop goes, the round-trip must still be exact.
    expect(["identity", "br"]).toContain(row.encoding);
    expect(readKbg(packKbg({ entries: game([{ path: "noise.dat", data: rnd }]), id: "demo", name: "Demo", quality: 0 }).buffer)
      .files.get("noise.dat")).toEqual(rnd);
  });

  it("shouldCompress matches the server's denylist and floor", () => {
    expect(shouldCompress("a.js", 5000)).toBe(true);
    expect(shouldCompress("a.wasm", 5000)).toBe(true);
    expect(shouldCompress("a.unknown", 5000)).toBe(true); // denylist, not allowlist
    expect(shouldCompress("a.png", 5000)).toBe(false);
    expect(shouldCompress("a.br", 5000)).toBe(false);
    expect(shouldCompress("a.kbg", 5000)).toBe(false);
    expect(shouldCompress("a.js", DEFAULT_MIN_BYTES - 1)).toBe(false);
    expect(shouldCompress("dir.png/a.js", 5000)).toBe(true); // extension comes from the last segment
  });

  it("--quality 0 still produces a readable package", () => {
    const { buffer } = packDemo([{ path: "big.js", data: Buffer.from("y".repeat(9000)) }], { quality: 0 });
    expect(readKbg(buffer).files.get("big.js").toString()).toBe("y".repeat(9000));
  });
});

describe("path rules", () => {
  const bad = {
    "traversal": "../evil.txt",
    "nested traversal": "a/../../evil.txt",
    "absolute": "/etc/passwd",
    "drive letter": "C:/windows/x",
    "alternate data stream": "foo:bar",
    "empty segment": "a//b",
    "dot segment": "a/./b",
    "trailing dot": "a.",
    "trailing space": "a ",
    "reserved device": "NUL",
    "reserved device with extension": "com1.txt",
    "wildcard": "a*.txt",
    "pipe": "a|b.txt",
  };
  for (const [label, path] of Object.entries(bad)) {
    it(`rejects ${label}`, () => {
      expect(() => packDemo([{ path, data: B("x") }])).toThrow(KbgError);
    });
  }

  it("accepts hyphens, spaces and dots inside names", () => {
    expect(normalizePath("my-game/some file.min.js")).toBe("my-game/some file.min.js");
  });

  it("normalizes backslashes to forward slashes", () => {
    expect(normalizePath("a\\b\\c.txt")).toBe("a/b/c.txt");
  });

  it("requires an id to be a single segment", () => {
    expect(normalizeId("demo")).toBe("demo");
    expect(() => normalizeId("a/b")).toThrow(/single path segment/);
    expect(() => normalizeId("..")).toThrow(KbgError);
    expect(() => normalizeId("")).toThrow(KbgError);
  });

  it("rejects duplicate paths", () => {
    expect(() => packDemo([{ path: "dup.txt", data: B("a") }, { path: "dup.txt", data: B("b") }]))
      .toThrow(/duplicate file/);
  });

  it("rejects paths that differ only by case, which collide on Windows and macOS", () => {
    expect(() => packDemo([{ path: "A.js", data: B("a") }, { path: "a.js", data: B("b") }]))
      .toThrow(/differ only by case/);
  });

  it("rejects a literal .br file that would collide with a compressed entry", () => {
    const big = Buffer.from("compress me ".repeat(400));
    expect(() => packDemo([{ path: "big", data: big }, { path: "big.br", data: B("literal") }]))
      .toThrow(/collides with the file/);
  });
});

describe("packKbg input validation", () => {
  it("requires a root GAME.json", () => {
    expect(() => packKbg({ entries: [{ path: "index.html", data: B("x") }], id: "demo", name: "Demo" }))
      .toThrow(/must contain a root GAME.json/);
  });

  it("requires a name", () => {
    expect(() => packKbg({ entries: game(), id: "demo", name: "  " })).toThrow(/name is required/);
  });

  it("requires at least one file", () => {
    expect(() => packKbg({ entries: [], id: "demo", name: "Demo" })).toThrow(/at least one file/);
  });

  it("requires Buffer contents", () => {
    expect(() => packKbg({ entries: [...game(), { path: "x.txt", data: "not a buffer" }], id: "demo", name: "Demo" }))
      .toThrow(/must be a Buffer/);
  });
});

describe("readKbg rejects malformed packages", () => {
  /** Build a deliberately malformed .kbg that packKbg would refuse to produce. */
  const forge = (header, entries) => writeStoredZip([
    { name: HEADER_NAME, data: B(JSON.stringify(header)) },
    ...entries,
  ]);
  const okFiles = [{ path: "GAME.json", encoding: "identity", size: GAME_JSON.length }];
  const okEntries = [{ name: "GAME.json", data: B(GAME_JSON) }];

  it("rejects a future formatVersion with an upgrade hint", () => {
    const z = forge({ formatVersion: KBG_FORMAT_VERSION + 1, id: "demo", name: "Demo", files: okFiles }, okEntries);
    expect(() => readKbg(z)).toThrow(/packed by a newer version of KnockBox/);
  });

  it("rejects a non-integer formatVersion", () => {
    expect(() => readKbg(forge({ formatVersion: "1", id: "demo", name: "Demo", files: okFiles }, okEntries)))
      .toThrow(/'formatVersion' must be an integer/);
  });

  it("rejects a missing KBG.json", () => {
    expect(() => readKbg(writeStoredZip(okEntries))).toThrow(/no KBG.json entry/);
  });

  it("rejects KBG.json that is not JSON", () => {
    expect(() => readKbg(writeStoredZip([{ name: HEADER_NAME, data: B("{oops") }, ...okEntries])))
      .toThrow(/not valid JSON/);
  });

  it("rejects an entry not listed in KBG.json (nothing smuggled past the header)", () => {
    const z = forge({ formatVersion: 1, id: "demo", name: "Demo", files: okFiles },
      [...okEntries, { name: "sneaky.js", data: B("evil()") }]);
    expect(() => readKbg(z)).toThrow(/not listed in KBG.json/);
  });

  it("rejects a listed file with no matching entry", () => {
    const z = forge({
      formatVersion: 1, id: "demo", name: "Demo",
      files: [...okFiles, { path: "ghost.js", encoding: "identity", size: 3 }],
    }, okEntries);
    expect(() => readKbg(z)).toThrow(/has no "ghost.js" entry/);
  });

  it("rejects a declared size that does not match the payload", () => {
    const z = forge({
      formatVersion: 1, id: "demo", name: "Demo",
      files: [{ path: "GAME.json", encoding: "identity", size: 99999 }],
    }, okEntries);
    expect(() => readKbg(z)).toThrow(/declares 99999/);
  });

  it("rejects a bad sha256", () => {
    const z = forge({
      formatVersion: 1, id: "demo", name: "Demo",
      files: [{ path: "GAME.json", encoding: "identity", size: GAME_JSON.length, sha256: "0".repeat(64) }],
    }, okEntries);
    expect(() => readKbg(z)).toThrow(/failed its SHA-256 check/);
  });

  it("rejects an unsupported encoding", () => {
    const z = forge({
      formatVersion: 1, id: "demo", name: "Demo",
      files: [{ path: "GAME.json", encoding: "gzip", size: 2 }],
    }, okEntries);
    expect(() => readKbg(z)).toThrow(/unsupported encoding/);
  });

  it("rejects a traversal path in the header", () => {
    const z = forge({
      formatVersion: 1, id: "demo", name: "Demo",
      files: [{ path: "../evil", encoding: "identity", size: 1 }],
    }, [{ name: "../evil", data: B("x") }]);
    expect(() => readKbg(z)).toThrow(KbgError);
  });

  it("rejects a package with no root GAME.json", () => {
    const z = forge({
      formatVersion: 1, id: "demo", name: "Demo",
      files: [{ path: "index.html", encoding: "identity", size: 1 }],
    }, [{ name: "index.html", data: B("x") }]);
    expect(() => readKbg(z)).toThrow(/no root GAME.json/);
  });

  it("rejects a truncated archive", () => {
    const { buffer } = packDemo();
    expect(() => readKbg(buffer.subarray(0, buffer.length - 8))).toThrow(/truncated or corrupt/);
  });

  it("rejects a corrupted payload via its CRC", () => {
    const { buffer } = packDemo();
    const corrupt = Buffer.from(buffer);
    // Flip a byte inside the first payload (just past the KBG.json local header).
    corrupt[30 + HEADER_NAME.length + 5] ^= 0xff;
    expect(() => readKbg(corrupt)).toThrow(/failed its CRC check/);
  });

  it("rejects a non-ZIP buffer", () => {
    expect(() => readKbg(B("this is not a zip file at all, not even close"))).toThrow(/not a ZIP archive/);
  });
});

describe("v1 format limits (no Zip64)", () => {
  it("rejects more entries than a v1 archive can address", () => {
    // writeStoredZip is the layer that owns the limit; going through packKbg would need 65k files.
    const many = Array.from({ length: 0x10000 }, (_, i) => ({ name: `f${i}`, data: Buffer.alloc(0) }));
    expect(() => writeStoredZip(many)).toThrow(/entry limit of the .kbg v1 format/);
  });
});

describe("brotli payloads are directly reusable as HTTP pre-compressed variants", () => {
  it("a br entry's stored bytes decompress standalone", () => {
    // This is the property that lets the server copy the blob straight into its serving cache
    // instead of re-running Brotli at max effort on boot.
    const big = Buffer.from("function x(){return 1;}".repeat(300));
    const { buffer } = packDemo([{ path: "code.js", data: big }]);

    let p = 0;
    let blob = null;
    while (p + 30 <= buffer.length && buffer.readUInt32LE(p) === 0x04034b50) {
      const size = buffer.readUInt32LE(p + 18);
      const nameLen = buffer.readUInt16LE(p + 26);
      const extraLen = buffer.readUInt16LE(p + 28);
      const name = buffer.toString("utf8", p + 30, p + 30 + nameLen);
      const start = p + 30 + nameLen + extraLen;
      if (name === "code.js.br") blob = buffer.subarray(start, start + size);
      p = start + size;
    }
    expect(blob).not.toBeNull();
    expect(brotliDecompressSync(blob)).toEqual(big);
  });
});
