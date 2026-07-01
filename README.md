# Cove Extensions Monorepo

This repository is the Cove extensions monorepo, converted from a one-repo-per-extension layout
into a single multi-extension repo following [yourcove](https://github.com/yourcove)'s official
`multi-extension-repo-template` pattern.

## Extensions

Extensions are registered in [`catalog.json`](catalog.json), the source of truth CI reads to
compute its build matrix. Today the catalog holds one entry:

- **Renamer** (`extensions/Renamer/`) — a file-renaming extension for a self-hosted Cove media
  library.

This list will grow as more entries are added to `catalog.json`.

## Building

Build the shared solution from the repo root:

```sh
dotnet build Renamer.slnx
```

`Directory.Build.props`/`Directory.Build.targets` at this root auto-wire every project against
`Cove.Sdk` (transitively `Cove.Plugins` + `Cove.Core`), either from a local sibling `../cove`
checkout (if present) or from NuGet — individual extensions do not declare their own Cove
reference.

## Docs

- Per-extension documentation and changelogs live under each extension's own folder:
  `extensions/<Name>/docs/`, `extensions/<Name>/CHANGELOG.md`.
- Repo-wide process docs live in [`docs/`](docs/): [`docs/BRANCHING.md`](docs/BRANCHING.md) and
  [`docs/RELEASING.md`](docs/RELEASING.md).
