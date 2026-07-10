#!/usr/bin/env node
/*
 * KnockBox game packer — assembles a drop-in `games/<id>/` folder from any engine.
 *
 *   node pack-game.mjs --in <built-dir> --manifest <GAME.json> \
 *        [--out <gamesDir>] [--build "<cmd>"] [--cwd <dir>] \
 *        [--thumbnail <file>] [--no-clean]
 *
 * "Build" (producing a folder of static files) is engine-specific and optional:
 *   • Vite/Phaser → `--build "npm run build" --in dist`
 *   • Godot/Unity → export from the editor first, then `--in build/web` (no --build)
 *   • hand-written → `--in . --manifest GAME.json` (no --build)
 *
 * "Assemble" is universal and is what this tool owns: validate the manifest against
 * the platform contract, then lay down `<out>/<id>/` = built files + GAME.json +
 * thumbnail. The output folder is named `<id>` because the platform serves assets at
 * /games/{id}/… and requires the folder name to equal the manifest id.
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
import { cpSync, existsSync, mkdirSync, readFileSync, rmSync, statSync } from "node:fs";
import { dirname, isAbsolute, join, relative, resolve } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const toolDir = dirname(fileURLToPath(import.meta.url));
// tools/pack-game/ → repo root → games/. The default target is this platform's games dir.
export const defaultOut = resolve(toolDir, "..", "..", "games");

// Max serverAuthority module size. Mirrors the server default
// (AuthorityOptions.DefaultMaxScriptBytes / KnockBox:AuthorityMaxScriptBytes) — keep in sync.
export const AUTHORITY_MAX_SCRIPT_BYTES = 1_048_576;

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
  // The output folder is named <id> and must equal it, so id must be one safe segment.
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

/**
 * Run the build (if any), validate, and assemble the drop-in folder. Async because a declared
 * serverAuthority module is also load-checked via dynamic import.
 * @returns { target, manifest } where target is the assembled <out>/<id> path.
 */
export async function pack(opts) {
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

  // The two checks the static pass can't do: the module actually loads, and exports the contract.
  if (manifest.serverAuthority) {
    await checkAuthorityModule(resolve(inDir, manifest.serverAuthority));
  }

  // --thumbnail overrides only the SOURCE file; the output name is always whatever
  // GAME.json references, since that is what the catalog serves. An override with no
  // declared thumbnail has nothing to wire up.
  if (opts.thumbnail) {
    if (!manifest.thumbnail) throw new PackError("--thumbnail given but GAME.json declares no 'thumbnail' to override.");
    thumbSrc = resolve(opts.thumbnail);
    if (!existsSync(thumbSrc)) throw new PackError(`--thumbnail not found: ${opts.thumbnail}`);
  }

  const outRoot = opts.out ? resolve(opts.out) : defaultOut;
  const target = join(outRoot, manifest.id); // folder name === id (platform requirement)

  if (opts.clean !== false) rmSync(target, { recursive: true, force: true });
  mkdirSync(target, { recursive: true });

  // Built static files, then the manifest, then the thumbnail (under its declared name).
  cpSync(inDir, target, { recursive: true });
  cpSync(manifestPath, join(target, "GAME.json"));
  if (thumbSrc) {
    // The declared name may be nested (e.g. "assets/thumb.svg"); cpSync of a file won't
    // create missing parents, so make the dir first.
    const thumbDest = join(target, manifest.thumbnail);
    mkdirSync(dirname(thumbDest), { recursive: true });
    cpSync(thumbSrc, thumbDest);
  }

  return { target, manifest };
}

const HELP = `KnockBox game packer

Usage:
  node pack-game.mjs --in <built-dir> --manifest <GAME.json> [options]

Options:
  --in <dir>          Folder of built static files to package (required).
  --manifest <file>   Path to GAME.json (required); copied verbatim into the output.
  --out <dir>         Target games directory (default: this platform's games/).
  --build "<cmd>"     Optional build command to run before assembling (in --cwd).
  --cwd <dir>         Working directory for --build (default: current directory).
  --thumbnail <file>  Thumbnail source override (output name stays manifest.thumbnail).
  --no-clean          Do not wipe the target <id>/ folder first (default: wipe).
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
      case "--build": opts.build = argv[++i]; break;
      case "--cwd": opts.cwd = argv[++i]; break;
      case "--thumbnail": opts.thumbnail = argv[++i]; break;
      case "--no-clean": opts.clean = false; break;
      case "-h": case "--help": opts.help = true; break;
      default: throw new PackError(`unknown argument: ${a}`);
    }
  }
  return opts;
}

async function cli() {
  try {
    const opts = parseArgs(process.argv.slice(2));
    if (opts.help) { console.log(HELP); return; }
    const { target, manifest } = await pack(opts);
    console.log(`✓ packed "${manifest.name}" → ${target}`);
    console.log(`  drop ${manifest.id}/ into KnockBox-Games/games/ (it hot-reloads — no restart).`);
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
