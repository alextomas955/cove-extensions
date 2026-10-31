# Scan and Identify

Scan and Identify is a Cove extension (`com.alextomas955.scanandidentify`) that takes a library from
newly imported files to tagged items in one run. It scans for new files, fills in the fingerprints
Cove needs to match them, identifies them, and then generates the images Cove shows for each item.

## Install

1. In Cove 1.7.0 or later, open **Settings**, and under **Extensions** select **Discover**.
2. Search for **Scan and Identify**, then select **Install**.

To install a specific release, download its ZIP from
[Releases](https://github.com/alextomas955/cove-extensions/releases) and use **Install from ZIP…** in
the **⋯** menu on the same page.

## Documentation

[What it does](docs/index.md) describes the run for someone who uses Cove.

The rest of this file is for contributors working on the extension itself.

## Layout

| Path                                             | Role                                                                    |
| ------------------------------------------------ | ----------------------------------------------------------------------- |
| `src/ScanAndIdentify/`                           | The extension class library (`IExtension`) - API endpoints and the run. |
| `src/ScanAndIdentify.Ui/`                        | The settings panel bundle (React/TypeScript → `dist/index.mjs`).        |
| `src/ScanAndIdentify.Tests/`                     | The backend suite. Needs a `../cove` source checkout.                   |
| `registry/com.alextomas955.scanandidentify.json` | The registry manifest for this extension.                               |

## Routes

The settings panel reads and writes the options document and the run record through the extension's
own routes, under `/api/extensions/com.alextomas955.scanandidentify`:

| Route              | What it does                                                                        |
| ------------------ | ----------------------------------------------------------------------------------- |
| `GET /options`     | Answers the saved options, or the defaults when nothing has been saved.             |
| `PUT /options`     | Replaces them. A body it cannot read is a `400` with code `INVALID_OPTIONS`.        |
| `POST /run`        | Claims a run, or answers `409 RUN_IN_FLIGHT` when one is already going.             |
| `GET /run`         | Answers the run with every unsettled stage re-read from its host job, or `204`.     |
| `POST /run/stages` | Records a stage being started, then the job id it was started as.                   |
| `POST /run/end`    | Ends a run the stage states cannot end by themselves, such as one a person stopped. |

Both options routes require `extensions.configure`. The four run routes require `library.scan`,
`jobs.run`, `library.identify` and `extensions.configure` together, the last because the run view
answers the options document the options routes gate on it.

The panel starts each stage through Cove's own routes with the signed-in session; the extension
enqueues nothing. The options document and the run record are stored as one value each, under the
store keys `options` and `run`.

## Build and test

Build the whole monorepo (including this extension) from the repo root:

```sh
dotnet build CoveExtensions.slnx
```

Run the backend test suite, from the repo root:

```sh
dotnet test --project extensions/ScanAndIdentify/src/ScanAndIdentify.Tests/ScanAndIdentify.Tests.csproj
```

This needs a `../cove` sibling checkout, or `COVE_REPO` pointed at one. Without it the build stops
with one error rather than running a smaller set.

## Frontend (the settings panel)

The panel bundle is built with an offline, vendored `@cove/extension-sdk` tarball (`npm ci` resolves
it from `src/ScanAndIdentify.Ui/vendor/`, no registry access needed). The panel's wire types are
generated from the committed OpenAPI document and gitignored, so generate them from the repo root
before the first frontend command on a fresh clone:

```sh
npm ci --no-workspaces
npm run generate:wire
```

Then, from `src/ScanAndIdentify.Ui/`:

```sh
npm ci            # first time only (offline; installs the vendored SDK)
npm run verify    # typecheck + format:check + check-classes + check-host-imports + tests
npm run build     # rebuild dist/index.mjs
```

`dist/` is build output and is not committed - it is gitignored. CI rebuilds the bundle from source
and packages the freshly built `dist/index.mjs` into the release, so you do not need to build or
commit the bundle for a normal source change.

## Local dev deploy

```sh
pwsh scripts/deploy-dev.ps1
```

This builds against the `../cove` sibling (or `$COVE_REPO`), assembles the file set a release ships
and installs it into a local Cove. Restart Cove afterwards, because the host loads extension
assemblies once per run. On macOS and Linux set `COVE_HOME` to the Cove data directory.

## Releasing

A release is cut by pushing a tag of the form `scan-and-identify/v<semver>` (e.g.
`scan-and-identify/v0.1.0`). See the repo-wide
[Releasing](https://alextomas955.github.io/cove-extensions/contributing/releasing) guide for the full
process.
