# Releasing

This describes how a release is built and how an extension is published to the official Cove
extension registry. Nothing here publishes automatically — cutting a release and opening the
registry pull request are deliberate steps a maintainer takes, per extension.

## Releases are per-extension, gated by extension-scoped tags

`extensions/catalog.json` is the source of truth for the release matrix. Each entry declares its
own `tagPrefix` (e.g. Renamer's is `renamer/`), and a release for that extension is cut by pushing
a tag of the form `<tagPrefix>v<semver>` — for example `renamer/v1.0.0`. This is a change from the
old single-extension-repo scheme, which used a flat `v*` tag with no extension prefix.

## What CI does: a catalog-driven validate → build → release matrix

`.github/workflows/build.yml` reads `extensions/catalog.json` to compute its build matrix:

- **validate** — on every PR and every push, confirms the catalog is well-formed. On a tag push it
  additionally confirms the tag matches exactly one catalog entry's `tagPrefix` with a valid semver
  suffix, that the entry's `extension.json` declares exactly the tag's version, and that the
  extension's registry manifest — when it has one — already carries a `versions[]` row for that
  version. All of these fail before any extension builds.
- **build** — runs for every extension in `extensions/catalog.json` on every PR (there is no
  `paths:` filtering; this matches the upstream template convention, not a CI-minute optimization —
  every extension's build is exercised on every PR regardless of which extension the PR actually
  touched). On a tag push, only the tagged extension's entry builds and is versioned; every other
  extension in the matrix builds with a placeholder version and is not packaged.
  - Packaging paths are driven entirely by each catalog entry's fields — `projectPath`,
    `manifestPath`, and `uiPath` (only present when the extension ships a frontend) — not hardcoded
    to one extension. Adding a new extension's release capability requires only a correct
    `catalog.json` entry; the workflow logic itself does not need editing.
  - Packaging is one `Assemble package` step. The job publishes into a throwaway directory, then that
    step copies the file set the extension's `catalog.json` `artifacts` array declares into a clean
    package directory, stamping the release version into the packaged `extension.json` as it copies —
    so the shipped manifest always agrees with the release it came from. A declared file the build did
    not produce fails the job before anything is zipped, and the step prints every file it copied and
    a count.
- **release** — triggers only on a tag push, downloads every build job's artifact, and attaches
  the matching `.zip` to a GitHub release for that tag.

Renamer is the concrete worked example today: its `tagPrefix` is `renamer/`, its manifest id is
`com.alextomas955.renamer`, and cutting `renamer/v0.1.0` builds, assembles, and packages
`com.alextomas955.renamer-0.1.0.zip`.

## Publish order: release asset first, then the registry pull request

The official registry is metadata-only. It does not host binaries; it points at the GitHub release
asset and records a checksum. Its CI computes each `versions[].checksum` by downloading the
version's `downloadUrl` and hashing it, and it fails the pull request if that URL returns a 404.

That gives a strict order, for any extension being released:

1. Tag the release (`<tagPrefix>v<semver>`, e.g. `renamer/v0.1.0`) and let the workflow publish
   the packaged `.zip` asset to the GitHub release.
2. Confirm the asset is reachable at its `downloadUrl`.
3. Only then open the registry pull request that adds that extension's registry entry (e.g.
   `extensions/com.alextomas955.renamer.json`).

If the registry pull request is opened before the asset exists, the checksum computation has
nothing to hash and the pull request fails. Publishing the asset first is what makes the checksum
step succeed against the real asset bytes.

## Raising minCoveVersion

An extension's minimum host version is declared once, in its `extension.json` `minCoveVersion`.
That is the only place you edit it; the loaded assembly reads it from the shipped manifest, and
`scripts/validate-extension-repo.mjs` checks it is at least the repo-wide `CoveMinVersion` in
`Directory.Build.props` on every push.

The `minCoveVersion` in a registry manifest's `versions[]` row is a different thing that happens to
share a name. Each row describes an immutable zip a user can still download, and its floor is the
floor _that_ artifact needs — not a copy of the source tree's current one. So a raised floor reaches
the registry by prepending a new row for the release you are cutting, never by editing an existing
row: a row claiming a higher floor than its zip actually needs both misdescribes that file and locks
out users for whom it works.

Raising the floor is a user-facing change — a user below the new floor loses the extension entirely,
rather than losing a feature. Say so in that extension's `CHANGELOG.md`, and name the capability
that forced the floor, so the requirement reads as a reason rather than a version number.

## What the registry computes — do not hand-write it

Each extension's registry draft carries the `id`, `repositoryUrl`, a `raw.githubusercontent.com`
`sourceManifestUrl`, the categories, and a `versions[]` entry with `version`, `downloadUrl`,
`minCoveVersion`, and a changelog.

It deliberately omits three things the registry owns:

- `checksum` — computed by registry CI from the reachable `downloadUrl`.
- `releasedAt` — stamped by registry CI when the pull request merges.
- `index.json` — regenerated by registry CI; it is never edited by hand.

Letting CI compute the checksum ties the published metadata to the actual asset bytes; a
hand-written checksum could mask a wrong or tampered asset.
