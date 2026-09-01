# Renamer

A Cove extension (`com.alextomas955.renamer`) that bulk-renames — and optionally relocates —
library items from configurable metadata templates. It updates the file on disk and its Cove
database record together, previews every change before touching disk, and can undo the last rename
up to a bounded size.

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

| Path                                     | Role                                                                                   |
| ---------------------------------------- | -------------------------------------------------------------------------------------- |
| `src/Renamer/`                           | The extension class library (`IExtension`) — engine, planner, executor, API endpoints. |
| `src/Renamer.Ui/`                        | The settings/preview panel bundle (React/TypeScript → `dist/index.mjs`).               |
| `src/Renamer.Tests/`                     | Tests that need no Cove source checkout.                                               |
| `src/Renamer.Cove.Tests/`                | Tests that need a real `CoveContext`, so they need a Cove source checkout.             |
| `e2e/`                                   | This extension's Playwright suite (run through the shared `tests/e2e` harness).        |
| `registry/com.alextomas955.renamer.json` | The registry manifest for this extension.                                              |

## Build and test

Build the whole monorepo (including this extension) from the repo root:

```sh
dotnet build CoveExtensions.slnx
```

Run the tests that need no Cove checkout, from the repo root:

```sh
dotnet test --project extensions/Renamer/src/Renamer.Tests/Renamer.Tests.csproj
```

Run the rest, which need a `../cove` sibling checkout:

```sh
dotnet test --project extensions/Renamer/src/Renamer.Cove.Tests/Renamer.Cove.Tests.csproj
```

Run them one after another rather than concurrently — `src/Renamer.Tests/README.md` has the
drive-letter reason, and which project a new test belongs in.

## Frontend (the settings panel)

The panel bundle is built with an offline, vendored `@cove/extension-sdk` tarball (`npm install`
resolves it from `src/Renamer.Ui/vendor/`, no registry access needed). The panel's wire types are
generated from the committed OpenAPI document and gitignored, so generate them from the repo root
before the first frontend command on a fresh clone:

```sh
npm ci --no-workspaces
npm run generate:wire
```

Then, from `src/Renamer.Ui/`:

```sh
npm ci            # first time only (offline; installs the vendored SDK)
npm run verify    # typecheck + format:check + check-classes + check-host-imports + tests
npm run build     # rebuild dist/index.mjs
```

`npm run typecheck` and `npm run test` regenerate the wire types themselves, so `verify` works once
the root install exists.

`dist/` is build output and is not committed — it is gitignored. CI rebuilds the bundle from source
with `npm run build` and packages the freshly built `dist/index.mjs` into the release, so you do not
need to build or commit the bundle for a normal source change.

## Local dev deploy

`scripts/deploy-dev.ps1` runs the full build → frontend-build → assemble → deploy → restart loop
against a local Cove dev instance. It builds against a local sibling `../cove` checkout (or
`$COVE_REPO`) so the extension is ABI-identical to the running host.

Invoke it as `pwsh` on any OS — Windows PowerShell 5.1 does not define the `$IsWindows` variable the
script reads. Only the _default_ data root is Windows-specific: with no `COVE_HOME` set the script
falls back to the per-user local-application-data `cove` folder, which exists on Windows only, so on
macOS and Linux you must set `COVE_HOME`. It throws there rather than guessing, because a guessed data
root deploys into a directory Cove never reads and then reports success.

The assemble step is the shared `scripts/assemble-package.mjs` and installs the file set
`extensions/catalog.json` declares for Renamer — the same set a release ships — so a bug you hit in
dev is a bug in the shipped shape.

## Releasing

A release is cut by pushing a tag of the form `renamer/v<semver>` (e.g. `renamer/v0.1.0`). See the
repo-wide [Releasing](https://alextomas955.github.io/cove-extensions/contributing/releasing) guide
for the full process.
