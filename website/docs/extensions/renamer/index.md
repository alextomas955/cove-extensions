---
id: index
title: Renamer
---

Renamer bulk-renames — and optionally relocates — library items from configurable metadata
templates. It updates the file on disk and its Cove database record together, previews every
change before touching disk, and supports undo of the last batch.

## In this section

- [User guide](./renamer/guide) — enable Renamer, set a naming template, preview with a dry run,
  rename, and undo.
- [Settings reference](./renamer/settings) — every setting, grouped by panel section, with defaults.
- [Naming templates](./renamer/templates) — the template tokens (`$title`, `$resolution`, …),
  presets, and worked examples.
- [Architecture](./renamer/architecture) — how the extension turns an option change into a file
  moved on disk and a database record updated.
- [Changelog](./renamer/changelog) — user-facing changes, newest first.

## Install and build

For install, build, and local dev deploy instructions, see the extension's
[README on GitHub](https://github.com/alextomas955/cove-extensions/blob/main/extensions/Renamer/README.md).
