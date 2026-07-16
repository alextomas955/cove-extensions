# Cove Extensions Monorepo

[![Build and Release Extensions](https://github.com/alextomas955/cove-extensions/actions/workflows/build.yml/badge.svg)](https://github.com/alextomas955/cove-extensions/actions/workflows/build.yml)
[![CodeQL](https://github.com/alextomas955/cove-extensions/actions/workflows/codeql.yml/badge.svg)](https://github.com/alextomas955/cove-extensions/actions/workflows/codeql.yml)
[![License: AGPL v3](https://img.shields.io/badge/License-AGPL%20v3-blue.svg)](LICENSE)

This repository is the Cove extensions monorepo, converted from a one-repo-per-extension layout
into a single multi-extension repo following [yourcove](https://github.com/yourcove)'s official
`multi-extension-repo-template` pattern.

> **Community project.** These are personal, third-party extensions maintained by alextomas955.
> They are not affiliated with, or endorsed by, the [Cove](https://github.com/yourcove/cove) project.

## Extensions

Extensions are registered in [`extensions/catalog.json`](extensions/catalog.json), the source of
truth CI reads to compute its build matrix. The catalog currently ships:

- **Renamer** ([`extensions/Renamer/`](extensions/Renamer/)) — bulk metadata-driven rename and
  relocate for a self-hosted Cove media library.
  [Docs](https://alextomas955.github.io/cove-extensions/extensions/renamer).
- **WhisparrSync** ([`extensions/WhisparrSync/`](extensions/WhisparrSync/)) — connects a Cove
  library to a [Whisparr](https://whisparr.com) acquisition pipeline.
  [Docs](https://alextomas955.github.io/cove-extensions/extensions/whisparr-sync).

This list grows as more entries are added to `extensions/catalog.json`.

## Building

Build the shared solution from the repo root:

```sh
dotnet build CoveExtensions.slnx
```

`Directory.Build.props`/`Directory.Build.targets` at this root auto-wire every project against
`Cove.Sdk` (transitively `Cove.Plugins` + `Cove.Core`), either from a local sibling `../cove`
checkout (if present) or from NuGet — individual extensions do not declare their own Cove
reference. Package versions are centralized via NuGet Central Package Management in the root
`Directory.Packages.props`; the `Cove.Sdk` pin stays the `$(CoveSdkVersion)` property that the
extension-repo validator reads as the host-SDK version floor.

## Adding an extension

Every extension is a dynamically-loaded `Cove.Sdk` plugin: implement `IExtension` (via
`FullExtensionBase`), ship an `extension.json` manifest, and register the extension in
[`extensions/catalog.json`](extensions/catalog.json). See
[`CONTRIBUTING.md`](CONTRIBUTING.md#adding-or-extending-an-extension) for the full contract.

## Docs

- Full docs site: [alextomas955.github.io/cove-extensions](https://alextomas955.github.io/cove-extensions/).
- Per-extension documentation and changelogs live under each extension's own folder:
  `extensions/<Name>/docs/`, `extensions/<Name>/CHANGELOG.md`.
- Repo-wide process docs live on the docs site under Contributing:
  [Branching](https://alextomas955.github.io/cove-extensions/contributing/branching),
  [Releasing](https://alextomas955.github.io/cove-extensions/contributing/releasing), and
  [Authoring E2E tests](https://alextomas955.github.io/cove-extensions/contributing/authoring-e2e).

## License

Copyright (C) 2026 alextomas955

Licensed under the [GNU Affero General Public License v3.0](LICENSE) (AGPL-3.0).
