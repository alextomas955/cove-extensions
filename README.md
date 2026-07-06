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
truth CI reads to compute its build matrix. Today the catalog holds one entry:

- **Renamer** (`extensions/Renamer/`) — a file-renaming extension for a self-hosted Cove media
  library.

This list will grow as more entries are added to `extensions/catalog.json`.

## Building

Build the shared solution from the repo root:

```sh
dotnet build CoveExtensions.slnx
```

`Directory.Build.props`/`Directory.Build.targets` at this root auto-wire every project against
`Cove.Sdk` (transitively `Cove.Plugins` + `Cove.Core`), either from a local sibling `../cove`
checkout (if present) or from NuGet — individual extensions do not declare their own Cove
reference.

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
