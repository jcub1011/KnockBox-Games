/**
 * KnockBox addon installer — the `knockbox addon …` half of the CLI.
 *
 * Installs a versioned client addon (the Godot addon, the Phaser client, the vanilla JS SDK) into a
 * GAME's repo, records exactly what it wrote, and can tell you later whether what is on disk is
 * still what was published. Before this, the documented install procedure was "copy that folder and
 * don't fork it" — with nothing recording the version copied and nothing able to check the "don't".
 *
 * Trust model, deliberately the same as the game marketplace's (docs/MARKETPLACE.md §3), because
 * this is that mechanism pointed the other way:
 *   • The INDEX is the trust root, not the release. A release asset can be re-uploaded in place, so
 *     what the index commits to is a REQUIRED sha256, enforced on every download.
 *   • URLs are DERIVED ({base}/{repo}/releases/download/{tag}/{asset}), never carried in the index,
 *     so a tampered entry has nothing to point elsewhere. repo/tag/asset are pattern-checked BEFORE
 *     any request leaves the process.
 *   • Archives are untrusted input: every entry is validated (stored-only, CRC, path rules) before a
 *     byte is written, via the same kbg.mjs primitives the .kbg reader uses.
 *
 * Archive layout is PROJECT-RELATIVE, which is what makes a hand-unzip a first-class install:
 *
 *   addons/knockbox/…      the addon's files
 *   addons/knockbox/LICENSE
 *   knockbox.json          the record, at the project root
 *
 * So "unzip this at your project root" lands every file exactly where the CLI would have put it —
 * the same convention a Godot Asset Library zip uses. A developer with no Node installed is not on a
 * lesser path; they are on the same path without the download step.
 *
 * No dependencies (node builtins only), matching the rest of this tool. Every network call is
 * injectable so the tests never touch the network.
 */

import { createHash } from "node:crypto";
import { existsSync, mkdirSync, readFileSync, readdirSync, rmSync, rmdirSync, statSync, writeFileSync } from "node:fs";
import { dirname, join, relative, resolve, sep } from "node:path";
import { pathToFileURL } from "node:url";

import { KbgError, normalizePath, readStoredZip, writeStoredZip } from "./kbg.mjs";

/** The published index of addons. Overridable with --index (a URL or a local path). */
export const DEFAULT_INDEX_URL =
  "https://raw.githubusercontent.com/jcub1011/KnockBox-Games/main/.addons/ADDONS.json";

/** Download host for derived release URLs. Overridable with --download-base. */
export const DEFAULT_DOWNLOAD_BASE = "https://github.com";

/** The record this tool writes into a game repo. Committed, hand-readable. */
export const MANIFEST_NAME = "knockbox.json";

/**
 * The placeholder every in-repo version declaration holds.
 *
 * `clients/addons.manifest.json`'s `sdkVersion` is the ONE real version number. Files that have to
 * carry a version for their own format's sake — the Godot `plugin.cfg`, the CLI's `package.json` —
 * hold this sentinel in the repo and get the real value stamped in at build/publish time. That way a
 * release bumps exactly one file, and a stale number cannot exist to drift, because no file in the
 * repo claims a real version at all.
 *
 * It is also honest: a checkout is not a release, and a dev build saying `0.0.0-dev` is true.
 */
export const DEV_VERSION = "0.0.0-dev";

/** Highest index schemaVersion major this build understands. A newer major is refused, not half-read. */
export const MAX_INDEX_SCHEMA_MAJOR = 1;

/** Caps on an untrusted download. An addon is source text; these are generous by a wide margin. */
export const MAX_INDEX_BYTES = 1_048_576;
export const MAX_ARCHIVE_BYTES = 33_554_432;

/** Seconds before a network read is abandoned. */
export const INDEX_TIMEOUT_MS = 30_000;
export const DOWNLOAD_TIMEOUT_MS = 120_000;

/** Thrown for any addon usage/contract error, so the CLI reports it without a stack trace. */
export class AddonError extends Error {}

// ── Small helpers ──────────────────────────────────────────────────────────────

export function sha256(buffer) {
  return createHash("sha256").update(buffer).digest("hex");
}

/** Stable JSON with a trailing newline, so re-writing an unchanged manifest is a no-op in git. */
function stringify(value) {
  return `${JSON.stringify(value, null, 2)}\n`;
}

/**
 * `repo`, `tag` and `asset` go into a URL, so they are pattern-checked before they can be
 * concatenated into one. `asset` must end in .zip: the index once pointed at a GAME.json by mistake
 * on the game side, and a malformed asset name is far better as a hard error than as a 404 halfway
 * through an install.
 */
const REPO_RE = /^[A-Za-z0-9][A-Za-z0-9._-]*\/[A-Za-z0-9][A-Za-z0-9._-]*$/;
const TAG_RE = /^[A-Za-z0-9][A-Za-z0-9._-]*$/;
const ASSET_RE = /^[A-Za-z0-9][A-Za-z0-9._-]*\.zip$/;
const SHA256_RE = /^[a-f0-9]{64}$/;

// ── The index ──────────────────────────────────────────────────────────────────

/**
 * Parse and shape-check an ADDONS.json. Split from validateEntry the same way the marketplace splits
 * Parse (is it an index?) from ValidateEntry (is this entry safe to act on?) — a single bad entry
 * must not make the whole index unreadable.
 */
export function parseIndex(text) {
  let index;
  try {
    index = JSON.parse(text);
  } catch (err) {
    throw new AddonError(`addon index is not valid JSON: ${err.message}`);
  }
  if (index === null || typeof index !== "object" || Array.isArray(index)) {
    throw new AddonError("addon index must be a JSON object.");
  }

  const schema = String(index.schemaVersion ?? "");
  const major = Number.parseInt(schema.split(".")[0], 10);
  if (!Number.isInteger(major)) {
    throw new AddonError(`addon index has no usable schemaVersion (got ${JSON.stringify(index.schemaVersion)}).`);
  }
  if (major > MAX_INDEX_SCHEMA_MAJOR) {
    throw new AddonError(
      `addon index declares schemaVersion ${schema}, newer than this tool understands ` +
      `(${MAX_INDEX_SCHEMA_MAJOR}.x). Update the CLI: npm i -g knockbox-cli`);
  }

  if (index.addons === null || typeof index.addons !== "object" || Array.isArray(index.addons)) {
    throw new AddonError("addon index has no 'addons' object.");
  }
  return index;
}

/** The ids an index offers, sorted for stable output. */
export function indexIds(index) {
  return Object.keys(index.addons).sort();
}

/**
 * Look one addon up, with a message that lists what IS on offer — a typo'd id is the likeliest
 * failure here and "not found" alone makes the user go read the docs.
 */
export function indexEntry(index, id) {
  const entry = index.addons[id];
  if (!entry) {
    throw new AddonError(`unknown addon '${id}'. This index offers: ${indexIds(index).join(", ") || "(none)"}.`);
  }
  return entry;
}

/**
 * Choose which published release of an addon to act on.
 *
 * An entry describes its CURRENT release inline and may carry older ones under `versions`. Pinning
 * is served out of that map rather than by deriving a URL from the requested version number: the
 * sha256 is mandatory, and a version the index does not publish is a version there is no hash for.
 * Guessing the URL would install an unverifiable archive, which is the one thing this design refuses
 * to do — so an unpublished pin is an error naming what IS available.
 */
export function selectRelease(id, entry, wanted) {
  if (entry === null || typeof entry !== "object") throw new AddonError(`addon '${id}': entry must be an object.`);

  const bounds = (from) => ({
    minAppVersion: from.minAppVersion ?? entry.minAppVersion,
    maxAppVersion: from.maxAppVersion ?? entry.maxAppVersion,
  });

  if (!wanted || wanted === entry.version) {
    return { version: entry.version, source: entry.source, ...bounds(entry) };
  }

  const older = entry.versions?.[wanted];
  if (older) {
    return { version: wanted, source: older.source, ...bounds(older) };
  }

  const available = [entry.version, ...Object.keys(entry.versions ?? {})].filter(Boolean);
  throw new AddonError(
    `addon '${id}' version '${wanted}' is not published in this index. Available: ${available.join(", ")}.`);
}

/**
 * Validate everything about a selected release that has to hold before we act on it. Runs before any
 * request leaves the process, so a malformed entry can never become an outbound URL.
 */
export function validateEntry(id, entry) {
  const where = `addon '${id}'`;
  if (entry === null || typeof entry !== "object") throw new AddonError(`${where}: entry must be an object.`);

  const version = entry.version;
  if (typeof version !== "string" || version === "") throw new AddonError(`${where}: missing 'version'.`);

  const source = entry.source;
  if (source === null || typeof source !== "object") throw new AddonError(`${where}: missing 'source'.`);
  if (source.type !== "github-release") {
    throw new AddonError(`${where}: unsupported source type ${JSON.stringify(source.type)} (only 'github-release').`);
  }
  if (!REPO_RE.test(String(source.repo ?? ""))) throw new AddonError(`${where}: invalid source.repo ${JSON.stringify(source.repo)}.`);
  if (!TAG_RE.test(String(source.tag ?? ""))) throw new AddonError(`${where}: invalid source.tag ${JSON.stringify(source.tag)}.`);
  if (!ASSET_RE.test(String(source.asset ?? ""))) {
    throw new AddonError(`${where}: invalid source.asset ${JSON.stringify(source.asset)} — must be a .zip filename.`);
  }
  // Required, not optional. The index's commit history is the trust root; without a hash there is
  // nothing to check a re-uploaded release asset against.
  if (!SHA256_RE.test(String(source.sha256 ?? "").toLowerCase())) {
    throw new AddonError(`${where}: source.sha256 is required and must be 64 hex characters.`);
  }
  return entry;
}

/** Derive the download URL. Never read from the index — see the trust model above. */
export function downloadUrl(entry, base = DEFAULT_DOWNLOAD_BASE) {
  const { repo, tag, asset } = entry.source;
  return `${String(base).replace(/\/+$/, "")}/${repo}/releases/download/${tag}/${asset}`;
}

// ── Fetchers (injectable; a local path or file:// URL works, which is what tests and CI use) ──

function isProbablyUrl(location) {
  return /^[a-z][a-z0-9+.-]*:\/\//i.test(location);
}

async function readLocation(location, { maxBytes, timeoutMs, what }) {
  if (!isProbablyUrl(location)) {
    if (!existsSync(location)) throw new AddonError(`${what} not found: ${location}`);
    const bytes = readFileSync(location);
    if (bytes.length > maxBytes) throw new AddonError(`${what} is ${bytes.length} bytes, over the ${maxBytes}-byte cap.`);
    return bytes;
  }

  const url = new URL(location);
  if (url.protocol === "file:") {
    const bytes = readFileSync(url);
    if (bytes.length > maxBytes) throw new AddonError(`${what} is ${bytes.length} bytes, over the ${maxBytes}-byte cap.`);
    return bytes;
  }
  if (url.protocol !== "https:" && !(url.protocol === "http:" && isLocalHost(url.hostname))) {
    throw new AddonError(
      `${what} must be https (or http on localhost for testing): ${location}`);
  }

  const signal = AbortSignal.timeout(timeoutMs);
  let response;
  try {
    response = await fetch(url, { signal, redirect: "follow" });
  } catch (err) {
    throw new AddonError(`could not fetch ${what} from ${location}: ${err.message}`);
  }
  if (!response.ok) throw new AddonError(`${what} fetch failed: HTTP ${response.status} from ${location}`);

  const bytes = Buffer.from(await response.arrayBuffer());
  if (bytes.length > maxBytes) throw new AddonError(`${what} is ${bytes.length} bytes, over the ${maxBytes}-byte cap.`);
  return bytes;
}

/** Same rule the server's MarketplaceClient.IsAllowedUrl applies: https anywhere, http on loopback. */
function isLocalHost(hostname) {
  return hostname === "localhost" || hostname === "127.0.0.1" || hostname === "[::1]" || hostname === "::1";
}

export async function fetchIndex(location = DEFAULT_INDEX_URL) {
  const bytes = await readLocation(location, {
    maxBytes: MAX_INDEX_BYTES, timeoutMs: INDEX_TIMEOUT_MS, what: "addon index",
  });
  return parseIndex(bytes.toString("utf8"));
}

export async function fetchArchive(url) {
  return readLocation(url, {
    maxBytes: MAX_ARCHIVE_BYTES, timeoutMs: DOWNLOAD_TIMEOUT_MS, what: "addon archive",
  });
}

// ── The project manifest (knockbox.json) ───────────────────────────────────────

export function manifestPath(projectDir) {
  return join(projectDir, MANIFEST_NAME);
}

export function readProjectManifest(projectDir) {
  const path = manifestPath(projectDir);
  if (!existsSync(path)) return { addons: {} };
  let parsed;
  try {
    parsed = JSON.parse(readFileSync(path, "utf8"));
  } catch (err) {
    throw new AddonError(`${MANIFEST_NAME} is not valid JSON (${err.message}). Fix or delete it, then re-run.`);
  }
  if (parsed === null || typeof parsed !== "object" || Array.isArray(parsed)) {
    throw new AddonError(`${MANIFEST_NAME} must be a JSON object.`);
  }
  if (parsed.addons === null || typeof parsed.addons !== "object" || Array.isArray(parsed.addons)) parsed.addons = {};
  return parsed;
}

/**
 * Build the manifest record for one installed addon. The single writer for this shape, used by the
 * CLI and by the release build that pre-writes the copy inside each archive — so a hand-unzipped
 * install and a CLI install produce byte-identical records rather than merely similar ones.
 */
export function buildRecord({ version, files, minAppVersion, maxAppVersion }) {
  const paths = {};
  for (const name of [...files.keys()].sort()) paths[name] = sha256(files.get(name));
  return {
    version,
    ...(minAppVersion ? { minAppVersion } : {}),
    ...(maxAppVersion ? { maxAppVersion } : {}),
    files: paths,
  };
}

// Deliberately NOT recorded: the archive's own sha256. It cannot be known when the release job
// pre-writes the copy that ships inside the archive (the hash would have to cover the file it is
// written into), so recording it would make a CLI install and a hand-unzip differ by one field —
// and "identical either way" is the property that keeps the no-tooling path first-class. Nothing is
// lost: the archive hash is verified against the index at install time, and the per-file hashes
// below pin the content afterwards.

export function writeProjectManifest(projectDir, manifest) {
  const ordered = {
    $comment:
      "Written by `knockbox addon`. Commit this: it records which addon versions this game was " +
      "built against, `knockbox addon check` verifies the files against it, and `knockbox pack` " +
      "stamps it into the shipped .kbg.",
    addons: Object.fromEntries(Object.keys(manifest.addons).sort().map((k) => [k, manifest.addons[k]])),
  };
  writeFileSync(manifestPath(projectDir), stringify(ordered));
  return ordered;
}

// ── Inspecting what is on disk ─────────────────────────────────────────────────

/**
 * Compare the installed files against the manifest record. This is what makes the developer guide's
 * "don't fork it" checkable rather than merely requested: a forked file reports MODIFIED, which no
 * amount of reading the instructions would have revealed.
 */
export function inspectInstall(projectDir, record) {
  const files = [];
  for (const [name, expected] of Object.entries(record.files ?? {})) {
    const full = join(projectDir, name);
    if (!existsSync(full)) {
      files.push({ path: name, status: "MISSING" });
      continue;
    }
    const actual = sha256(readFileSync(full));
    files.push({ path: name, status: actual === expected ? "ok" : "MODIFIED", actual, expected });
  }
  return {
    files,
    modified: files.filter((f) => f.status === "MODIFIED").map((f) => f.path),
    missing: files.filter((f) => f.status === "MISSING").map((f) => f.path),
    get clean() { return this.modified.length === 0 && this.missing.length === 0; },
  };
}

// ── Extraction ─────────────────────────────────────────────────────────────────

/**
 * Read an archive into a path -> bytes map, validating every entry BEFORE anything is written.
 * `readStoredZip` covers the container (stored-only, CRC, no duplicate names); `normalizePath`
 * covers the paths (no absolute, no drive letter, no "..", no reserved device names). Both are the
 * same primitives the .kbg reader uses, so an addon archive gets a game package's scrutiny.
 */
export function readArchive(buffer, { expectedSha256 } = {}) {
  if (expectedSha256) {
    const actual = sha256(buffer);
    if (actual !== String(expectedSha256).toLowerCase()) {
      throw new AddonError(
        `archive sha256 mismatch — refusing to install.\n  expected ${expectedSha256}\n  actual   ${actual}\n` +
        "The index is the trust root: a release asset can be replaced in place, so this is the check " +
        "that would catch it.");
    }
  }

  let raw;
  try {
    raw = readStoredZip(buffer);
  } catch (err) {
    throw err instanceof KbgError ? new AddonError(`invalid addon archive: ${err.message}`) : err;
  }

  const files = new Map();
  for (const [rawName, data] of raw) {
    let name;
    try {
      name = normalizePath(rawName, "archive entry");
    } catch (err) {
      throw new AddonError(`invalid addon archive: ${err.message}`);
    }
    if (name.endsWith("/")) continue;             // directory entry, nothing to write
    files.set(name, data);
  }
  if (files.size === 0) throw new AddonError("addon archive contains no files.");
  return files;
}

/**
 * Write the archive's files under `projectDir`.
 *
 * `keepModified` is the whole difference between the two callers. `add` (a developer asking to make
 * the addon pristine) overwrites and reports; `update` (a version change) refuses first and only
 * overwrites once told to. Silently discarding an edit is fine when it was asked for and hostile
 * when it wasn't.
 */
export function extractInto(projectDir, files, { previous, keepModified = false } = {}) {
  // What the last install PUT there, per file. Comparing against this — rather than against the
  // incoming bytes — is what separates "you edited this" from "this file simply changed between
  // versions". Comparing on-disk to incoming conflates them, and during an update that mislabels
  // every legitimately-changed file as a discarded local edit, which is alarming and false.
  const recorded = previous?.files ?? {};

  const written = [];    // newly created
  const updated = [];    // replaced, and we had put the previous contents there ourselves
  const restored = [];   // replaced, and the previous contents were NOT ours — a local edit is lost
  const skipped = [];

  for (const name of [...files.keys()].sort()) {
    const full = join(projectDir, name);
    const data = files.get(name);
    const existed = existsSync(full);
    const onDisk = existed ? sha256(readFileSync(full)) : null;

    // A file we have no record of counts as locally modified: without a recorded hash there is
    // nothing proving we are the ones who put it there, so overwriting it is worth reporting.
    const locallyModified = existed && onDisk !== recorded[name];

    if (locallyModified && keepModified) {
      skipped.push(name);
      continue;
    }
    if (existed && onDisk === sha256(data)) continue;   // already exactly right; leave the mtime alone

    mkdirSync(dirname(full), { recursive: true });
    writeFileSync(full, data);
    if (!existed) written.push(name);
    else if (locallyModified) restored.push(name);
    else updated.push(name);
  }

  // Prune only what a PREVIOUS install recorded and this version no longer ships. Scoped to the
  // recorded list on purpose: a developer's own file sitting in addons/knockbox/ was never ours to
  // delete, and this is the difference between reinstalling an addon and clearing a directory.
  const pruned = [];
  for (const name of Object.keys(previous?.files ?? {})) {
    if (files.has(name)) continue;
    const full = join(projectDir, name);
    if (!existsSync(full)) continue;
    rmSync(full, { force: true });
    pruned.push(name);
  }

  return { written, updated, restored, skipped, pruned };
}

// ── Commands ───────────────────────────────────────────────────────────────────

/** Shared plumbing: resolve the index, pick the release, fetch and validate its archive. */
async function resolveAddon(id, opts) {
  const index = opts.index ?? await (opts.fetchIndex ?? fetchIndex)(opts.indexLocation ?? DEFAULT_INDEX_URL);
  const entry = validateEntry(id, selectRelease(id, indexEntry(index, id), opts.version));
  const url = opts.archiveLocation ?? downloadUrl(entry, opts.downloadBase ?? DEFAULT_DOWNLOAD_BASE);
  const buffer = await (opts.fetchArchive ?? fetchArchive)(url);
  const files = readArchive(buffer, { expectedSha256: entry.source.sha256 });
  return { index, entry, url, buffer, files };
}

/**
 * Install (or reinstall) one addon.
 *
 * Idempotent by design, which makes it the repair path too: run it again and any modified file is
 * restored, any deleted file re-fetched. There is no separate `reset` verb because there does not
 * need to be one — "install the addon" and "make the addon be the addon" are the same request.
 */
export async function add(id, opts = {}) {
  const projectDir = resolve(opts.dir ?? ".");
  const manifest = readProjectManifest(projectDir);
  const previous = manifest.addons[id];

  const { entry, files, buffer } = await resolveAddon(id, opts);

  // A manifest inside the archive is the archive's own record of itself; the project's manifest is
  // the merge of every installed addon, so it is rebuilt here rather than copied over. (Dropping it
  // is also what stops a second hand-unzip from erasing the first addon's record.)
  files.delete(MANIFEST_NAME);

  const result = extractInto(projectDir, files, { previous, keepModified: opts.keepModified === true });

  manifest.addons[id] = buildRecord({
    version: entry.version,
    files,
    minAppVersion: entry.minAppVersion,
    maxAppVersion: entry.maxAppVersion,
  });
  writeProjectManifest(projectDir, manifest);

  return { id, version: entry.version, previousVersion: previous?.version ?? null, ...result };
}

/**
 * Move an addon to a different version.
 *
 * Refuses on a modified file unless `force`, the opposite default from `add` — see extractInto.
 */
export async function update(id, opts = {}) {
  const projectDir = resolve(opts.dir ?? ".");
  const manifest = readProjectManifest(projectDir);
  const previous = manifest.addons[id];
  if (!previous) {
    throw new AddonError(`'${id}' is not installed in ${projectDir}. Use \`knockbox addon add ${id}\`.`);
  }

  const state = inspectInstall(projectDir, previous);
  if (state.modified.length > 0 && opts.force !== true) {
    throw new AddonError(
      `refusing to update '${id}': these files differ from the installed ${previous.version} and the ` +
      `change would be lost:\n  ${state.modified.join("\n  ")}\n` +
      `Re-run with --force to discard them, or \`knockbox addon add ${id}\` to restore ${previous.version} first.`);
  }

  const { entry, files } = await resolveAddon(id, opts);
  files.delete(MANIFEST_NAME);

  if (entry.version === previous.version && opts.force !== true) {
    return { id, version: entry.version, previousVersion: previous.version, upToDate: true,
      written: [], updated: [], restored: [], skipped: [], pruned: [] };
  }

  // `--force --keep-modified` together are not a contradiction: force gets you past the refusal
  // above, keep-modified then spares the specific files you have edited. That is the deliberate
  // "I maintain a fork of one file" case. The record still stores the PUBLISHED hashes, so `check`
  // keeps reporting those files as MODIFIED — which is the honest description of that state.
  const result = extractInto(projectDir, files, { previous, keepModified: opts.keepModified === true });
  manifest.addons[id] = buildRecord({
    version: entry.version,
    files,
    minAppVersion: entry.minAppVersion,
    maxAppVersion: entry.maxAppVersion,
  });
  writeProjectManifest(projectDir, manifest);

  return { id, version: entry.version, previousVersion: previous.version, upToDate: false, ...result };
}

/**
 * Report, without changing anything: is each installed addon intact, is a newer version published,
 * and does it declare itself compatible with `appVersion`.
 *
 * The index is optional — verifying the files is the half that must work offline, since that is the
 * half a developer reaches for when something is already wrong.
 */
export async function check(opts = {}) {
  const projectDir = resolve(opts.dir ?? ".");
  const manifest = readProjectManifest(projectDir);
  const ids = Object.keys(manifest.addons).sort();

  let index = null;
  let indexError = null;
  if (opts.offline !== true) {
    try {
      index = opts.index ?? await (opts.fetchIndex ?? fetchIndex)(opts.indexLocation ?? DEFAULT_INDEX_URL);
    } catch (err) {
      // One unreachable index is a reported error, never a failed run — the local verification below
      // is still worth everything it was worth offline.
      indexError = err.message;
    }
  }

  const addons = ids.map((id) => {
    const record = manifest.addons[id];
    const state = inspectInstall(projectDir, record);
    const offered = index?.addons?.[id];
    const latest = typeof offered?.version === "string" ? offered.version : null;
    return {
      id,
      version: record.version,
      latest,
      updateAvailable: latest !== null && latest !== record.version,
      modified: state.modified,
      missing: state.missing,
      clean: state.clean,
      incompatible: incompatibility(record, opts.appVersion),
    };
  });

  return { projectDir, addons, indexError, empty: ids.length === 0 };
}

/**
 * Whether an addon declares itself unable to run on `appVersion`. Mirrors the marketplace rule: both
 * bounds inclusive, and a bound that cannot be PARSED counts as incompatible, because a constraint
 * we cannot read is not the absence of a constraint.
 */
export function incompatibility(record, appVersion) {
  if (!appVersion) return null;
  const app = parseSemVer(appVersion);
  if (!app) return `could not parse --app-version '${appVersion}'`;

  const { minAppVersion: min, maxAppVersion: max } = record;
  if (min) {
    const bound = parseSemVer(min);
    if (!bound) return `declares an unreadable minAppVersion '${min}'`;
    if (compareSemVer(app, bound) < 0) return `needs KnockBox >= ${min} (this server is ${appVersion})`;
  }
  if (max) {
    const bound = parseSemVer(max);
    if (!bound) return `declares an unreadable maxAppVersion '${max}'`;
    if (compareSemVer(app, bound) > 0) return `supports KnockBox <= ${max} (this server is ${appVersion})`;
  }
  return null;
}

/** Enough of semver 2.0.0 to order releases and prereleases. Mirrors Marketplace/PluginVersion.cs. */
export function parseSemVer(text) {
  const m = /^(\d+)\.(\d+)\.(\d+)(?:-([0-9A-Za-z.-]+))?(?:\+[0-9A-Za-z.-]+)?$/.exec(String(text ?? "").trim());
  if (!m) return null;
  return { major: +m[1], minor: +m[2], patch: +m[3], prerelease: m[4] ?? null };
}

export function compareSemVer(a, b) {
  if (a.major !== b.major) return a.major < b.major ? -1 : 1;
  if (a.minor !== b.minor) return a.minor < b.minor ? -1 : 1;
  if (a.patch !== b.patch) return a.patch < b.patch ? -1 : 1;
  // A prerelease sorts BEFORE its release: 1.0.0-rc.1 < 1.0.0. String comparison gets this backwards.
  if (a.prerelease === b.prerelease) return 0;
  if (a.prerelease === null) return 1;
  if (b.prerelease === null) return -1;

  const as = a.prerelease.split("."), bs = b.prerelease.split(".");
  for (let i = 0; i < Math.max(as.length, bs.length); i++) {
    const x = as[i], y = bs[i];
    if (x === undefined) return -1;
    if (y === undefined) return 1;
    const xn = /^\d+$/.test(x), yn = /^\d+$/.test(y);
    if (xn && yn) { if (+x !== +y) return +x < +y ? -1 : 1; continue; }
    if (xn !== yn) return xn ? -1 : 1;   // numeric identifiers sort below alphanumeric
    if (x !== y) return x < y ? -1 : 1;
  }
  return 0;
}

/** What is installed, from the manifest alone — no network, no hashing. */
export function list(opts = {}) {
  const projectDir = resolve(opts.dir ?? ".");
  const manifest = readProjectManifest(projectDir);
  return {
    projectDir,
    addons: Object.keys(manifest.addons).sort().map((id) => ({
      id,
      version: manifest.addons[id].version,
      fileCount: Object.keys(manifest.addons[id].files ?? {}).length,
    })),
  };
}

/** Uninstall: remove exactly the recorded files, then the record. Nothing else. */
export function remove(id, opts = {}) {
  const projectDir = resolve(opts.dir ?? ".");
  const manifest = readProjectManifest(projectDir);
  const record = manifest.addons[id];
  if (!record) throw new AddonError(`'${id}' is not installed in ${projectDir}.`);

  const removed = [];
  for (const name of Object.keys(record.files ?? {})) {
    const full = join(projectDir, name);
    if (!existsSync(full)) continue;
    rmSync(full, { force: true });
    removed.push(name);
  }
  // Tidy up directories we emptied, but never one that still holds anything (a developer's own file
  // in addons/knockbox/ keeps the folder, and that is correct). rmdirSync, not rmSync: it refuses a
  // non-empty directory, so "only if empty" is enforced by the call rather than by our own check
  // racing it — and a recursive delete here would be exactly the bug this guard exists to prevent.
  const dirs = new Set(Object.keys(record.files ?? {}).map((n) => dirname(join(projectDir, n))));
  for (const start of [...dirs].sort((a, b) => b.length - a.length)) {
    // Climb: emptying addons/knockbox/ usually leaves addons/ empty too. Stops at the project root,
    // which is never removed even when it ends up empty.
    let dir = start;
    while (dir !== projectDir && dir.startsWith(projectDir)) {
      let remaining;
      try { remaining = readdirSync(dir); } catch { break; }   // already gone
      if (remaining.length > 0) break;                         // holds something that was not ours
      try { rmdirSync(dir); } catch { break; }
      dir = dirname(dir);
    }
  }

  delete manifest.addons[id];
  writeProjectManifest(projectDir, manifest);
  return { id, version: record.version, removed };
}

// ── Building an archive (used by the release job, and by the tests as a fixture) ──

/**
 * Build one addon's release archive from a repo checkout, per clients/addons.manifest.json.
 *
 * Emits PROJECT-RELATIVE paths plus a LICENSE and a pre-written knockbox.json, so the archive is a
 * complete install on its own. The record is built by the same `buildRecord` the CLI uses, which is
 * what makes the two installs byte-identical instead of just equivalent.
 */
export function buildAddonArchive({ repoRoot, id, addon, sdkVersion, minAppVersion, maxAppVersion, license }) {
  const installTo = normalizePath(addon.installTo, "installTo");
  const root = join(repoRoot, ...addon.root.split("/"));
  if (!existsSync(root)) throw new AddonError(`addon '${id}': root ${addon.root} does not exist.`);

  const sources = new Map();   // archive path -> absolute source path
  const declared = addon.files ?? ["**"];
  if (declared.length === 1 && declared[0] === "**") {
    for (const abs of walk(root)) {
      sources.set(`${installTo}/${relative(root, abs).split(sep).join("/")}`, abs);
    }
  } else {
    for (const name of declared) {
      const abs = join(root, ...name.split("/"));
      if (!existsSync(abs)) throw new AddonError(`addon '${id}': declared file ${name} not found under ${addon.root}.`);
      sources.set(`${installTo}/${normalizePath(name, "file")}`, abs);
    }
  }
  for (const doc of addon.docs ?? []) {
    const abs = join(repoRoot, ...doc.split("/"));
    if (!existsSync(abs)) throw new AddonError(`addon '${id}': declared doc ${doc} not found.`);
    sources.set(`${installTo}/${doc.split("/").pop()}`, abs);
  }

  // Repo-relative paths whose declared version the archive must carry for real, per the manifest.
  const versionFiles = new Set(addon.versionFiles ?? []);

  const files = new Map();
  for (const name of [...sources.keys()].sort()) {
    const source = sources.get(name);
    const repoRelative = relative(repoRoot, source).split(sep).join("/");
    const bytes = readFileSync(source);
    files.set(name, versionFiles.has(repoRelative) ? stampVersion(name, bytes, sdkVersion) : bytes);
  }
  // Every addon carries its terms. A copied-out folder used to carry none, since the only LICENSE
  // in the repo was at the root and nobody copies the root.
  if (license) files.set(`${installTo}/LICENSE`, Buffer.from(license, "utf8"));

  const record = buildRecord({ version: sdkVersion, files, minAppVersion, maxAppVersion });

  const manifest = {
    $comment:
      "Written by `knockbox addon`. Commit this: it records which addon versions this game was " +
      "built against, `knockbox addon check` verifies the files against it, and `knockbox pack` " +
      "stamps it into the shipped .kbg.",
    addons: { [id]: record },
  };
  files.set(MANIFEST_NAME, Buffer.from(stringify(manifest), "utf8"));

  const entries = [...files.keys()].sort().map((name) => ({ name, data: files.get(name) }));
  const buffer = writeStoredZip(entries);
  return { buffer, files, sha256: sha256(buffer), record };
}

/**
 * Rewrite the version a file declares, for the archive only.
 *
 * Handles the two formats that appear in `versionFiles`: a `package.json` `"version"` field and a
 * Godot `plugin.cfg` `version="…"` line. An unrecognised extension is returned unchanged rather than
 * guessed at — silently failing to stamp is better than corrupting a file, and the tests assert the
 * archive's plugin.cfg carries the real version, so a format we cannot stamp fails loudly there.
 */
export function stampVersion(name, bytes, version) {
  const text = bytes.toString("utf8");

  if (name.endsWith(".json")) {
    const stamped = text.replace(/("version"\s*:\s*")[^"]*(")/, `$1${version}$2`);
    return stamped === text ? bytes : Buffer.from(stamped, "utf8");
  }
  if (name.endsWith(".cfg")) {
    const stamped = text.replace(/^(version\s*=\s*")[^"]*(")/m, `$1${version}$2`);
    return stamped === text ? bytes : Buffer.from(stamped, "utf8");
  }
  return bytes;
}

function walk(dir) {
  const out = [];
  for (const name of readdirSync(dir).sort()) {
    const full = join(dir, name);
    if (statSync(full).isDirectory()) out.push(...walk(full));
    else out.push(full);
  }
  return out;
}

export { pathToFileURL };
