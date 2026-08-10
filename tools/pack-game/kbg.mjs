/*
 * .kbg (KnockBox Game) reader/writer — the reference implementation of the writer side of
 * docs/KBG_FORMAT.md. Zero dependencies: a .kbg is a ZIP with every entry STORED, and Node's
 * node:zlib supplies both Brotli (for payloads) and crc32 (for ZIP headers), so there is no
 * deflate path and nothing to install.
 *
 * Why stored-only: `br` payloads are already compressed, and `identity` payloads were judged
 * not worth compressing. Deflating either would burn CPU for nothing.
 *
 * The compress-or-store decision deliberately mirrors
 * KnockBox.Server/Games/GameAssetPrecompressor.ShouldCompress + its not-smaller backstop, because
 * the server copies these Brotli streams straight into its HTTP serving cache. Keep the two in
 * sync: if that denylist or the size floor changes, change it here too.
 *
 * Memory note: payloads are held in memory while packing, because KBG.json carries every file's
 * size/sha256 and must be written as the FIRST entry — so all content must be known before any
 * bytes go out. Peak usage is the sum of the *packed* payloads, not the raw game.
 */

import { createHash } from "node:crypto";
import { brotliCompressSync, brotliDecompressSync, constants as zconst, crc32 } from "node:zlib";

/** The only format version this module reads or writes. */
export const KBG_FORMAT_VERSION = 1;

/** Name of the header entry. Always the first entry, always stored. */
export const HEADER_NAME = "KBG.json";

/** Default size floor for compression, mirroring the server's KnockBox:PrecompressMinBytes. */
export const DEFAULT_MIN_BYTES = 1024;

/** Default Brotli quality. 11 is max effort: slow to pack, but paid once instead of per server boot. */
export const DEFAULT_QUALITY = 11;

// Mirrors GameAssetPrecompressor.IncompressibleExtensions — contents already compressed by their
// own format, where re-compressing wastes CPU and rarely shrinks.
const INCOMPRESSIBLE = new Set([
  ".png", ".jpg", ".jpeg", ".gif", ".webp", ".avif", ".ico",
  ".mp3", ".ogg", ".wav", ".mp4", ".webm", ".woff2",
  ".br", ".gz", ".zip", ".kbg",
]);

// Windows reserved device names are unusable as filenames with OR without an extension, so a game
// containing one could never be extracted there. Rejected at pack time rather than at install.
const RESERVED = new Set([
  "CON", "PRN", "AUX", "NUL",
  "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
  "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
]);

// ZIP has no Zip64 in format v1, so these are hard ceilings rather than a fallback path. Failing
// loudly beats emitting an archive a v1 reader cannot open.
const MAX_U32 = 0xffffffff;
const MAX_ENTRIES = 0xffff;

// Fixed DOS timestamp (1980-01-01 00:00:00) so packing identical input twice yields identical
// bytes — operators can then diff or checksum builds. Year field is years since 1980.
const DOS_TIME = 0;
const DOS_DATE = (0 << 9) | (1 << 5) | 1;

/** Thrown for any malformed input or format-contract violation. */
export class KbgError extends Error {}

/**
 * Pure decision: compress unless the file is below <minBytes> or its extension is a known
 * already-compressed format. Denylist (not allowlist) so unknown engine asset types still get
 * compressed; the not-smaller check in packKbg is the backstop.
 */
export function shouldCompress(path, size, minBytes = DEFAULT_MIN_BYTES) {
  if (size < minBytes) return false;
  const dot = path.lastIndexOf(".");
  const slash = Math.max(path.lastIndexOf("/"), path.lastIndexOf("\\"));
  const ext = dot > slash ? path.slice(dot).toLowerCase() : "";
  return !INCOMPRESSIBLE.has(ext);
}

/**
 * Validate one logical path against docs/KBG_FORMAT.md "Path rules". Returns the normalized
 * (forward-slashed) path; throws KbgError describing the first violation.
 */
export function normalizePath(raw, what = "path") {
  if (typeof raw !== "string" || raw === "") throw new KbgError(`${what} must be a non-empty string.`);
  // Accept a Windows-authored path here (the caller may pass one from the filesystem) but store
  // forward slashes, which is what the ZIP spec requires.
  const path = raw.replaceAll("\\", "/");
  if (path.startsWith("/")) throw new KbgError(`${what} must be relative: "${raw}".`);
  if (/^[A-Za-z]:/.test(path)) throw new KbgError(`${what} must not carry a drive letter: "${raw}".`);
  if (path.includes(":")) throw new KbgError(`${what} must not contain ":": "${raw}".`);

  for (const seg of path.split("/")) {
    if (seg === "") throw new KbgError(`${what} has an empty segment: "${raw}".`);
    if (seg === "." || seg === "..") throw new KbgError(`${what} must not contain "." or ".." segments: "${raw}".`);
    // Windows silently trims trailing dots and spaces, so "a. " and "a" would collide on extract.
    if (seg.endsWith(".") || seg.endsWith(" ")) {
      throw new KbgError(`${what} segment "${seg}" must not end in a dot or space: "${raw}".`);
    }
    const stem = (seg.includes(".") ? seg.slice(0, seg.indexOf(".")) : seg).toUpperCase();
    if (RESERVED.has(stem)) throw new KbgError(`${what} segment "${seg}" is a Windows reserved device name: "${raw}".`);
    // eslint-disable-next-line no-control-regex
    if (/[<>"|?*\u0000-\u001f]/.test(seg)) throw new KbgError(`${what} segment "${seg}" contains an invalid filename character.`);
  }
  return path;
}

/** Validate an id: everything normalizePath enforces, plus "must be a single segment". */
export function normalizeId(raw) {
  const id = normalizePath(raw, "id");
  if (id.includes("/")) throw new KbgError(`id must be a single path segment (no slashes): "${raw}".`);
  return id;
}

/**
 * Pack a game into a .kbg buffer.
 *
 * @param {object} o
 * @param {Array<{path: string, data: Buffer}>} o.entries logical game-folder paths + contents
 * @param {string} o.id            game id (names the installed folder; must match GAME.json)
 * @param {string} o.name          display name
 * @param {string} [o.version]     free-form game build label
 * @param {string} [o.packedBy]    tool identification
 * @param {string} [o.packedAt]    RFC 3339 timestamp
 * @param {number} [o.quality]     Brotli quality 0-11
 * @param {number} [o.minBytes]    compression size floor
 * @returns {{ buffer: Buffer, header: object, stats: {raw: number, packed: number, compressed: number} }}
 */
export function packKbg({
  entries, id, name, version, packedBy, packedAt,
  quality = DEFAULT_QUALITY, minBytes = DEFAULT_MIN_BYTES,
}) {
  const safeId = normalizeId(id);
  if (typeof name !== "string" || name.trim() === "") throw new KbgError("name is required.");
  if (!Array.isArray(entries) || entries.length === 0) throw new KbgError("a .kbg must contain at least one file.");

  // Normalize + reject duplicates before doing any expensive compression.
  const seen = new Map(); // lowercased path -> original, for case-insensitive collision detection
  const normalized = entries.map(({ path, data }) => {
    const p = normalizePath(path);
    if (!Buffer.isBuffer(data)) throw new KbgError(`contents for "${p}" must be a Buffer.`);
    const key = p.toLowerCase();
    if (seen.has(key)) {
      const other = seen.get(key);
      throw new KbgError(other === p
        ? `duplicate file in package: "${p}".`
        : `files "${other}" and "${p}" differ only by case and would collide on Windows/macOS.`);
    }
    seen.set(key, p);
    return { path: p, data };
  });
  // Stable, deterministic order so repacking identical input gives identical bytes.
  normalized.sort((a, b) => (a.path < b.path ? -1 : a.path > b.path ? 1 : 0));

  if (!normalized.some((e) => e.path === "GAME.json")) {
    throw new KbgError("a .kbg must contain a root GAME.json.");
  }

  // Entry names are DERIVED from (path, encoding): `path` for identity, `path + ".br"` for br. A
  // literal "foo.br" in the game therefore collides with the compressed form of "foo". Vanishingly
  // rare, but an explicit error beats an ambiguous archive.
  const files = [];
  const payloads = []; // { name, data } — ZIP entry name and stored bytes
  let raw = 0;
  let compressedCount = 0;

  for (const { path, data } of normalized) {
    raw += data.length;
    let encoding = "identity";
    let stored = data;

    if (shouldCompress(path, data.length, minBytes)) {
      const br = brotliCompressSync(data, {
        params: {
          [zconst.BROTLI_PARAM_QUALITY]: quality,
          [zconst.BROTLI_PARAM_SIZE_HINT]: data.length,
        },
      });
      // Backstop: if Brotli didn't actually shrink it, store it raw. Same rule the server applies.
      if (br.length < data.length) {
        encoding = "br";
        stored = br;
        compressedCount++;
      }
    }

    const entryName = encoding === "br" ? `${path}.br` : path;
    if (seen.has(entryName.toLowerCase()) && entryName !== path) {
      throw new KbgError(
        `"${path}" would be stored as entry "${entryName}", which collides with the file ` +
        `"${seen.get(entryName.toLowerCase())}" already in this game. Rename one of them.`);
    }

    files.push({
      path,
      encoding,
      size: data.length,
      sha256: createHash("sha256").update(data).digest("hex"),
    });
    payloads.push({ name: entryName, data: stored });
  }

  const header = {
    formatVersion: KBG_FORMAT_VERSION,
    id: safeId,
    name,
    ...(version === undefined ? {} : { version }),
    ...(packedBy === undefined ? {} : { packedBy }),
    ...(packedAt === undefined ? {} : { packedAt }),
    files,
  };

  // KBG.json goes FIRST so `head -c` can sniff a file's identity without a ZIP parser.
  const all = [
    { name: HEADER_NAME, data: Buffer.from(`${JSON.stringify(header, null, 2)}\n`, "utf8") },
    ...payloads,
  ];
  const buffer = writeStoredZip(all);
  return { buffer, header, stats: { raw, packed: buffer.length, compressed: compressedCount } };
}

/**
 * Assemble a ZIP where every entry is stored (method 0). No Zip64, no data descriptors.
 * Exported so tests can build deliberately malformed fixtures (e.g. a future formatVersion, or an
 * entry missing from KBG.json) that packKbg would refuse to produce.
 */
export function writeStoredZip(entries) {
  if (entries.length > MAX_ENTRIES) {
    throw new KbgError(
      `${entries.length} entries exceeds the ${MAX_ENTRIES}-entry limit of the .kbg v1 format (Zip64 is not part of v1).`);
  }

  const locals = [];
  const centrals = [];
  let offset = 0;

  for (const { name, data } of entries) {
    const nameBuf = Buffer.from(name, "utf8");
    if (data.length > MAX_U32) {
      throw new KbgError(`"${name}" is ${data.length} bytes, over the 4 GiB per-entry limit of the .kbg v1 format.`);
    }
    const crc = crc32(data);

    const local = Buffer.alloc(30 + nameBuf.length);
    local.writeUInt32LE(0x04034b50, 0);  // local file header signature
    local.writeUInt16LE(10, 4);          // version needed: 1.0 is enough for stored
    local.writeUInt16LE(0x0800, 6);      // flags: bit 11 = names are UTF-8
    local.writeUInt16LE(0, 8);           // method 0 = stored
    local.writeUInt16LE(DOS_TIME, 10);
    local.writeUInt16LE(DOS_DATE, 12);
    local.writeUInt32LE(crc, 14);
    local.writeUInt32LE(data.length, 18); // compressed size == uncompressed size when stored
    local.writeUInt32LE(data.length, 22);
    local.writeUInt16LE(nameBuf.length, 26);
    local.writeUInt16LE(0, 28);           // no extra field
    nameBuf.copy(local, 30);

    const central = Buffer.alloc(46 + nameBuf.length);
    central.writeUInt32LE(0x02014b50, 0); // central directory header signature
    // "Version made by": high byte 0 = MS-DOS/FAT. Deliberately NOT unix, so no file mode is
    // implied — readers must never apply one, and this writer never emits one.
    central.writeUInt16LE(0x001e, 4);
    central.writeUInt16LE(10, 6);
    central.writeUInt16LE(0x0800, 8);
    central.writeUInt16LE(0, 10);
    central.writeUInt16LE(DOS_TIME, 12);
    central.writeUInt16LE(DOS_DATE, 14);
    central.writeUInt32LE(crc, 16);
    central.writeUInt32LE(data.length, 20);
    central.writeUInt32LE(data.length, 24);
    central.writeUInt16LE(nameBuf.length, 28);
    central.writeUInt16LE(0, 30);         // extra field length
    central.writeUInt16LE(0, 32);         // file comment length
    central.writeUInt16LE(0, 34);         // disk number start
    central.writeUInt16LE(0, 36);         // internal attributes
    central.writeUInt32LE(0, 38);         // external attributes: none (see "version made by" above)
    central.writeUInt32LE(offset, 42);    // offset of the local header
    nameBuf.copy(central, 46);

    locals.push(local, data);
    centrals.push(central);
    offset += local.length + data.length;
    if (offset > MAX_U32) {
      throw new KbgError("package exceeds the 4 GiB total limit of the .kbg v1 format (Zip64 is not part of v1).");
    }
  }

  const centralSize = centrals.reduce((n, c) => n + c.length, 0);
  const eocd = Buffer.alloc(22);
  eocd.writeUInt32LE(0x06054b50, 0);     // end of central directory signature
  eocd.writeUInt16LE(0, 4);              // this disk
  eocd.writeUInt16LE(0, 6);              // disk with the central directory
  eocd.writeUInt16LE(entries.length, 8);
  eocd.writeUInt16LE(entries.length, 10);
  eocd.writeUInt32LE(centralSize, 12);
  eocd.writeUInt32LE(offset, 16);        // central directory offset
  eocd.writeUInt16LE(0, 20);             // no archive comment

  return Buffer.concat([...locals, ...centrals, eocd]);
}

/**
 * Read a .kbg back. Used by the packer's post-write self-check and by tests; the server has its own
 * reader. Verifies CRCs, that every entry is stored, and that the header's `files` list is closed in
 * BOTH directions against the archive.
 *
 * @returns {{ header: object, files: Map<string, Buffer> }} files keyed by LOGICAL path, decompressed.
 */
export function readKbg(buffer) {
  const raw = readStoredZip(buffer);

  const headerBytes = raw.get(HEADER_NAME);
  if (!headerBytes) throw new KbgError(`not a .kbg: no ${HEADER_NAME} entry.`);
  let header;
  try {
    header = JSON.parse(headerBytes.toString("utf8"));
  } catch (err) {
    throw new KbgError(`${HEADER_NAME} is not valid JSON: ${err.message}`);
  }

  if (!Number.isInteger(header.formatVersion)) throw new KbgError(`${HEADER_NAME}: 'formatVersion' must be an integer.`);
  if (header.formatVersion > KBG_FORMAT_VERSION) {
    throw new KbgError(
      `this package declares .kbg format version ${header.formatVersion}, but this tool only understands ` +
      `${KBG_FORMAT_VERSION} — it was packed by a newer version of KnockBox.`);
  }
  normalizeId(header.id);
  if (!Array.isArray(header.files)) throw new KbgError(`${HEADER_NAME}: 'files' must be an array.`);

  const files = new Map();
  const claimed = new Set([HEADER_NAME]);

  for (const row of header.files) {
    const path = normalizePath(row?.path, "files[].path");
    if (row.encoding !== "identity" && row.encoding !== "br") {
      throw new KbgError(`${HEADER_NAME}: unsupported encoding "${row.encoding}" for "${path}".`);
    }
    if (!Number.isInteger(row.size) || row.size < 0) {
      throw new KbgError(`${HEADER_NAME}: 'size' for "${path}" must be a non-negative integer.`);
    }
    if (files.has(path)) throw new KbgError(`${HEADER_NAME}: duplicate entry for "${path}".`);

    const entryName = row.encoding === "br" ? `${path}.br` : path;
    const stored = raw.get(entryName);
    if (!stored) throw new KbgError(`${HEADER_NAME} lists "${path}" but the archive has no "${entryName}" entry.`);
    claimed.add(entryName);

    const data = row.encoding === "br" ? brotliDecompressSync(stored) : stored;
    if (data.length !== row.size) {
      throw new KbgError(`"${path}" decompressed to ${data.length} bytes but ${HEADER_NAME} declares ${row.size}.`);
    }
    if (row.sha256 !== undefined) {
      const actual = createHash("sha256").update(data).digest("hex");
      if (actual !== row.sha256) throw new KbgError(`"${path}" failed its SHA-256 check.`);
    }
    files.set(path, data);
  }

  // The other direction: nothing may be smuggled past the header.
  for (const name of raw.keys()) {
    if (!claimed.has(name)) throw new KbgError(`archive contains "${name}", which is not listed in ${HEADER_NAME}.`);
  }
  if (!files.has("GAME.json")) throw new KbgError("not a valid .kbg: no root GAME.json.");

  return { header, files };
}

/** Parse a stored-only ZIP via its central directory. Returns entry name -> bytes. */
function readStoredZip(buffer) {
  // Scan back for the EOCD; there is no archive comment in a .kbg, but tolerate one.
  let eocd = -1;
  for (let i = buffer.length - 22; i >= 0 && i >= buffer.length - 22 - 0xffff; i--) {
    if (buffer.readUInt32LE(i) === 0x06054b50) { eocd = i; break; }
  }
  if (eocd < 0) throw new KbgError("not a ZIP archive (no end-of-central-directory record) — truncated or corrupt.");

  const count = buffer.readUInt16LE(eocd + 10);
  let p = buffer.readUInt32LE(eocd + 16);
  const entries = new Map();

  for (let i = 0; i < count; i++) {
    if (p + 46 > buffer.length || buffer.readUInt32LE(p) !== 0x02014b50) {
      throw new KbgError("corrupt central directory.");
    }
    const method = buffer.readUInt16LE(p + 10);
    const crc = buffer.readUInt32LE(p + 16);
    const size = buffer.readUInt32LE(p + 24);
    const nameLen = buffer.readUInt16LE(p + 28);
    const extraLen = buffer.readUInt16LE(p + 30);
    const commentLen = buffer.readUInt16LE(p + 32);
    const localOffset = buffer.readUInt32LE(p + 42);
    const name = buffer.toString("utf8", p + 46, p + 46 + nameLen);

    if (method !== 0) throw new KbgError(`"${name}" uses compression method ${method}; every .kbg entry must be stored.`);

    if (localOffset + 30 > buffer.length || buffer.readUInt32LE(localOffset) !== 0x04034b50) {
      throw new KbgError(`corrupt local header for "${name}".`);
    }
    const lNameLen = buffer.readUInt16LE(localOffset + 26);
    const lExtraLen = buffer.readUInt16LE(localOffset + 28);
    const start = localOffset + 30 + lNameLen + lExtraLen;
    if (start + size > buffer.length) throw new KbgError(`"${name}" extends past the end of the archive — truncated.`);

    const data = buffer.subarray(start, start + size);
    if (crc32(data) !== crc) throw new KbgError(`"${name}" failed its CRC check — the package is corrupt.`);
    if (entries.has(name)) throw new KbgError(`archive contains two entries named "${name}".`);
    entries.set(name, data);

    p += 46 + nameLen + extraLen + commentLen;
  }
  return entries;
}
