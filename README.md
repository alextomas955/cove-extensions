# Cove Extensions Monorepo

This repository is the Cove extensions monorepo, converted from a one-repo-per-extension layout
into a single multi-extension repo following [yourcove](https://github.com/yourcove)'s official
`multi-extension-repo-template` pattern.

Extensions are registered in `catalog.json`, the source of truth CI reads to compute its build
matrix. Today the catalog holds one entry, **Renamer**, a file-renaming extension for a
self-hosted Cove media library.

Build the shared solution from the repo root with `dotnet build Renamer.slnx`; each extension's
own documentation lives under its own `extensions/<Name>/` folder.
