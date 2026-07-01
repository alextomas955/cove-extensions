# Cove Extensions Monorepo

## Project

This is the Cove extensions monorepo — a single git repository holding one or more Cove
extensions, following [yourcove](https://github.com/yourcove)'s official
`multi-extension-repo-template` pattern. Today it holds one extension, **Renamer**
(`extensions/Renamer/`). See `README.md` for the extension list and dev setup.

## Registry and CI

- `catalog.json` is the extension registry and the source of truth CI reads to compute its build
  matrix. Each entry declares that extension's `name`, `id`, `path`, `tagPrefix`, `projectPath`,
  `manifestPath`, `versionSourcePath`, and (optionally) `uiPath`. Adding a new extension's release
  capability is a `catalog.json` edit, not a workflow-logic change.
- CI (`.github/workflows/build.yml`) is a catalog-driven `validate → build → release` matrix: every
  catalog entry builds on every PR (no `paths:` filtering); a release for one extension is cut by
  pushing a tag of the form `<tagPrefix>v<semver>` (e.g. `renamer/v1.0.0`), which builds, strip-
  verifies, and packages only that extension.
- See `docs/BRANCHING.md` and `docs/RELEASING.md` for the full branching and release process.

## Build wiring

The root `Directory.Build.props`/`Directory.Build.targets` auto-wire `Cove.Sdk` (which
transitively carries `Cove.Plugins` + `Cove.Core`) for every project in the monorepo, either
against a local sibling `../cove` checkout (auto-detected, or via `COVE_REPO`) or from NuGet.
Individual extensions' `.csproj` files should not add their own direct Cove reference or restate
the relative-path math to `../cove` — that's centralized here.

Build the whole monorepo from this root:

```sh
dotnet build Renamer.slnx
```

## Planning

This folder has its own thin `.planning/` at this root, scoped to cross-cutting monorepo concerns
only (CI changes, a future `shared/` folder, new-extension scaffolding). Each extension inside the
monorepo has its OWN separate nested `.planning/` (e.g. `extensions/Renamer/.planning/`) for its
own feature work — the two planning surfaces are distinct and do not share history. These are
structured planning notes for this repo's own workflow, gitignored and not part of the published
extension.
