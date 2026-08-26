# Client addons — distribution & versioning

The **addons** are the client libraries a game embeds to talk to KnockBox. This document is how they
are published, how a game developer installs and updates one, and what guarantees the mechanism gives.

Three are published today, all from one version line:

| Addon id | Engine | What it is | Source |
| --- | --- | --- | --- |
| `godot` | Godot 4 | GDScript addon: the `KnockBox` autoload, `KBNet`, `KBAuthority` | `clients/godot/addons/knockbox/` |
| `phaser` | Phaser 3 | Global plugin, protocol core, authority helper, server-less local peer | `clients/phaser/` |
| `web` | none (vanilla JS) | The reference SDK, for vendoring a pinned copy | `web/knockbox.js` + `web/kb-protocol.js` |

> **Most vanilla-JS games need none of this.** The platform serves the SDK at `/knockbox.js`, so a
> plain HTML5 game just loads it and is always on the server's own version. The `web` addon exists
> for the cases where that isn't wanted: offline development, or an engine export that would rather
> bundle the SDK than depend on the platform serving it.

---

## 1. Installing

### Godot — from inside the editor, no terminal

1. **Project → AssetLib**, search **KnockBox**, Install. (Or download
   `knockbox-godot-<version>.zip` from the [releases page] and unzip it at your project root.)
2. **Project → Project Settings → Plugins** → enable **KnockBox**.
3. Add the two autoloads (Project Settings → Autoload), in this order:
   ```
   KnockBox   res://addons/knockbox/knockbox.gd
   Net        res://addons/knockbox/kb_net.gd
   ```

Once enabled, two actions appear under **Project → Tools**:

- **KnockBox: check for addon updates** — fetches the index and offers a newer version. It refuses to
  overwrite a file you have edited until you confirm, and names the file.
- **KnockBox: reinstall addon (repair local edits)** — re-fetches the version you already have and
  restores every file. This is the fix for "I changed something in there and now it's broken."

Neither runs on a timer, and nothing here reaches the network until you click. If you would rather the
addon never phoned home at all, delete `addons/knockbox/updater.gd` — the addon works without it.

### Anything with Node — the CLI

```bash
npx knockbox addon add phaser        # or: godot, web
npx knockbox addon check
```

`npx` fetches the CLI on demand, so there is nothing to install first; Node is already a prerequisite
of any Vite/Phaser toolchain. To pin it in your repo like any other dev dependency:
`npm i -D knockbox-cli`.

### Anything at all — download and unzip

Grab `knockbox-<id>-<version>.zip` from the [releases page] and unzip it **at your project root**.
The archive is laid out project-relative, so everything lands where it belongs:

```
addons/knockbox/…      the addon
addons/knockbox/LICENSE
knockbox.json          the record of what you installed
```

This is a **first-class** install, not a fallback. The archive ships the same `knockbox.json` the CLI
would have written — byte for byte — so `knockbox addon check` and the repair path work afterwards
exactly as if you had used the CLI. A test asserts that equality, because nothing at runtime does.

[releases page]: https://github.com/jcub1011/KnockBox-Games/releases

---

## 2. `knockbox addon` reference

| Command | Does |
| --- | --- |
| `add <id> [--version <v>]` | Install, or **reinstall to repair** — see below. |
| `update [id] [--to <v>]` | Move to another version. No id updates every addon installed. |
| `check [--app-version <v>]` | Verify the installed files and report available updates. Changes nothing. |
| `list` | What's installed, from `knockbox.json`. |
| `remove <id>` | Uninstall, removing exactly the files that were installed. |

Options: `--dir <dir>` (project directory), `--index <url|path>`, `--download-base <url>`,
`--offline` (check only), `--keep-modified` (add/update), `--force` (update only).

### Repair: `add` restores, `check` diagnoses

Re-running `add` on an installed addon reinstalls the recorded version: modified files are restored,
deleted ones re-fetched, and each one is named in the output. There is no separate `reset` verb
because there doesn't need to be one — "install the addon" and "make the addon be the addon" are the
same request.

```
$ knockbox addon check
  godot    1.0.0      NEEDS REPAIR
    MODIFIED addons/knockbox/kb_core.gd
    MISSING  addons/knockbox/kb_net.gd

repair with `knockbox addon add <id>` — it reinstalls the recorded version.

$ knockbox addon add godot
✓ reinstalled godot 1.0.0
  installed 1 file
  restored  addons/knockbox/kb_core.gd (local changes discarded)
```

`check` exits non-zero for a broken or incompatible install, so it works as a CI step. An **available
update is not a failure** and does not affect the exit code.

### `add` and `update` take opposite defaults, deliberately

- **`add`** at the same version is you saying "make this pristine". Overwriting is the whole request,
  so it overwrites and reports. `--keep-modified` opts out.
- **`update`** moves to a *different* version, where silently discarding your edit is a surprise you
  did not ask for. It refuses, names the file, and points at `--force`. Updating several addons at
  once, one refusal does not block the others — the command reports it and exits non-zero.

`--force --keep-modified` together are not a contradiction: force gets past the refusal, keep-modified
then spares the specific files you have edited. That is the deliberate "I maintain a fork of one file"
case, and `check` goes on reporting those files as MODIFIED, which is the truth about them.

The Godot in-editor actions mirror this split for the same reason.

### Pruning is scoped

`add` and `update` remove files that the *previous* version recorded and the new one no longer ships.
They never touch a file they did not install — your own script sitting in `addons/knockbox/` survives,
and so does the directory holding it.

---

## 3. Versioning

**One version line covers every addon and the CLI.** Declared once, in
`clients/addons.manifest.json`:

```json
{ "sdkVersion": "1.0.0", "minAppVersion": "1.0.0", "maxAppVersion": null }
```

**Nothing else in the repo declares a real version.** Files that need one for their own format's
sake hold the sentinel `0.0.0-dev`, and the build stamps the real value in:

| Where a version appears | How it gets the real number |
| --- | --- |
| `clients/addons.manifest.json` | **You edit this. It is the only one.** |
| Godot `plugin.cfg` | `tools/build-addons.mjs` stamps it into the release archive. |
| `tools/pack-game/package.json` | CI stamps it before `npm publish`. |
| `KnockBox.Server` (`KnockBoxSdk`) | Reads the manifest, **embedded into the assembly** by the csproj. |
| `clients/phaser/`, `web/` `package.json` | Nothing — both are `private`, unpublished, and their version was never used for anything. |

`AddonManifestTests` asserts every in-repo declaration is *still the sentinel*, not that it equals
`sdkVersion`. That distinction is the point: an equality check still leaves six real numbers that must
be edited together, which is the arrangement that kept going wrong — there were five copies before the
manifest and they had already drifted to three different values for artifacts the docs claimed moved
together. Now a stale number cannot exist, because no committed file claims a version at all.

A checkout therefore reports `0.0.0-dev` in a few places, which is true: a checkout is not a release.
The packer is the one exception — run from inside a checkout it reads `sdkVersion` directly, so a
locally built `.kbg` is stamped with the same version a released one would be. The Godot updater
refuses to run at all when it sees the sentinel, since that means it is looking at the KnockBox source
tree and updating would overwrite the repo's own files.

**The addon version is independent of the server version.** Compatibility is expressed the way a
marketplace catalog entry expresses it — `minAppVersion` / `maxAppVersion`, compared against
`Hosting/KnockBoxVersion.cs`. So an addon release does not force a server release, or the reverse.
Both bounds are inclusive, and a bound that cannot be *parsed* counts as incompatible: a constraint
we cannot read is not the absence of one.

`knockbox addon check --app-version 1.0.0` applies those bounds against a specific server.

### What a game records, and what the portal shows

`knockbox pack` reads your `knockbox.json` and stamps the installed versions into the shipped
`GAME.json`:

```json
{ "id": "my-game", "…": "…", "sdk": { "godot": "1.0.0" } }
```

Never validated, and it never affects whether a game loads — like `version`, and for the same reason:
every hand-written game has no stamp. Its consumer is the admin portal's **Game Catalog**, which
compares it against the SDK the server shipped with and badges the two actionable cases:

| Status | Badge | Meaning |
| --- | --- | --- |
| `unknown` | *(none)* | No stamp. The common case — most games. Not a problem. |
| `current` | *(none)* | Matches this server's SDK. |
| `behind` | **SDK outdated** | Built against an older addon; rebuild to pick up client fixes. |
| `ahead` | SDK newer | Built against a newer addon than this server shipped. Still runs. |

`unknown` and `current` are silent on purpose: a badge that appears on nearly every card is not read.
With several addons stamped the worst answer wins, and `behind` outranks `ahead` — behind is the one
an operator can act on. Use `--no-sdk-stamp` to omit the stamp.

This is separate from, and coarser than, the **wire protocol version** (`KnockBoxProtocol.Version`,
currently `1`), which is what actually protects a vendored SDK at connect time: the server accepts
anything up to its own version and terminally rejects (`1008`) anything newer.

---

## 4. The index and the trust model

`.addons/ADDONS.json`, served from
`raw.githubusercontent.com/jcub1011/KnockBox-Games/main/.addons/ADDONS.json`. This mirrors the game
marketplace's model (`docs/MARKETPLACE.md` §3) because it is that mechanism pointed the other way —
instead of a server pulling game packages, a game developer pulls addons.

```json
{
  "schemaVersion": "1.0",
  "sdkVersion": "1.0.0",
  "addons": {
    "godot": {
      "version": "1.0.0",
      "engine": "godot4",
      "installTo": "addons/knockbox",
      "minAppVersion": "1.0.0",
      "source": {
        "type": "github-release",
        "repo": "jcub1011/KnockBox-Games",
        "tag": "v1.0.0",
        "asset": "knockbox-godot-1.0.0.zip",
        "sha256": "95e5c67b…",
        "size": 62328
      },
      "versions": { "0.9.0": { "source": { "…": "…" } } }
    }
  }
}
```

Four properties are load-bearing:

1. **The index is the trust root, not the release.** A release asset can be re-uploaded in place, so
   what the index commits to is a **required `sha256`**, verified on every download before a byte is
   written. Its authority comes from its commit history.
2. **URLs are derived, never carried.** `{base}/{repo}/releases/download/{tag}/{asset}`. A tampered
   entry has nothing to point elsewhere, and `repo`/`tag`/`asset` are pattern-checked *before* any
   request leaves the process. `asset` must end in `.zip`.
3. **Archives are untrusted input.** Every entry is validated before extraction: stored-only, CRC
   checked, no duplicate names, and path rules that reject absolute paths, drive letters, `..`
   segments and Windows reserved device names. The CLI reuses the same `kbg.mjs` primitives the `.kbg`
   reader uses; the Godot updater applies the same rules in GDScript.
4. **Pinning is served from `versions`, never guessed.** `--version 0.9.0` is answered out of the
   index's history, so it has a verified hash. A version the index does not publish is refused rather
   than fetched unverified.

A newer `schemaVersion` **major** is refused with an upgrade hint rather than half-read. Within a
major, unknown properties are ignored.

`.addons/ADDONS.json` is committed; the `.zip` archives are release assets and are gitignored.

---

## 5. Releasing

```bash
# 1. Bump the version. ONE file, one line.
$EDITOR clients/addons.manifest.json      # "sdkVersion": "1.1.0"

# 2. Verify.
dotnet test KnockBox-Games.slnx --nologo

# 3. Optional: build the archives locally and eyeball them (CI does this on every PR anyway).
node tools/build-addons.mjs

# 4. Merge to main, then tag that commit. CI does the rest.
git tag addons-v1.1.0 && git push origin addons-v1.1.0
```

Step 4 says *merge first* for a reason: the `addons` job checks out `main`, not the tagged commit, so
the bump has to be on `main` or the tag-vs-manifest guard reads a stale value.

**Editing `minAppVersion` (or `maxAppVersion`) requires a release of its own.** Those bounds reach
users only through `.addons/ADDONS.json`, which is regenerated and committed by an `addons-v*` tag and
by nothing else — so a manifest edit that lands without one leaves the *published* index still
advertising the old bound, and every `knockbox addon add` judged against it. Bump `sdkVersion` in the
same commit and cut the tag. `AddonManifestTests` fails a checkout where the index and the manifest
claim the same `sdkVersion` but disagree about `minAppVersion`, which is exactly that state.

### Two tag namespaces

| Tag | Releases | Version it names |
| --- | --- | --- |
| `v1.2.3` | the container image, plus the three downloadable release assets | `KnockBox.Server.csproj` `<Version>` |
| `addons-v1.2.3` | addon archives, `ADDONS.json`, the npm CLI | `addons.manifest.json` `sdkVersion` |

They are separate because the two version numbers are independent, and a tag has to name exactly one
of them. Sharing `v*` broke both directions: a server-only release failed the `addons` job's
tag-vs-manifest guard (nothing was wrong — no addon had changed), and an addon-only release published
an image *labelled* with a version its own assembly did not report, because image tags come from the
git tag while `KnockBoxVersion` comes from the assembly — and that value is what marketplace
`minAppVersion` bounds are compared against.

Only `addons-v*` is a push trigger. `v*` deliberately is **not** listed in `ci.yml` — a platform
release is cut by the manual `release.yml`, which derives its tag from the csproj rather than reading
one you typed, and a hand-pushed `v*` would be a second path to the same artifact that bypassed every
guard that workflow adds. `ReleaseWorkflowTests` asserts it stays absent. The `publish` job needs no
exclusion either: its condition is `refs/heads/main`, which no tag ref matches, so an addon release
leaves the image alone.

The `addons` CI job (`addons-v*` tags only, gated on every test job) verifies the tag matches the manifest, builds
one archive per addon, generates the index, uploads the archives to the release, commits the index to
`main`, and publishes `knockbox-cli` to npm via trusted publishing (OIDC — no long-lived token in this
repo). The `pack-game` job builds the archives on **every** run, so a stale entry in the manifest's
file lists fails a PR rather than a tag build.

### One-time setup before the first release

- **npm trusted publishing** must be configured for `knockbox-cli`, and there is a bootstrap
  catch-22: npm requires the package to **already exist** before a trusted publisher can be
  configured (it is what stops someone claiming a name they don't own), but CI can't create it without
  one. So the first release is the only manual one:

  1. Locally, from `tools/pack-game/`, with the version set to the real number:
     `npm version 1.0.0 --no-git-tag-version && npm publish --access public`
     (`npm login` first; revert the version bump afterwards so the repo keeps the `0.0.0-dev` sentinel.)
  2. On npmjs.com → the package → **Settings → Trusted publisher**, add a GitHub Actions publisher:
     **organization/user** `jcub1011`, **repository** `KnockBox-Games`, **workflow filename**
     `ci.yml`, environment blank, and allow **`npm publish`**. Equivalently:
     `npm trust github knockbox-cli --file ci.yml --repo jcub1011/KnockBox-Games --allow-publish`
     (needs npm ≥ 11.15.0 and account 2FA; it does *not* remove the must-already-exist requirement).
  3. Optionally tighten: **Settings → Publishing access → "Require two-factor authentication and
     disallow tokens"**, so the OIDC publisher becomes the only way to publish.

  Every release after that is fully automatic. Two things to know: the publisher matches on the
  workflow **filename**, so renaming `ci.yml` silently breaks publishing until you update it; and
  trusted publishing needs **npm ≥ 11.5.1 / Node ≥ 22.14.0**, which is why the job upgrades npm before
  publishing — a too-old npm fails with an auth error, not a version error, which sends you debugging
  the wrong thing.
- **Godot Asset Library** submission needs a Godot account and a review round; later versions are a
  resubmission of the same entry. The release zip means AssetLib is never the only way in.
- **`.addons/ADDONS.json` is generated output.** CI regenerates and commits it on every tag. A copy
  committed ahead of the matching release will point at release assets that do not exist yet.

---

## 6. Adding a new addon

1. Add an entry to `clients/addons.manifest.json` — `engine`, `description`, `root`, `installTo`, and
   either `"files": ["**"]` for a whole directory or an explicit list. `docs` names files to copy in
   from elsewhere in the repo.
2. Run `node tools/build-addons.mjs` and confirm the archive contents.
3. `dotnet test` — `AddonManifestTests` checks every declared file exists.

The schema already accommodates a future `unity` entry with no change. If the new addon reimplements
the protocol core, add it to `web/__tests__/client-parity.test.js` so the three-way port comparison
covers it.

---

## 7. Known gap: the Godot addon's protocol surface

The Godot addon does not implement `normalizeReady`, `LOG_LEVELS` or `makeLogger`. Concretely, a Godot
game cannot use `KnockBox.log.*` and cannot see `authority`, `ownerId` or `isOwner` — the
server-authority owner contract that `web/` and `clients/phaser/` both gained.

This is tracked, not hidden: `web/__tests__/client-parity.test.js` pins the gap in a named allowlist
and fails if the allowlist goes stale in either direction. Closing it is a separate task
(`docs/SERVER_AUTHORITY_DESIGN.md`: "Godot addon gets the same treatment as a parity follow-up").
