#!/usr/bin/env node
/**
 * Turn the repo's docker-compose.yml into the copy that ships in a release bundle.
 *
 *   node tools/compose-release.mjs [--version 1.2.3] [--out <file>] [--image <ref>] [--check]
 *
 * The repo's compose file defaults to `build:` from a source tree, which is right for a developer
 * and useless to someone who downloaded a zip: they have no checkout to build from. This rewrites
 * that one stanza into `image: ghcr.io/<repo>:<version>`, pinned to the exact release, and leaves
 * every comment and every other line byte-identical.
 *
 * A transformed COPY of the compose file is deliberately not kept in the repo. Two compose files
 * drift, and the one nobody edits is the one users download — the same reason ADDONS.json is
 * generated rather than hand-maintained. The cost of generating it is that this script depends on
 * anchors in a hand-edited file, so `--check` runs in CI on every PR: a compose edit that moves the
 * anchor fails there, not during a release.
 *
 * Repo tooling, not part of the published npm package.
 */

import { readFileSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const COMPOSE = join(repoRoot, "docker-compose.yml");
const CSPROJ = join(repoRoot, "KnockBox.Server", "KnockBox.Server.csproj");
const DEFAULT_IMAGE = "ghcr.io/jcub1011/knockbox-games";

export class ComposeError extends Error {}

const HELP = `Rewrite docker-compose.yml to run a prebuilt image instead of building locally.

Usage:
  node tools/compose-release.mjs [options]

Options:
  --version <x.y.z>  Version to pin (default: <Version> from KnockBox.Server.csproj).
  --image <ref>      Image repository, without the tag (default: ${DEFAULT_IMAGE}).
  --out <file>       Write here (default: stdout).
  --check            Verify the transform still applies; write nothing. Exit 1 if it does not.
  -h, --help         Show this help.`;

function parseArgs(argv) {
  const opts = {};
  for (let i = 0; i < argv.length; i++) {
    switch (argv[i]) {
      case "--version": opts.version = argv[++i]; break;
      case "--image": opts.image = argv[++i]; break;
      case "--out": opts.out = argv[++i]; break;
      case "--check": opts.check = true; break;
      case "-h": case "--help": opts.help = true; break;
      default: throw new ComposeError(`unknown argument: ${argv[i]}`);
    }
  }
  return opts;
}

/**
 * The csproj <Version> is the single source of truth for the platform version (Hosting/
 * KnockBoxVersion.cs reads it back off the assembly). Read here so a bundle can never be pinned to
 * a version the image it names does not report.
 */
export function csprojVersion() {
  const xml = readFileSync(CSPROJ, "utf8");
  const m = /<Version>\s*([^<\s]+)\s*<\/Version>/.exec(xml);
  if (!m) throw new ComposeError(`no <Version> element in ${CSPROJ}`);
  return m[1];
}

/**
 * Rewrites the `build:` stanza to a pinned `image:`, and mounts the bundle's `appsettings.json`.
 *
 * All three edits are anchored so a partial match is an error rather than a silently half-transformed
 * file: the commented `# image: …:latest` line (which becomes the live one), the `build:` block with
 * its `context:`/`dockerfile:` children (which get commented out), and the `/games` mount (which the
 * appsettings mount is inserted above). Anything else in the file — including the second,
 * commented-out service and the volume definitions — is left alone.
 */
export function transform(text, { version, image = DEFAULT_IMAGE } = {}) {
  if (!version) throw new ComposeError("a version is required");

  // Preserve the file's own line endings; the repo copy is CRLF and a bundle that silently switched
  // to LF would show up as a whole-file diff the first time someone compared the two.
  const eol = text.includes("\r\n") ? "\r\n" : "\n";
  const lines = text.split(/\r?\n/);

  const pinned = `${image}:${version}`;
  let uncommented = 0;
  let commented = 0;
  let mounted = 0;

  const out = lines.flatMap((line) => {
    // `    # image: ghcr.io/…:latest    # stable release` → the live, pinned image line. Only the
    // stable-channel line is promoted; the `:develop` one stays a comment. Its trailing
    // `# stable release` comment is dropped rather than carried over — the pin may be a prerelease.
    const stable = /^(\s*)#\s*image:\s*\S+:latest(?:\s+#.*)?$/.exec(line);
    if (stable) {
      uncommented++;
      return `${stable[1]}image: ${pinned}`;
    }

    // The build stanza and its children. Matched by exact key rather than by tracking indentation
    // depth, because these three keys appear exactly once uncommented in the file and a YAML-aware
    // rewrite would reflow every comment in it.
    const build = /^(\s*)(build:|context:\s*\.|dockerfile:\s*KnockBox\.Server\/Dockerfile)(\s*)$/.exec(line);
    if (build) {
      commented++;
      return `${build[1]}# ${build[2]}`;
    }

    // The bundle ships an `appsettings.json` beside this file, and without a mount it does nothing:
    // the image bakes its own copy, and nothing here reads the one next to the compose file. A user
    // edits a `KnockBox:` knob, restarts, and sees no change and no reason for it. Anchored on the
    // /games mount rather than on `volumes:` because that key appears twice in the file (the
    // service's and the top-level one), while this line appears exactly once uncommented — the count
    // check below is what pins that. Added for the BUNDLE only: a developer with a checkout already
    // has the file in the tree, so the repo's own compose file is left alone.
    const games = /^(\s*)- \$\{KNOCKBOX_GAMES_DIR:-\.\/games\}:\/games:ro\s*$/.exec(line);
    if (games) {
      mounted++;
      // AFTER the anchor, not before: the /games mount has its own comment block above it, and
      // inserting there would leave that comment sitting over the wrong line.
      return [
        line,
        `${games[1]}# Every KnockBox: knob, shipped beside this file, mounted over the image's own copy.`,
        `${games[1]}# Edit it and \`docker compose up -d\`. Env vars (KnockBox__Key) still take priority.`,
        `${games[1]}- ./appsettings.json:/app/appsettings.json:ro`,
      ];
    }

    return line;
  });

  if (uncommented !== 1) {
    throw new ComposeError(
      `expected exactly 1 commented \`# image: …:latest\` line in docker-compose.yml, found ${uncommented}. ` +
      "The release bundle's image pin is derived from that line — restore it or update this script.");
  }
  if (commented !== 3) {
    throw new ComposeError(
      `expected exactly 3 lines of the \`build:\` stanza (build:/context:/dockerfile:), found ${commented}. ` +
      "A downloaded bundle has no source tree, so an un-commented `build:` makes it unusable.");
  }
  if (mounted !== 1) {
    throw new ComposeError(
      `expected exactly 1 uncommented \`/games:ro\` mount in docker-compose.yml, found ${mounted}. ` +
      "The bundle's appsettings.json is mounted above it — without that anchor the file ships inert.");
  }

  return { text: out.join(eol), pinned };
}

export function build({ version, image, out, check } = {}) {
  const source = readFileSync(COMPOSE, "utf8");
  const resolved = version ?? csprojVersion();
  const result = transform(source, { version: resolved, image });

  if (!check && out) writeFileSync(out, result.text);
  return { ...result, version: resolved, out: check ? null : (out ?? null) };
}

function cli() {
  try {
    const opts = parseArgs(process.argv.slice(2));
    if (opts.help) { console.log(HELP); return; }

    const result = build(opts);
    if (opts.check) {
      console.log(`✓ docker-compose.yml transform still applies (would pin ${result.pinned})`);
    } else if (opts.out) {
      console.log(`✓ wrote ${opts.out} pinned to ${result.pinned}`);
    } else {
      process.stdout.write(result.text);
    }
  } catch (err) {
    if (err instanceof ComposeError) { console.error(`✗ ${err.message}`); process.exit(1); }
    throw err;
  }
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) cli();
