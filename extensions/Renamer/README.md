# Renamer

Renamer is a Cove extension (`com.alextomas955.renamer`) that gives the files in your library tidy,
consistent names built from their metadata, and can sort them into folders. A dry run shows every
change before anything moves, and the last rename can be undone for 7 days.

![The Renamer settings page, with the filename template and a live preview of the new names.](docs/img/settings-overview.jpg)

## Install

1. In Cove 1.5.0 or later, open **Settings**, and under **Extensions** select **Discover**.
2. Search for **Renamer**, then select **Install**.

To install a specific release, download its ZIP from
[Releases](https://github.com/alextomas955/cove-extensions/releases) and use **Install from ZIP…** in
the **⋯** menu on the same page.

## Documentation

The docs site has step-by-step guides with screenshots:

- [Quick start](https://alextomas955.github.io/cove-extensions/extensions/renamer/quick-start) - install, pick a pattern, preview and rename.
- [How-to guides](https://alextomas955.github.io/cove-extensions/extensions/renamer/how-to/sort-into-folders) - folders, per-studio routing, renaming a few items, undo.
- [Troubleshooting](https://alextomas955.github.io/cove-extensions/extensions/renamer/troubleshooting) - what each badge and message means.
- [Naming templates](https://alextomas955.github.io/cove-extensions/extensions/renamer/templates) and [Settings reference](https://alextomas955.github.io/cove-extensions/extensions/renamer/settings) - every token and setting.
- [Changelog](https://alextomas955.github.io/cove-extensions/extensions/renamer/changelog).

How it works inside: [Architecture](https://alextomas955.github.io/cove-extensions/extensions/renamer/architecture).

The rest of this file is for contributors working on the extension itself.

## Layout

| Path                                     | Role                                                                                   |
| ---------------------------------------- | -------------------------------------------------------------------------------------- |
| `src/Renamer/`                           | The extension class library (`IExtension`) - engine, planner, executor, API endpoints. |
| `src/Renamer.Ui/`                        | The settings/preview panel bundle (React/TypeScript → `dist/index.mjs`).               |
| `src/Renamer.Tests/`                     | The backend suite. Needs a `../cove` source checkout.                                  |
| `e2e/`                                   | This extension's Playwright suite (run through the shared `tests/e2e` harness).        |
| `registry/com.alextomas955.renamer.json` | The registry manifest for this extension.                                              |

## Build and test

Build the whole monorepo (including this extension) from the repo root:

```sh
dotnet build CoveExtensions.slnx
```

Run the backend test suite, from the repo root:

```sh
dotnet test --project extensions/Renamer/src/Renamer.Tests/Renamer.Tests.csproj
```

This needs a `../cove` sibling checkout, or `COVE_REPO` pointed at one. Without it the build stops
with one error rather than running a smaller set.

## Frontend (the settings panel)

The panel bundle is built with an offline, vendored `@cove/extension-sdk` tarball (`npm ci`
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

`dist/` is build output and is not committed - it is gitignored. CI rebuilds the bundle from source
with `npm run build` and packages the freshly built `dist/index.mjs` into the release, so you do not
need to build or commit the bundle for a normal source change.

## Local dev deploy

```sh
pwsh scripts/deploy-dev.ps1
```

This builds against the `../cove` sibling (or `$COVE_REPO`), assembles the file set a release ships
and installs it into a local Cove. Restart Cove afterwards. On macOS and Linux set `COVE_HOME` to the
Cove data directory. [Deploy into a local Cove host](https://alextomas955.github.io/cove-extensions/contributing/development#deploy-into-a-local-cove-host)
has the details.

## Releasing

A release is cut by pushing a tag of the form `renamer/v<semver>` (e.g. `renamer/v0.1.0`). See the
repo-wide [Releasing](https://alextomas955.github.io/cove-extensions/contributing/releasing) guide
for the full process.
