# Renamer

A Cove extension (`com.alextomas955.renamer`) that bulk-renames — and optionally relocates —
library items from configurable metadata templates. It updates the file on disk and its Cove
database record together, previews every change before touching disk, and supports undo of the
last batch.

## Documentation

**User docs live on the docs site — start there:**

- **[Renamer docs](https://alextomas955.github.io/cove-extensions/extensions/renamer)** — overview and index
- [User guide](https://alextomas955.github.io/cove-extensions/extensions/renamer/guide) — enable, set a template, dry-run, rename, undo
- [Settings reference](https://alextomas955.github.io/cove-extensions/extensions/renamer/settings) — every setting, with defaults
- [Naming templates](https://alextomas955.github.io/cove-extensions/extensions/renamer/templates) — tokens, presets, and examples

Design and safety model: [Architecture](https://alextomas955.github.io/cove-extensions/extensions/renamer/architecture).
Release history: [Changelog](https://alextomas955.github.io/cove-extensions/extensions/renamer/changelog).

The rest of this file is for contributors working on the extension itself.

## Layout

| Path | Role |
| ---- | ---- |
| `src/Renamer/` | The extension class library (`IExtension`) — engine, planner, executor, API endpoints. |
| `src/Renamer.Ui/` | The settings/preview panel bundle (React/TypeScript → `dist/index.mjs`). |
| `src/Renamer.Tests/` | Unit + concurrency tests. |
| `e2e/` | This extension's Playwright suite (run through the shared `tests/e2e` harness). |
| `extensions/com.alextomas955.renamer.json` | The registry manifest for this extension. |

## Build and test

Build the whole monorepo (including this extension) from the repo root:

```sh
dotnet build CoveExtensions.slnx
```

Run the unit tier (the pure-core tests that need no live Cove checkout):

```sh
dotnet test src/Renamer.Tests/Renamer.Tests.csproj
```

## Frontend (the settings panel)

The panel bundle is built with an offline, vendored `@cove/extension-sdk` tarball (`npm install`
resolves it from `src/Renamer.Ui/vendor/`, no registry access needed). From `src/Renamer.Ui/`:

```sh
npm install       # first time only (offline; installs the vendored SDK)
npm run verify    # typecheck + lint + format:check + check-classes + tests
npm run build     # rebuild dist/index.mjs
```

`dist/index.mjs` is committed. If you change any UI source, run `npm run build` and commit the
rebuilt bundle — CI's stale-bundle gate fails if the committed bundle does not match source.

## Local dev deploy

`scripts/deploy-dev.ps1` runs the full build → strip-verify → frontend-build → deploy → restart
loop against a local Cove dev instance (Windows). It builds against a local sibling `../cove`
checkout (or `$COVE_REPO`) so the extension is ABI-identical to the running host.

## Releasing

A release is cut by pushing a tag of the form `renamer/v<semver>` (e.g. `renamer/v0.1.0`). See the
repo-wide [Releasing](https://alextomas955.github.io/cove-extensions/contributing/releasing) guide
for the full process.
