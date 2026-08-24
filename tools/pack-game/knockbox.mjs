#!/usr/bin/env node
/**
 * `knockbox` — the KnockBox game developer CLI.
 *
 * Two groups of subcommands:
 *   knockbox pack …     build a .kbg game package (the original knockbox-pack; see pack-game.mjs)
 *   knockbox addon …    install / update / verify the client addons (see addon.mjs)
 *
 * Bare flags with no subcommand still run `pack`, so every documented `knockbox-pack --in …`
 * invocation keeps working unchanged.
 */

import { resolve } from "node:path";
import { fileURLToPath } from "node:url";

import { runPack } from "./pack-game.mjs";
import {
  AddonError, DEFAULT_DOWNLOAD_BASE, DEFAULT_INDEX_URL, MANIFEST_NAME,
  add, check, list, remove, update,
} from "./addon.mjs";

const HELP = `KnockBox CLI

Usage:
  knockbox pack  --in <built-dir> --manifest <GAME.json> [options]
  knockbox addon <command> [options]

Packaging:
  pack                Build a .kbg game package. Run \`knockbox pack --help\` for its options.

Addons (the client libraries your game embeds):
  addon add <id>      Install an addon, or REINSTALL it to repair local edits.
  addon update [id]   Move to a newer version (refuses to discard local edits without --force).
  addon check         Verify installed files, report available updates. Changes nothing.
  addon list          Show what is installed, from ${MANIFEST_NAME}.
  addon remove <id>   Uninstall, removing exactly the files that were installed.

Addon options:
  --dir <dir>            Project directory (default: current).
  --version <v>          Pin a version for add (default: whatever the index offers).
  --to <v>               Target version for update.
  --index <url|path>     Addon index to read (default: the official one).
  --download-base <url>  Release download host (default: ${DEFAULT_DOWNLOAD_BASE}).
  --app-version <v>      Server version to judge min/maxAppVersion against, for check.
  --offline              check only: skip the index, just verify local files.
  --keep-modified        add/update: leave locally-edited files alone instead of overwriting them.
  --force                update only: proceed even though local edits would be overwritten.
  -h, --help             Show this help.

Docs: docs/ADDONS.md`;

/** Minimal flag parser for the addon subcommands. Same style as pack's — zero dependencies. */
export function parseAddonArgs(argv) {
  const opts = {};
  const positional = [];
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    switch (a) {
      case "--dir": opts.dir = argv[++i]; break;
      case "--version": opts.version = argv[++i]; break;
      case "--to": opts.to = argv[++i]; break;
      case "--index": opts.indexLocation = argv[++i]; break;
      case "--download-base": opts.downloadBase = argv[++i]; break;
      case "--app-version": opts.appVersion = argv[++i]; break;
      case "--archive": opts.archiveLocation = argv[++i]; break;
      case "--offline": opts.offline = true; break;
      case "--keep-modified": opts.keepModified = true; break;
      case "--force": opts.force = true; break;
      case "-h": case "--help": opts.help = true; break;
      default:
        if (a.startsWith("-")) throw new AddonError(`unknown argument: ${a}`);
        positional.push(a);
    }
  }
  return { opts, positional };
}

const plural = (n, one, many = `${one}s`) => `${n} ${n === 1 ? one : many}`;

/** Report what an install/reinstall/update actually did to the working tree. */
function reportWrite(result) {
  if (result.written.length) console.log(`  installed ${plural(result.written.length, "file")}`);
  if (result.updated.length) console.log(`  updated   ${plural(result.updated.length, "file")}`);
  // Named individually, never just counted: this is a developer's edit being discarded, and they are
  // entitled to know which file it was even when they asked for it. Files that merely changed between
  // versions are counted above instead — calling those "discarded" would cry wolf on every update.
  for (const f of result.restored) console.log(`  restored  ${f} (local changes discarded)`);
  for (const f of result.skipped) console.log(`  kept      ${f} (locally modified, --keep-modified)`);
  for (const f of result.pruned) console.log(`  removed   ${f} (not in this version)`);
}

async function runAddon(argv) {
  const { opts, positional } = parseAddonArgs(argv);
  const [command, id] = positional;

  if (opts.help || !command) { console.log(HELP); return; }

  switch (command) {
    case "add": {
      if (!id) throw new AddonError("`addon add` needs an addon id, e.g. `knockbox addon add godot`.");
      const result = await add(id, opts);
      const verb = result.previousVersion === null ? "installed"
        : result.previousVersion === result.version ? "reinstalled" : "replaced";
      console.log(`✓ ${verb} ${result.id} ${result.version}`);
      reportWrite(result);
      if (verb === "reinstalled" && !result.restored.length && !result.written.length) {
        console.log("  already matched the published files — nothing to repair.");
      }
      return;
    }

    case "update": {
      // No id updates everything installed — the command someone actually types when they mean "get
      // me current". A per-addon try/catch rather than letting the first refusal abort the run: one
      // addon with a local edit must not silently leave the others un-updated, which would be a
      // half-done update reported as a single failure.
      const ids = id ? [id] : list(opts).addons.map((a) => a.id);
      if (!ids.length) {
        console.log(`no addons recorded in ${MANIFEST_NAME} — install one with \`knockbox addon add godot\`.`);
        return;
      }

      let refused = false;
      for (const target of ids) {
        try {
          const result = await update(target, { ...opts, version: opts.to ?? opts.version });
          if (result.upToDate) { console.log(`✓ ${result.id} is already ${result.version}`); continue; }
          console.log(`✓ ${result.id} ${result.previousVersion} -> ${result.version}`);
          reportWrite(result);
        } catch (err) {
          if (!(err instanceof AddonError)) throw err;
          console.error(`✗ ${target}: ${err.message}`);
          refused = true;
        }
      }
      if (refused) process.exitCode = 1;
      return;
    }

    case "check": {
      const report = await check(opts);
      if (report.empty) {
        console.log(`no addons recorded in ${report.projectDir}/${MANIFEST_NAME}`);
        console.log("install one with `knockbox addon add godot` (or phaser, or web).");
        return;
      }
      let problems = 0;
      for (const a of report.addons) {
        const bits = [];
        if (!a.clean) bits.push("NEEDS REPAIR");
        if (a.updateAvailable) bits.push(`update available: ${a.latest}`);
        if (a.incompatible) bits.push(a.incompatible);
        console.log(`  ${a.id.padEnd(8)} ${a.version.padEnd(10)} ${bits.length ? bits.join("; ") : "ok"}`);
        for (const f of a.modified) console.log(`    MODIFIED ${f}`);
        for (const f of a.missing) console.log(`    MISSING  ${f}`);
        if (!a.clean || a.incompatible) problems++;
      }
      if (report.indexError) console.log(`  (could not read the addon index: ${report.indexError})`);
      if (report.addons.some((a) => !a.clean)) {
        console.log("\nrepair with `knockbox addon add <id>` — it reinstalls the recorded version.");
      }
      // Non-zero only for a broken or incompatible install, never merely for an available update:
      // this is meant to be usable in a CI step, and "a newer version exists" is not a failure.
      if (problems > 0) process.exitCode = 1;
      return;
    }

    case "list": {
      const report = list(opts);
      if (!report.addons.length) { console.log(`no addons recorded in ${report.projectDir}/${MANIFEST_NAME}`); return; }
      for (const a of report.addons) console.log(`  ${a.id.padEnd(8)} ${a.version.padEnd(10)} ${plural(a.fileCount, "file")}`);
      return;
    }

    case "remove": {
      if (!id) throw new AddonError("`addon remove` needs an addon id.");
      const result = remove(id, opts);
      console.log(`✓ removed ${result.id} ${result.version} (${plural(result.removed.length, "file")})`);
      return;
    }

    default:
      throw new AddonError(`unknown addon command '${command}'. Try: add, update, check, list, remove.`);
  }
}

export async function main(argv = process.argv.slice(2)) {
  const [first, ...rest] = argv;

  // No subcommand, or leading flags: this is the original `knockbox-pack --in …` form.
  if (first === undefined || first.startsWith("-")) {
    if (first === "-h" || first === "--help") { console.log(HELP); return; }
    if (first === undefined) { console.log(HELP); return; }
    return runPack(argv);
  }

  if (first === "pack") return runPack(rest);
  if (first === "addon") {
    try {
      return await runAddon(rest);
    } catch (err) {
      if (err instanceof AddonError) {
        console.error(`✗ ${err.message}`);
        process.exit(1);
      }
      throw err;
    }
  }

  console.error(`✗ unknown command '${first}'. Try: pack, addon. Run \`knockbox --help\`.`);
  process.exit(1);
}

export { DEFAULT_INDEX_URL, HELP };

// Run only when invoked directly, not when imported by tests.
if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main();
}
