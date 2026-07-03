# Renamer

A Cove extension (`com.alextomas955.renamer`) that bulk-renames — and optionally relocates —
library items from configurable metadata templates. It updates the file on disk and its Cove
database record together, previews every change before touching disk, and supports undo of the
last batch.

See [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) for the design and safety model, and
[`CHANGELOG.md`](CHANGELOG.md) for the release history.

## Layout

| Path | Role |
|------|------|
| `src/Renamer/` | The extension class library (`IExtension`) — engine, planner, executor, API endpoints. |
| `src/Renamer.Ui/` | The settings/preview panel bundle (React/TypeScript → `dist/index.mjs`). |
| `src/Renamer.Tests/` | Unit + concurrency tests. |
| `e2e/` | This extension's Playwright suite (run through the shared `tests/e2e` harness). |
| `extensions/com.alextomas955.renamer.json` | The registry manifest for this extension. |

## Build and test

Build the whole monorepo (including this extension) from the repo root:

```sh
dotnet build Renamer.slnx
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

A release is cut by pushing a tag of the form `renamer/v<semver>` (e.g. `renamer/v1.0.0`). See the
repo-wide [`docs/RELEASING.md`](../../docs/RELEASING.md) for the full process.
