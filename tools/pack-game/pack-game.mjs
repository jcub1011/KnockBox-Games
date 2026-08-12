#!/usr/bin/env node
/*
 * KnockBox game packer — packages any engine's build into a single drop-in `.kbg` file.
 *
 *   node pack-game.mjs --in <built-dir> --manifest <GAME.json> \
 *        [--out <file.kbg>] [--dir <dir>] [--build "<cmd>"] [--cwd <dir>] \
 *        [--thumbnail <file>] [--version <s>] [--quality <0-11>] [--no-clean]
 *
 * "Build" (producing a folder of static files) is engine-specific and optional:
 *   • Vite/Phaser → `--build "npm run build" --in dist`
 *   • Godot/Unity → export from the editor first, then `--in build/web` (no --build)
 *   • hand-written → `--in . --manifest GAME.json` (no --build)
 *
 * "Assemble" is universal and is what this tool owns: validate the manifest against the platform
 * contract, then emit `<id>.kbg` — a single file an administrator copies into the server's games
 * directory, where it installs itself (see docs/KBG_FORMAT.md). `--dir` writes the older
 * uncompressed `<id>/` folder layout instead, which is useful for inspecting what was packaged;
 * the server still supports plain folders.
 *
 * Validation here covers the server's discovery rules in
 * KnockBox.Server/Games/GameCatalog.cs (Discover) and the KnockBox.Contracts
 * GameManifest record, and is intentionally STRICTER: the server leaves `name` and
 * `maxPlayers` to deserialization, while the packer rejects an empty name and a
 * non-positive/non-integer maxPlayers so authors fail fast. For `serverAuthority`
 * (server-authoritative games) the packer additionally runs two checks the catalog
 * can't do cheaply: a static import scan (single-file rule) and a load check that
 * dynamic-imports the module and asserts its exports — the developer's own code, run
 * in their own packer. Keep the two in sync: if the contract or discovery rules
 * change, update both.
 */

import { execSync } from "node:child_process";
import { cpSync, existsSync, mkdirSync, readFileSync, readdirSync, rmSync, statSync, writeFileSync } from "node:fs";
import { dirname, isAbsolute, join, relative, resolve, sep } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import { DEFAULT_QUALITY, KbgError, packKbg, readKbg } from "./kbg.mjs";

const toolDir = dirname(fileURLToPath(import.meta.url));
// tools/pack-game/ → repo root → games/. The default target is this platform's games dir, so the
// common dev loop (pack, then watch it hot-reload) stays a single command.
export const defaultOut = resolve(toolDir, "..", "..", "games");

const VERSION = "0.2.0";

// Max serverAuthority module size. Mirrors the server default
// (AuthorityOptions.DefaultMaxScriptBytes / KnockBox:AuthorityMaxScriptBytes) — keep in sync.
export const AUTHORITY_MAX_SCRIPT_BYTES = 1_048_576;

// Max authorityWords dictionary file size. Mirrors the server default
// (AuthorityOptions.DefaultMaxWordFileBytes / KnockBox:AuthorityMaxWordFileBytes) — keep in sync.
export const AUTHORITY_MAX_WORD_FILE_BYTES = 33_554_432;

/** Thrown for any contract/usage error so the CLI can report it and exit non-zero. */
export class PackError extends Error {}

/**
 * Static scan for the single-file rule: the SERVER configures no module loader, so any
 * `import` / `export … from` inside authority.js fails at lobby creation there. Catching it here
 * (and in knockbox-local.js's URL loader — keep the two in sync) beats a browser dev loop where a
 * relative import happily resolves. Authors with multi-file logic bundle (esbuild/rollup) first.
 */
export function scanAuthorityImports(source) {
  const lines = String(source).split("\n");
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    if (/^\s*import[\s('"]/.test(line) || /^\s*export\s+[^;]*\sfrom\s*['"]/.test(line)) {
      throw new PackError(
        `serverAuthority module must be single-file (the server has no module loader) — bundle your imports. ` +
        `Offending line ${i + 1}: ${line.trim()}`);
    }
  }
}

/**
 * Load check: dynamic-import the authority module (the developer's own code, in their own packer)
 * and assert the contract shape — createAuthority is a function, and config (when present) is a
 * plain object with valid perRecipient/tickHz. Catches "forgot to export" long before a server
 * rejects the lobby.
 */
export async function checkAuthorityModule(authorityPath) {
  let mod;
  try {
    // Cache-bust so repeated packs (and tests) see the current file, not Node's module cache.
    mod = await import(`${pathToFileURL(authorityPath).href}?v=${Date.now()}`);
  } catch (err) {
    throw new PackError(`serverAuthority module failed to load: ${err.message}`);
  }
  if (typeof mod.createAuthority !== "function") {
    throw new PackError("serverAuthority module must export a createAuthority(kb) function.");
  }
  const config = mod.config;
  if (config !== undefined) {
    if (config === null || typeof config !== "object" || Array.isArray(config)) {
      throw new PackError("serverAuthority 'config' export must be a plain object when present.");
    }
    if (config.perRecipient !== undefined && typeof config.perRecipient !== "boolean") {
      throw new PackError("serverAuthority config.perRecipient must be a boolean when present.");
    }
    if (config.tickHz !== undefined && (typeof config.tickHz !== "number" || !Number.isFinite(config.tickHz) || config.tickHz < 0)) {
      throw new PackError("serverAuthority config.tickHz must be a finite non-negative number when present.");
    }
  }
}

/**
 * Validate a parsed manifest against the platform contract — covering
 * GameCatalog.Discover()'s rules (plus stricter `name`/`maxPlayers` checks) so authors
 * fail fast here instead of having the game silently skipped at runtime. Throws
 * PackError on the first violation.
 * @returns the resolved absolute thumbnail source path (or null if none declared).
 */
export function validate(manifest, manifestPath, inDir) {
  if (!manifest || typeof manifest !== "object") throw new PackError("GAME.json did not parse to an object.");

  const { id, name, entry, maxPlayers, crossOriginIsolated } = manifest;

  if (typeof id !== "string" || id.trim() === "") throw new PackError("GAME.json: 'id' is required.");
  // The installed folder is named <id> and must equal it, so id must be one safe segment.
  if (/[\\/]/.test(id) || id === "." || id === ".." || id.includes("..")) {
    throw new PackError(`GAME.json: 'id' must be a single path segment (no slashes or "..": got "${id}").`);
  }
  if (typeof name !== "string" || name.trim() === "") throw new PackError("GAME.json: 'name' is required.");
  if (typeof entry !== "string" || entry.trim() === "") throw new PackError("GAME.json: 'entry' is required.");
  if (!Number.isInteger(maxPlayers) || maxPlayers <= 0) {
    throw new PackError("GAME.json: 'maxPlayers' must be an integer greater than 0.");
  }
  if (crossOriginIsolated !== undefined && typeof crossOriginIsolated !== "boolean") {
    throw new PackError("GAME.json: 'crossOriginIsolated' must be a boolean when present.");
  }
  // version (optional): the build label. Becomes KBG.json's `version` unless --version overrides it,
  // and is what a marketplace compares an installed copy against, so it has to be a string — a bare
  // number here would land in the header as a JSON number and fail to deserialize server-side.
  if (manifest.version !== undefined && (typeof manifest.version !== "string" || manifest.version.trim() === "")) {
    throw new PackError("GAME.json: 'version' must be a non-empty string when present (e.g. \"1.0.0\").");
  }

  // The entry must resolve to a file inside the built dir — never escape it (path traversal).
  const inFull = resolve(inDir);
  const entryFull = resolve(inFull, entry);
  const rel = relative(inFull, entryFull);
  if (rel === "" || rel.startsWith("..") || isAbsolute(rel)) {
    throw new PackError(`GAME.json: 'entry' (${entry}) escapes the built folder.`);
  }
  if (!existsSync(entryFull) || !statSync(entryFull).isFile()) {
    throw new PackError(`entry file not found in --in: ${entry} (looked in ${inDir}).`);
  }

  // serverAuthority (optional): the per-game opt-in to server-authoritative mode. Same traversal
  // guard as entry, plus existence, a size cap, and the single-file import scan — mirroring
  // GameCatalog.Discover(), which SKIPS the whole game on any violation (never a silent downgrade
  // to host mode), so authors must fail here instead. The load check (dynamic import) is async and
  // runs in pack(), not here.
  if (manifest.serverAuthority !== undefined) {
    const authority = manifest.serverAuthority;
    if (typeof authority !== "string" || authority.trim() === "") {
      throw new PackError("GAME.json: 'serverAuthority' must be a non-empty string when present.");
    }
    if (!authority.toLowerCase().endsWith(".js")) {
      throw new PackError("GAME.json: 'serverAuthority' must be a .js module (the WASM backend is not yet supported).");
    }
    const authorityFull = resolve(inFull, authority);
    const authorityRel = relative(inFull, authorityFull);
    if (authorityRel === "" || authorityRel.startsWith("..") || isAbsolute(authorityRel)) {
      throw new PackError(`GAME.json: 'serverAuthority' (${authority}) escapes the built folder.`);
    }
    if (!existsSync(authorityFull) || !statSync(authorityFull).isFile()) {
      throw new PackError(`serverAuthority module not found in --in: ${authority} (looked in ${inDir}).`);
    }
    const size = statSync(authorityFull).size;
    if (size > AUTHORITY_MAX_SCRIPT_BYTES) {
      throw new PackError(`serverAuthority module is ${size} bytes (max ${AUTHORITY_MAX_SCRIPT_BYTES}).`);
    }
    scanAuthorityImports(readFileSync(authorityFull, "utf8"));
  }

  // authorityWords (optional): immutable dictionaries the authority module queries via kb.words.
  // Same traversal / existence / size treatment as serverAuthority, and it REQUIRES serverAuthority
  // (word data is only reachable server-side). Mirrors GameCatalog.ValidateAuthorityWords, which
  // SKIPS the whole game on any violation — so authors must fail here instead.
  if (manifest.authorityWords !== undefined) {
    const words = manifest.authorityWords;
    if (words === null || typeof words !== "object" || Array.isArray(words)) {
      throw new PackError("GAME.json: 'authorityWords' must be an object mapping keys to { file, caseInsensitive? }.");
    }
    if (typeof manifest.serverAuthority !== "string" || manifest.serverAuthority.trim() === "") {
      throw new PackError("GAME.json: 'authorityWords' requires 'serverAuthority' to be set (word data is server-only).");
    }
    for (const [key, decl] of Object.entries(words)) {
      if (key.trim() === "") throw new PackError("GAME.json: an 'authorityWords' key must be a non-empty string.");
      if (!decl || typeof decl !== "object" || Array.isArray(decl)) {
        throw new PackError(`GAME.json: authorityWords '${key}' must be an object { file, caseInsensitive? }.`);
      }
      if (typeof decl.file !== "string" || decl.file.trim() === "") {
        throw new PackError(`GAME.json: authorityWords '${key}' must have a non-empty 'file'.`);
      }
      if (decl.caseInsensitive !== undefined && typeof decl.caseInsensitive !== "boolean") {
        throw new PackError(`GAME.json: authorityWords '${key}' 'caseInsensitive' must be a boolean when present.`);
      }
      const fileFull = resolve(inFull, decl.file);
      const fileRel = relative(inFull, fileFull);
      if (fileRel === "" || fileRel.startsWith("..") || isAbsolute(fileRel)) {
        throw new PackError(`GAME.json: authorityWords '${key}' file (${decl.file}) escapes the built folder.`);
      }
      if (!existsSync(fileFull) || !statSync(fileFull).isFile()) {
        throw new PackError(`authorityWords '${key}' file not found in --in: ${decl.file} (looked in ${inDir}).`);
      }
      const wsize = statSync(fileFull).size;
      if (wsize > AUTHORITY_MAX_WORD_FILE_BYTES) {
        throw new PackError(`authorityWords '${key}' file is ${wsize} bytes (max ${AUTHORITY_MAX_WORD_FILE_BYTES}).`);
      }
    }
  }

  // Thumbnail (optional). Resolve relative to the manifest's folder so metadata can
  // live outside the build, then confirm it exists before we try to copy it.
  let thumbSrc = null;
  if (manifest.thumbnail) {
    // The thumbnail is written to <id>/<thumbnail> and served at /games/<id>/<thumbnail>,
    // so its NAME must stay inside the game folder — same traversal guard as entry. (The
    // SOURCE may live outside the build; only the output location is constrained.)
    if (isAbsolute(manifest.thumbnail) || relative(".", resolve(".", manifest.thumbnail)).startsWith("..")) {
      throw new PackError(`GAME.json: 'thumbnail' (${manifest.thumbnail}) escapes the game folder.`);
    }
    thumbSrc = resolve(dirname(manifestPath), manifest.thumbnail);
    if (!existsSync(thumbSrc)) throw new PackError(`thumbnail declared in GAME.json not found: ${manifest.thumbnail}`);
  }
  return thumbSrc;
}

/** Recursively list files under a directory as absolute paths. */
function walk(dir) {
  return readdirSync(dir, { withFileTypes: true }).flatMap((e) =>
    e.isDirectory() ? walk(join(dir, e.name)) : [join(dir, e.name)]);
}

/**
 * Run the build (if any), validate the manifest, and resolve the exact set of files that make up
 * the game — the shared front half of both output modes. Synchronous: the one async check a game
 * can need (load-checking a serverAuthority module) is awaited by pack() around this.
 * @returns {{ manifest: object, manifestPath: string, inDir: string, contents: Map<string,string> }}
 *          `contents` maps a logical game-folder path to the absolute file it comes from.
 */
function plan(opts) {
  if (!opts.in) throw new PackError("--in <built-dir> is required.");
  if (!opts.manifest) throw new PackError("--manifest <GAME.json> is required.");

  const manifestPath = resolve(opts.manifest);
  if (!existsSync(manifestPath)) throw new PackError(`manifest not found: ${opts.manifest}`);

  let manifest;
  try {
    manifest = JSON.parse(readFileSync(manifestPath, "utf8"));
  } catch (err) {
    throw new PackError(`could not parse ${opts.manifest} as JSON: ${err.message}`);
  }

  if (opts.build) {
    const cwd = opts.cwd ? resolve(opts.cwd) : process.cwd();
    console.log(`• building: ${opts.build}`);
    execSync(opts.build, { cwd, stdio: "inherit" });
  }

  const inDir = resolve(opts.in);
  if (!existsSync(inDir) || !statSync(inDir).isDirectory()) {
    throw new PackError(`--in is not a directory: ${opts.in}${opts.build ? " (did the build produce it?)" : ""}`);
  }

  // Validate the contract; returns the declared thumbnail's source (or null).
  let thumbSrc = validate(manifest, manifestPath, inDir);

  // --thumbnail overrides only the SOURCE file; the output name is always whatever
  // GAME.json references, since that is what the catalog serves. An override with no
  // declared thumbnail has nothing to wire up.
  if (opts.thumbnail) {
    if (!manifest.thumbnail) throw new PackError("--thumbnail given but GAME.json declares no 'thumbnail' to override.");
    thumbSrc = resolve(opts.thumbnail);
    if (!existsSync(thumbSrc)) throw new PackError(`--thumbnail not found: ${opts.thumbnail}`);
  }

  // Built files first, then the manifest, then the thumbnail — later writes win, so an explicit
  // --manifest/--thumbnail always beats a stale copy inside the build. (Same precedence the
  // folder output has always had.)
  const contents = new Map();
  for (const abs of walk(inDir)) contents.set(relative(inDir, abs).split(sep).join("/"), abs);
  contents.set("GAME.json", manifestPath);
  if (thumbSrc) contents.set(manifest.thumbnail.split(sep).join("/"), thumbSrc);

  return { manifest, manifestPath, inDir, contents };
}

/** Assemble the plain `<out>/<id>/` folder layout (debug / legacy output). */
function emitFolder(p, opts) {
  const outRoot = resolve(opts.dir);
  const target = join(outRoot, p.manifest.id); // folder name === id (platform requirement)

  if (opts.clean !== false) rmSync(target, { recursive: true, force: true });
  mkdirSync(target, { recursive: true });

  for (const [logical, abs] of p.contents) {
    const dest = join(target, logical);
    mkdirSync(dirname(dest), { recursive: true });
    cpSync(abs, dest);
  }
  return { target };
}

/** Assemble the single-file `.kbg` package (default output). */
function emitKbg(p, opts) {
  const entries = [...p.contents].map(([logical, abs]) => ({ path: logical, data: readFileSync(abs) }));

  let built;
  try {
    built = packKbg({
      entries,
      id: p.manifest.id,
      name: p.manifest.name,
      // Default the header's build label to the manifest's own `version`. Two version strings that
      // can silently disagree is a trap for anything reading the package — the marketplace compares
      // the catalog's version (derived from GAME.json) against what the server has installed, so
      // KBG.json claiming something else would make an up-to-date package look stale. An explicit
      // --version still wins, for builds that want a label the manifest doesn't carry.
      version: opts.version ?? p.manifest.version,
      packedBy: `knockbox-pack ${VERSION}`,
      packedAt: new Date().toISOString(),
      quality: opts.quality ?? DEFAULT_QUALITY,
    });
  } catch (err) {
    // Surface format-contract problems as ordinary usage errors, not stack traces.
    if (err instanceof KbgError) throw new PackError(err.message);
    throw err;
  }

  const target = resolveKbgPath(opts, p.manifest.id);
  mkdirSync(dirname(target), { recursive: true });
  writeFileSync(target, built.buffer);

  // Read the package straight back: verifies every CRC, that the header's file list is closed in
  // both directions, and that each payload decompresses to its declared size and hash. Cheap
  // (decompression is ~1000x faster than compression) and it means a corrupt write never ships.
  try {
    readKbg(readFileSync(target));
  } catch (err) {
    throw new PackError(`the package written to ${target} failed verification: ${err.message}`);
  }

  return { target, stats: built.stats, header: built.header };
}

/**
 * Decide where the .kbg goes. `--out` may name the file itself or an existing directory; with no
 * --out it lands in this platform's games/ dir so the dev loop stays one command.
 */
function resolveKbgPath(opts, id) {
  if (!opts.out) return join(defaultOut, `${id}.kbg`);
  const out = resolve(opts.out);
  if (opts.out.endsWith("/") || opts.out.endsWith("\\") || (existsSync(out) && statSync(out).isDirectory())) {
    return join(out, `${id}.kbg`);
  }
  if (!out.toLowerCase().endsWith(".kbg")) {
    throw new PackError(
      `--out must name a .kbg file or an existing directory (got "${opts.out}"). ` +
      "To write the uncompressed folder layout instead, use --dir.");
  }
  return out;
}

/**
 * Run the build (if any), validate, and emit the package.
 * @returns { target, manifest, stats?, header? } — target is the .kbg file, or the <id>/ folder
 *          when --dir was given.
 */
export async function pack(opts) {
  if (opts.dir && opts.out) throw new PackError("--out and --dir are mutually exclusive.");
  if (opts.clean === false && !opts.dir) throw new PackError("--no-clean only applies to --dir output.");
  if (opts.quality !== undefined && (!Number.isInteger(opts.quality) || opts.quality < 0 || opts.quality > 11)) {
    throw new PackError("--quality must be an integer from 0 to 11.");
  }

  const p = plan(opts);

  // The two checks the static pass can't do: the module actually loads, and exports the contract.
  // Async, hence pack()'s promise — it dynamic-imports the developer's own module in their own packer.
  if (p.manifest.serverAuthority) {
    await checkAuthorityModule(resolve(p.inDir, p.manifest.serverAuthority));
  }

  const emitted = opts.dir ? emitFolder(p, opts) : emitKbg(p, opts);
  return { ...emitted, manifest: p.manifest };
}

const HELP = `KnockBox game packer ${VERSION}

Usage:
  node pack-game.mjs --in <built-dir> --manifest <GAME.json> [options]

Packages a game into a single <id>.kbg file. Copy it into a KnockBox server's games
directory and it installs itself — no restart. See docs/KBG_FORMAT.md.

Options:
  --in <dir>          Folder of built static files to package (required).
  --manifest <file>   Path to GAME.json (required); copied verbatim into the package.
  --out <file|dir>    Where to write the .kbg (default: this platform's games/<id>.kbg).
                      A directory gets <dir>/<id>.kbg.
  --dir <dir>         Instead of a .kbg, write the uncompressed <dir>/<id>/ folder layout.
  --build "<cmd>"     Optional build command to run before assembling (in --cwd).
  --cwd <dir>         Working directory for --build (default: current directory).
  --thumbnail <file>  Thumbnail source override (output name stays manifest.thumbnail).
  --version <s>       Stamp a game version into the package (shown in server logs).
                      Defaults to GAME.json's own "version" when it declares one.
  --quality <0-11>    Brotli quality (default ${DEFAULT_QUALITY} = max). Lower is much faster to pack.
  --no-clean          With --dir: do not wipe the target <id>/ folder first.
  -h, --help          Show this help.`;

/** Minimal flag parser: --key value, plus boolean flags. Zero dependencies. */
export function parseArgs(argv) {
  const opts = {};
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    switch (a) {
      case "--in": opts.in = argv[++i]; break;
      case "--manifest": opts.manifest = argv[++i]; break;
      case "--out": opts.out = argv[++i]; break;
      case "--dir": opts.dir = argv[++i]; break;
      case "--build": opts.build = argv[++i]; break;
      case "--cwd": opts.cwd = argv[++i]; break;
      case "--thumbnail": opts.thumbnail = argv[++i]; break;
      case "--version": opts.version = argv[++i]; break;
      case "--quality": opts.quality = Number(argv[++i]); break;
      case "--no-clean": opts.clean = false; break;
      case "-h": case "--help": opts.help = true; break;
      default: throw new PackError(`unknown argument: ${a}`);
    }
  }
  return opts;
}

const size = (n) => (n < 1024 ? `${n} B`
  : n < 1048576 ? `${(n / 1024).toFixed(1)} KiB`
    : `${(n / 1048576).toFixed(2)} MiB`);

async function cli() {
  try {
    const opts = parseArgs(process.argv.slice(2));
    if (opts.help) { console.log(HELP); return; }
    const { target, manifest, stats } = await pack(opts);
    if (stats) {
      const pct = stats.raw === 0 ? 0 : Math.round((1 - stats.packed / stats.raw) * 100);
      console.log(`✓ packed "${manifest.name}" → ${target}`);
      console.log(`  ${size(stats.raw)} → ${size(stats.packed)} (${pct}% smaller, ${stats.compressed} file(s) Brotli-compressed)`);
      console.log(`  copy ${manifest.id}.kbg into your server's games dir — it installs itself, no restart.`);
    } else {
      console.log(`✓ packed "${manifest.name}" → ${target}`);
      console.log(`  drop ${manifest.id}/ into your server's games dir (it hot-reloads — no restart).`);
    }
  } catch (err) {
    if (err instanceof PackError) {
      console.error(`✗ ${err.message}`);
      process.exit(1);
    }
    throw err; // unexpected: surface with a stack (the unhandled rejection exits non-zero)
  }
}

// Run only when invoked directly, not when imported by tests.
if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  cli();
}
