---
id: guide
title: User guide
sidebar_position: 2
---

Renamer bulk-renames — and optionally relocates — your Cove library items from the metadata Cove
already has, using a naming template you control. It previews every change before touching disk,
updates the file and its Cove database record together, and lets you undo the last batch.

This guide walks the everyday workflow. For the meaning of every setting see the
[Settings reference](./settings); for the tokens you can put in a template see the
[Naming templates](./templates) reference.

## Enable Renamer

1. Install the extension into your Cove instance (drop the built extension into Cove's extensions
   folder, or install it from a release URL).
2. In Cove, open **Settings → Extensions** and confirm **Renamer** is enabled.
3. Open the **Rename** settings tab (Settings → Extensions → Rename). This is where you set the
   naming template and every other option, and where you run a rename.

## Set a naming template

The **filename template** decides what each file is named. It is made of plain text plus `$tokens`
that Cove fills in from each item's metadata — for example `$title` becomes the item's title and
`$resolution` becomes `1080p`.

1. In the **Filename & destination** section, either pick a **preset** chip (for example
   *Date – Title [Resolution]*) or type your own template.
2. Watch the **live preview** below the field — it shows the new name for a few sample items and
   updates as you type. Nothing is renamed yet.
3. To move files into folders as well as rename them, fill in the **folder template** (for example
   `$studio / $year`). Leave it blank to rename each file in place.

If a token might be empty for some items, wrap it in a `{ … }` group so its surrounding punctuation
disappears when the value is missing — `$title{ [$resolution]}` produces `My Movie [1080p]` when
the resolution is known and `My Movie` when it isn't. See [Naming templates](./templates) for the
full token list and the grouping rules.

## Preview with a dry run

A dry run scans your whole library and shows exactly what *would* happen — old name → new name, the
destination, and any warnings — without changing anything.

1. In the **Run & automation** section, click **Dry run**. While the library scans, a progress bar
   shows how far along it is, the number scanned so far, and an estimated time left.
2. Use the filter (All / Will change / Needs attention / No change) to review the results. The dry
   run uses your current settings, including edits you haven't saved yet, so you can iterate on the
   template and re-run until the preview looks right.
3. If something looks wrong, adjust the template or the relevant setting and dry-run again.

## Rename

1. When the preview looks right, **save** your settings (the sticky Save bar at the bottom).
2. Click **Rename all files** (in the panel or from the dry-run footer). If you started it from the
   dry-run footer, a progress bar and the current phase (planning, then per-file) show while it runs.
3. Renamer renames each file and updates its Cove record together. A file is never renamed onto an
   existing file — a collision gets a numbered suffix such as `(1)` instead.

## Undo the last batch

If a rename batch wasn't what you wanted, open the **Undo** section and click **Undo last batch**
(behind a confirmation). Undo restores the previous names and locations of the most recent batch.

Two limits are worth knowing:

- Only the **most recent** batch is undoable, and only until it's superseded by another batch.
- Undo does **not** re-create a source folder that ["Delete the source folder when a move leaves it
  empty"](./settings#destination-routing) removed.

## Common tasks

- **Rename only curated items** — turn on *Only rename organized items* (What gets renamed).
- **Keep files organized into folders by studio/year** — set a folder template like
  `$studio / $year` and, if the destination is outside the source folder, add that root under
  *Allowed roots* (Destination routing → Advanced).
- **Route certain studios or tags to specific drives** — use *Per-studio destinations* or
  *Per-tag destinations* (Destination routing).
- **Skip certain items entirely** — add exclude rules by tag, studio, or path (Advanced → Excludes).

Every one of these is documented field-by-field in the [Settings reference](./settings).
