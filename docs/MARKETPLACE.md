# The official game marketplace

How a KnockBox server discovers, downloads, and version-checks games published to the official
marketplace at [`jcub1011/KnockBox-Games-Marketplace`](https://github.com/jcub1011/KnockBox-Games-Marketplace).

Related: [`KBG_FORMAT.md`](KBG_FORMAT.md) (the package format), [`HOSTING.md`](HOSTING.md)
(deployment), [`GAME_DEVELOPER_GUIDE.md`](GAME_DEVELOPER_GUIDE.md) (authoring a game).

**Status.** The server-side client and the update check are implemented and tested. There is
deliberately **no UI and no automation yet**: nothing polls the catalog, and nothing installs what it
downloads. The admin portal's "Game Catalog" tab is still disabled.

---

## 1. The moving parts

| Where | What it does |
|---|---|
| A game repository | Builds its game and publishes a `<id>.kbg` as a GitHub release asset, then calls the marketplace's `sync-catalog` composite action. |
| The marketplace repository | Holds `.plugins/CATALOG.json` — one entry per published game — plus the two JSON schemas that define the format. |
| This server | Fetches the catalog, compares it against installed games, and downloads packages on request. |

The marketplace stores **no binaries**. Every package stays on the release of the repository that
built it; the catalog only points at it and vouches for its hash.

## 2. The catalog

Fetched from `KnockBox:MarketplaceCatalogUrl`, which defaults to the official catalog on `main`.
`Marketplace/MarketplaceCatalog.cs` mirrors `schemas/marketplace.schema.json`. One entry:

```json
{
  "id": "jcub1011-Alpha-Chain",
  "name": "Alpha Chain",
  "description": "A multiplayer, shiritori-esque word game.",
  "version": "0.1.0",
  "author": { "name": "jcub1011" },
  "lastUpdated": "2026-08-12T17:23:47.371Z",
  "minAppVersion": "1.0.0",
  "tags": ["word-game", "party", "multiplayer"],
  "source": {
    "type": "github-release",
    "repo": "jcub1011/Alpha-Chain-Phaser-",
    "tag": "v0.1.0",
    "asset": "jcub1011-Alpha-Chain.kbg",
    "sha256": "76f72e5079494e883c0717e7501367f830c42fbed0127b0eb9326aca0a618f4c",
    "size": 2319262
  }
}
```

`author` may be a bare string or an object; both shapes are published in practice, so
`MarketplaceAuthorConverter` reads either.

**Schema compatibility.** `MarketplaceCatalog.MaxSchemaVersionMajor` (currently `1`) is the highest
`schemaVersion` **major** this build reads, mirroring `GamePackage.MaxFormatVersion`. A newer major
is refused with an upgrade hint rather than half-read. Within a major, unknown properties are
ignored — that is what makes adding a field a minor bump.

### Every catalog field is optional to the parser

The DTOs are entirely nullable, exactly like `GamePackageHeader`. A catalog arrives over the network
from a repository this server does not control, so deserialization must never throw; the *checking*
happens afterwards, in code that can name what was wrong. This splits three ways:

- **`MarketplaceClient.Parse`** — is this a catalog at all? (schema version, duplicate ids)
- **`MarketplaceClient.ValidateEntry`** — is this entry safe to act on? (source, URL parts, hash)
- **`PluginUpdateEvaluator`** — is this entry *meaningful*? (versions, app-version range)

## 3. Trust model

**The catalog's commit history is the trust root — not the release.** A GitHub release asset can be
deleted and re-uploaded in place by whoever owns the repository, so "it came from the release the
catalog names" proves very little on its own. What the catalog commits to is a **SHA-256**, and that
hash is what this server enforces.

Consequently:

- `sha256` is **required** for a `github-release` source, both by the schema and by this server. An
  entry without one is refused; there is no "unverified" install path.
- The download URL is **derived**, never supplied:
  `{MarketplaceDownloadBaseUrl}/{repo}/releases/download/{tag}/{asset}`. The schema has no URL
  field, so there is nothing for a tampered entry to point at another host. `repo`, `tag`, and
  `asset` are each pattern-checked before any request leaves the process.
- Only `https` is accepted (loopback `http` excepted, for tests and offline mirrors).
- No GitHub API is used. Deriving the URL avoids the 60-requests-per-hour unauthenticated API limit
  and removes a second failure mode; the cost is that a malformed `asset` is a hard error, which is
  the right trade — it is a marketplace bug, fixable in one commit, and the error names it.

### What a download must prove

`MarketplaceClient.DownloadAsync` treats the response as hostile until all of this holds:

1. Byte count stays under `MarketplaceMaxDownloadBytes`, counted **while streaming**. A declared
   `Content-Length` or catalog `size` is a pre-flight courtesy only — the same rule
   `GamePackageReader.CopyCounted` follows for declared entry sizes.
2. SHA-256 matches what the catalog published (fixed-time compare).
3. The archive passes the full `GamePackageReader.Read` validation — the *same* reader the installer
   uses, not a weaker copy: entry caps, path rules, ratio cap, format version.
4. The game id inside the package matches the entry's `id`, compared ordinally (ids name a directory,
   and on Linux `Demo` and `demo` are two games).
5. The package's `GAME.json` `version` matches the entry's `version`. The publishing action derives
   the catalog version *from* that file, so a disagreement means the entry is describing bytes it
   did not ship.

Any failure deletes the partial file. The result is a `DownloadedPackage`, which is `IDisposable`
so a caller cannot leak a half-gigabyte artifact on an error path.

**It does not install.** Dropping a `.kbg` into the games directory remains the only way a package
becomes a playable game (see `GamePackageInstaller`). Downloading and installing are kept separate so
the read-only-`games/` deployment story is unaffected.

## 4. Is my copy up to date?

`PluginUpdateEvaluator` answers this, and is pure — no I/O, no clock. The installed side comes from
`GameCatalog.GameLocations`, and an installed game's version is `GAME.json`'s **`version`** field
(`GameManifest.Version`).

Comparison is real semantic versioning (`SemVer`, semver.org 2.0.0), not string comparison — which
gets `0.9.0` vs `0.10.0` and `1.0.0-rc.1` vs `1.0.0` backwards.

| Status | Meaning |
|---|---|
| `NotInstalled` | Offered, and this server does not have it. |
| `UpToDate` | Installed at exactly the offered version. |
| `UpdateAvailable` | The catalog offers something newer that this server can run. |
| `InstalledAhead` | Installed at a *higher* version — a local build, or a rolled-back catalog. Never presented as an update. |
| `InstalledVersionUnknown` | Installed, but its `GAME.json` declares no parseable `version`. |
| `Incompatible` | The offered version's `minAppVersion`/`maxAppVersion` excludes this server. |
| `Unusable` | The catalog entry itself is broken (no id, unreadable version). Surfaced, not dropped. |

Two rules worth knowing:

- **`Incompatible` outranks `UpdateAvailable`.** An update that could not run is never offered. Both
  bounds are inclusive, and a bound that cannot be *parsed* also counts as incompatible — a
  constraint we cannot read must not be treated as no constraint.
- **`InstalledVersionUnknown` is not `UpdateAvailable`.** Every hand-made game on a server has no
  version; reporting them all as out of date would make the list noise instead of signal.

The server's own version comes from `Hosting/KnockBoxVersion.cs`, read off the assembly so
`<Version>` in `KnockBox.Server.csproj` stays the single source of truth. **Bump it when releasing.**

## 5. Publishing a game

In the game repository's release workflow, after the release is created:

```yaml
- name: Sync to Marketplace Catalog
  if: ${{ !inputs.draft }}
  uses: jcub1011/KnockBox-Games-Marketplace/.github/actions/sync-catalog@main
  with:
    tag: ${{ steps.resolve_tag.outputs.tag }}
    marketplace-token: ${{ secrets.MARKETPLACE_TOKEN }}
    # game-json-path: export/GAME.json   (default)
    # package-dir:    dist-game          (default)
```

The action reads `GAME.json`, locates `<package-dir>/<id>.kbg`, hashes it, and writes the entry.
It **fails** if that package is missing: an entry pointing at bytes that do not exist is worse than
no entry, because every server that reads the catalog retries it forever.

Requirements on the game side:

- `GAME.json` must declare `id`, `name`, and a semver **`version`**. `knockbox-pack` copies that
  version into the `.kbg` header too, so the two cannot disagree.
- The release must carry the packer's output, named `<id>.kbg` (the packer guarantees this).

> **Historical note.** The action used to hardcode `asset: "GAME.json"`, so published entries pointed
> at the manifest instead of the package. Fixed by deriving the name from the manifest id; the schema
> now also requires `asset` to end in `.kbg`, so the mistake cannot be published again.

## 6. Configuration

All under the `KnockBox:` prefix (env: `KnockBox__MarketplaceEnabled`, etc.).

| Key | Default | Meaning |
|---|---|---|
| `MarketplaceEnabled` | `true` | Master switch. Off ⇒ nothing is registered and the server holds no `HttpClient` at all — the right posture for an air-gapped deployment. |
| `MarketplaceCatalogUrl` | official catalog on `main` | Where the index lives. Override to run your own marketplace. Must be `https` (or loopback). |
| `MarketplaceDownloadBaseUrl` | `https://github.com` | Origin that release URLs are built on. |
| `MarketplaceMaxCatalogBytes` | 4 MiB | Cap on the catalog body, enforced while reading. |
| `MarketplaceMaxDownloadBytes` | 512 MiB | Cap on a package, enforced against bytes received. Matches `MaxPackageBytes`. |
| `MarketplaceCatalogTimeoutSeconds` | 30 | Timeout for a catalog fetch. |
| `MarketplaceDownloadTimeoutSeconds` | 600 | Timeout for one package download. |

Catalog fetches send `If-None-Match`; an unchanged catalog costs one `304` and no re-parse.

## 7. Testing

`MarketplaceClientTests` and `MarketplaceCatalogParsingTests` run against
`TestHelpers.FakeHttpMessageHandler` — no sockets, no new test dependency, same hand-rolled-fake
convention as `FakeWebSocket`. Download tests use genuine `.kbg` bytes from `PackageFixture`, so the
verification path runs for real.

`MarketplaceLiveTests` reaches the **real** marketplace and is skipped unless opted into. It is the
only thing that can prove the *published* catalog is one this server can use — which is exactly the
class of bug described in §5:

```powershell
$env:KNOCKBOX_MARKETPLACE_LIVE = "1"
dotnet test KnockBox.Server.Tests --filter "FullyQualifiedName~MarketplaceLive"
```

## 8. Not built yet

Admin API routes and UI, scheduled catalog polling, one-click install/update, and signature
verification beyond the published hash. None of them change the contracts above.
