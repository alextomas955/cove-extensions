# Whisparr Sync

A Cove extension (`com.alextomas955.whisparrsync`) that connects Cove to the Whisparr instance you
configure, calling out to it over the network with an API key you supply and Cove holds server-side.

What exists today is a settings tab that tests a connection, reports which Whisparr generation
answered, keeps each generation's connection separately, and registers Cove's import callback in the
instance; the import path behind that callback, which brings a delivered file into the Cove library;
and a Whisparr control on studio and performer pages and in their selection bars that monitors and
unmonitors an entity on the connected instance, registers the scenes Cove holds that the instance's
catalogue does not, asks Whisparr to link the files Cove already holds for it into place, and asks
Whisparr to search for what a monitored entity wants. So this extension
changes your library, changes what a third party monitors, and has one action that spends the user's
bandwidth and disk. That last one is a separately named row on a single entity, it is absent from
the selection bar, and it is the only thing here that downloads.

## Documentation

**Start with the user documentation:**

- **[Whisparr Sync docs](https://alextomas955.github.io/cove-extensions/extensions/whisparr-sync)** - overview and index
- **[Monitor a studio or a performer](https://alextomas955.github.io/cove-extensions/extensions/whisparr-sync/monitoring)** - the entity control, the two scopes and what each costs
- **[Settings reference](https://alextomas955.github.io/cove-extensions/extensions/whisparr-sync/settings)** - every setting on the tab

The rest of this file is for contributors working on the extension itself.

## Layout

| Path                                          | Role                                                                            |
| --------------------------------------------- | ------------------------------------------------------------------------------- |
| `src/WhisparrSync/`                           | The extension class library (`IExtension`) - the load manifest and its API.     |
| `src/WhisparrSync.Ui/`                        | The settings panel bundle (React/TypeScript → `dist/index.mjs`).                |
| `src/WhisparrSync.Tests/`                     | Unit tests, including the wire-document drift check.                            |
| `wire/openapi.json`                           | The wire contract, emitted from the shipped registrations - never authored.     |
| `e2e/`                                        | This extension's Playwright suite (run through the shared `tests/e2e` harness). |
| `registry/com.alextomas955.whisparrsync.json` | The registry manifest for this extension.                                       |

## Build and test

Build the whole monorepo (including this extension) from the repo root:

```sh
dotnet build CoveExtensions.slnx
```

Run this extension's unit tier:

```sh
dotnet test --project src/WhisparrSync.Tests/WhisparrSync.Tests.csproj
```

## Frontend (the settings panel)

The panel bundle is built with an offline, vendored `@cove/extension-sdk` tarball (`npm install`
resolves it from `src/WhisparrSync.Ui/vendor/`, no registry access needed). The panel's wire types are
generated from the committed OpenAPI document and gitignored, so generate them from the repo root
before the first frontend command on a fresh clone:

```sh
npm ci --no-workspaces
npm run generate:wire
```

Then, from `src/WhisparrSync.Ui/`:

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

## Releasing

A release is cut by pushing a tag of the form `whisparr-sync/v<semver>` (e.g.
`whisparr-sync/v1.0.0`). See the repo-wide
[Releasing](https://alextomas955.github.io/cove-extensions/contributing/releasing) guide for the full
process.

Nothing is published yet, and `registry/com.alextomas955.whisparrsync.json` says so with an empty
`versions` list. That list is also what a tag push checks first: the workflow's release-tag step reads
`versions[0]` and fails when no row describes the version being released, before any build runs. So a
release here begins with the registry row for that version, not with the tag.
