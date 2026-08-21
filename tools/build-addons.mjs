#!/usr/bin/env node
/**
 * Build the addon release archives and the ADDONS.json index from clients/addons.manifest.json.
 *
 *   node tools/build-addons.mjs [--out <dir>] [--tag addons-v1.0.0] [--repo owner/name]
 *
 * Writes <out>/knockbox-<id>-<version>.zip for every addon plus <out>/ADDONS.json. CI uploads the
 * archives to the release and commits the index; `knockbox addon` reads the index and verifies each
 * download against the sha256 recorded in it.
 *
 * Repo tooling, not part of the published npm package (see package.json "files"). Kept out of
 * tools/pack-game/ for that reason, even though it imports from it.
 *
 * The index is generated rather than hand-written because its sha256 values are the trust root: a
 * hand-maintained hash is a hash that is eventually stale, and a stale one here fails every install
 * with a tampering error that is not tampering.
 */

import { existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

import { AddonError, buildAddonArchive } from "./pack-game/addon.mjs";

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const MANIFEST = join(repoRoot, "clients", "addons.manifest.json");

function parseArgs(argv) {
  const opts = {};
  for (let i = 0; i < argv.length; i++) {
    switch (argv[i]) {
      case "--out": opts.out = argv[++i]; break;
      case "--tag": opts.tag = argv[++i]; break;
      case "--repo": opts.repo = argv[++i]; break;
      case "-h": case "--help": opts.help = true; break;
      default: throw new AddonError(`unknown argument: ${argv[i]}`);
    }
  }
  return opts;
}

const HELP = `Build the KnockBox addon release archives + ADDONS.json.

Usage:
  node tools/build-addons.mjs [options]

Options:
  --out <dir>       Output directory (default: .addons/ in the repo root).
  --tag <tag>       Release tag the assets will be attached to (default: addons-v<sdkVersion>).
  --repo <o/n>      Source repo for derived download URLs (default: jcub1011/KnockBox-Games).
  -h, --help        Show this help.`;

export function buildAll({ out, tag, repo } = {}) {
  const declared = JSON.parse(readFileSync(MANIFEST, "utf8"));
  const sdkVersion = declared.sdkVersion;
  const license = readFileSync(join(repoRoot, "LICENSE"), "utf8");

  const outDir = resolve(out ?? join(repoRoot, ".addons"));
  // `addons-v…`, not `v…`: that is the addon release namespace. `v…` names a PLATFORM release (the
  // csproj <Version>, which drives the container image's semver tags), and the two version numbers
  // are independent — a tag has to name exactly one of them. CI passes the real tag; this default is
  // what a local build produces, and it must agree or a locally generated index would point at
  // release assets attached to the wrong tag.
  const releaseTag = tag ?? `addons-v${sdkVersion}`;
  const sourceRepo = repo ?? "jcub1011/KnockBox-Games";
  mkdirSync(outDir, { recursive: true });

  const addons = {};
  const built = [];

  for (const [id, addon] of Object.entries(declared.addons)) {
    const asset = `knockbox-${id}-${sdkVersion}.zip`;
    const { buffer, sha256, files } = buildAddonArchive({
      repoRoot, id, addon,
      sdkVersion,
      minAppVersion: declared.minAppVersion,
      maxAppVersion: declared.maxAppVersion ?? undefined,
      license,
    });

    writeFileSync(join(outDir, asset), buffer);
    built.push({ id, asset, bytes: buffer.length, fileCount: files.size, sha256 });

    addons[id] = {
      version: sdkVersion,
      engine: addon.engine,
      description: addon.description,
      installTo: addon.installTo,
      minAppVersion: declared.minAppVersion,
      ...(declared.maxAppVersion ? { maxAppVersion: declared.maxAppVersion } : {}),
      source: { type: "github-release", repo: sourceRepo, tag: releaseTag, asset, sha256, size: buffer.length },
    };
  }

  // Older releases are preserved so `knockbox addon add <id> --version <old>` stays servable: pinning
  // is answered out of this map, never by guessing a URL, because a version the index does not list
  // is a version there is no verified hash for.
  const indexPath = join(outDir, "ADDONS.json");
  if (existsSync(indexPath)) {
    try {
      const existing = JSON.parse(readFileSync(indexPath, "utf8"));
      for (const [id, entry] of Object.entries(existing.addons ?? {})) {
        if (!addons[id]) continue;                                  // addon no longer published
        const history = { ...(entry.versions ?? {}) };
        if (entry.version && entry.version !== sdkVersion && entry.source) {
          history[entry.version] = { source: entry.source, ...(entry.minAppVersion ? { minAppVersion: entry.minAppVersion } : {}) };
        }
        // Never let history claim the version being published now — the fresh entry is authoritative.
        delete history[sdkVersion];
        if (Object.keys(history).length > 0) addons[id].versions = history;
      }
    } catch (err) {
      throw new AddonError(
        `existing ${indexPath} is unreadable (${err.message}). Refusing to overwrite it: that would ` +
        "silently drop the published history every pinned install depends on.");
    }
  }

  const index = {
    $comment:
      "Generated by tools/build-addons.mjs — do not hand-edit. The sha256 values here are the trust " +
      "root for every addon install: download URLs are derived, never carried, so this file is what " +
      "commits to the bytes.",
    schemaVersion: "1.0",
    sdkVersion,
    lastUpdated: null,
    addons,
  };
  writeFileSync(indexPath, `${JSON.stringify(index, null, 2)}\n`);

  return { outDir, indexPath, sdkVersion, releaseTag, built, index };
}

function cli() {
  try {
    const opts = parseArgs(process.argv.slice(2));
    if (opts.help) { console.log(HELP); return; }

    const result = buildAll(opts);
    console.log(`✓ built ${result.built.length} addon archive(s) for ${result.sdkVersion} (${result.releaseTag})`);
    for (const a of result.built) {
      console.log(`  ${a.asset.padEnd(34)} ${String(a.fileCount).padStart(3)} files  ${(a.bytes / 1024).toFixed(1)} KiB  ${a.sha256.slice(0, 12)}…`);
    }
    console.log(`  index: ${result.indexPath}`);
  } catch (err) {
    if (err instanceof AddonError) { console.error(`✗ ${err.message}`); process.exit(1); }
    throw err;
  }
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) cli();
