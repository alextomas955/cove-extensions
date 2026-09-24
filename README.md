# Cove Extensions Monorepo

[![CI](https://github.com/alextomas955/cove-extensions/actions/workflows/ci.yml/badge.svg)](https://github.com/alextomas955/cove-extensions/actions/workflows/ci.yml)
[![CodeQL](https://github.com/alextomas955/cove-extensions/actions/workflows/codeql.yml/badge.svg)](https://github.com/alextomas955/cove-extensions/actions/workflows/codeql.yml)
[![License: AGPL v3](https://img.shields.io/badge/License-AGPL%20v3-blue.svg)](LICENSE)

Community extensions for [Cove](https://github.com/yourcove/cove), the self-hosted media library.

> **Community project.** These are personal, third-party extensions maintained by alextomas955.
> They are not affiliated with, or endorsed by, the Cove project.

**Docs, guides and screenshots: [alextomas955.github.io/cove-extensions](https://alextomas955.github.io/cove-extensions/)**

## Extensions

| Extension                      | What it does                                                                                                                                | Docs                                                                                         |
| ------------------------------ | ------------------------------------------------------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------- |
| [Renamer](extensions/Renamer/) | Renames your videos, images, audio and text documents to a pattern you choose, and sorts them into folders. Preview first, undo afterwards. | [Quick start](https://alextomas955.github.io/cove-extensions/extensions/renamer/quick-start) |

![Renamer's settings page in Cove, with a live preview of the new names.](extensions/Renamer/docs/img/settings-overview.jpg)

## Install an extension

In Cove, open **Settings**, and under **Extensions** select **Discover**. Search for the extension and
select **Install**. Each extension's quick start covers the rest.

## For contributors

The rest of this file is for people working on the code. The
[Contributing](https://alextomas955.github.io/cove-extensions/contributing) section of the docs site
has the full guides, including [Writing docs](https://alextomas955.github.io/cove-extensions/contributing/writing-docs).

This repository holds every extension in one place, following
[yourcove](https://github.com/yourcove)'s `multi-extension-repo-template` pattern. Extensions are
registered in [`extensions/catalog.json`](extensions/catalog.json), which CI reads to compute its build
matrix.

### Building

Build the shared solution from the repo root:

```sh
dotnet build CoveExtensions.slnx
```

Before building or verifying an extension's frontend, generate its wire types from the repo root -
they are derived from the committed OpenAPI document and gitignored, so a fresh clone has none and the
typecheck fails on a missing module:

```sh
npm ci --no-workspaces
npm run generate:wire
```

`Directory.Build.props`/`Directory.Build.targets` at this root auto-wire every project against
`Cove.Sdk` (transitively `Cove.Plugins` + `Cove.Core`), either from a local sibling `../cove`
checkout (if present) or from NuGet - individual extensions do not declare their own Cove
reference. Package versions are centralized via NuGet Central Package Management in the root
`Directory.Packages.props`; the `Cove.Sdk` pin stays the `$(CoveSdkVersion)` property, which derives
from `$(CoveMinVersion)` - the declared host floor that the extension-repo validator compares each
extension's `minCoveVersion` against.

### Adding an extension

Every extension is a dynamically-loaded `Cove.Sdk` plugin: implement `IExtension` (via
`FullExtensionBase`), ship an `extension.json` manifest, and register the extension in
[`extensions/catalog.json`](extensions/catalog.json). See
[`CONTRIBUTING.md`](CONTRIBUTING.md#adding-or-extending-an-extension) for the full contract.

### Docs

- User docs for each extension live in `extensions/<Name>/docs/`, with its changelog at
  `extensions/<Name>/CHANGELOG.md`. The docs site reads them from there.
- Repo-wide process docs are on the site under Contributing:
  [Branching](https://alextomas955.github.io/cove-extensions/contributing/branching),
  [Releasing](https://alextomas955.github.io/cove-extensions/contributing/releasing) and
  [Authoring E2E tests](https://alextomas955.github.io/cove-extensions/contributing/authoring-e2e).

## License

Copyright (C) 2026 alextomas955

Licensed under the [GNU Affero General Public License v3.0](LICENSE) (AGPL-3.0).
