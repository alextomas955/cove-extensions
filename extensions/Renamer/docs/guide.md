---
id: guide
title: User guide
sidebar_position: 2
---

Renamer bulk-renames — and optionally relocates — your Cove library items from the metadata Cove
already has, using a naming template you control. It previews every change before touching disk,
updates the file and its Cove database record together, and can undo the last batch.

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
   _Date – Title [Resolution]_) or type your own template.
2. Watch the **live preview** below the field — it shows the new name for a few sample items and
   updates as you type. Nothing is renamed yet.
3. To move files into folders as well as rename them, fill in the **folder template** (for example
   `$studio / $year`). Leave it blank to rename each file in place.

If a token might be empty for some items, wrap it in a `{ … }` group so its surrounding punctuation
disappears when the value is missing — `$title{ [$resolution]}` produces `My Movie [1080p]` when
the resolution is known and `My Movie` when it isn't. See [Naming templates](./templates) for the
full token list and the grouping rules.

## Preview with a dry run

A dry run scans your whole library and shows exactly what _would_ happen — old name → new name, the
destination, and any warnings — without changing anything.

1. In the **Run & automation** section, click **Dry run**. While the library scans, a progress bar
   shows how far along it is, the number scanned so far, and an estimated time left.
2. When the scan finishes, the line above the table gives the exact totals: how many files will
   change, how many need attention, how many are unchanged, and how many were scanned.
3. Use the filter (All / Will change / Needs attention / No change) to narrow the table. The
   counts on the buttons always describe the whole scan, so they don't move as you switch between
   them.
4. To find a particular file, type part of its path or its new name into the search box. The search
   runs on the server and covers the current path, the new path, the new name and the destination
   folder, ignoring case.
5. Scroll the table to load more rows. Rows arrive in scan order — videos, then images, then audio,
   each in library order — and the line under the table says how many are loaded and whether there
   are more. On a large library the server searches in stages: if it says it paused, keep scrolling
   (or click **Keep searching**) and it picks up where it left off.
6. The dry run uses your current settings, including edits you haven't saved yet, so you can
   iterate on the template and re-run until the preview looks right. If something looks wrong,
   adjust the template or the relevant setting and dry-run again.

## Rename

1. When the preview looks right, **save** your settings (the sticky Save bar at the bottom).
2. Click **Rename all files** (in the panel or from the dry-run footer). If you started it from the
   dry-run footer, a progress bar and the current phase (planning, then per-file) show while it runs.
3. Renamer renames each file and updates its Cove record together. A file is never renamed onto an
   existing file — a collision gets a numbered suffix such as `(1)` instead.

## Undo the last batch

If a rename batch wasn't what you wanted, open the **Undo** section and click **Undo last batch**
(behind a confirmation). Undo restores the previous names and locations of the most recent batch.

Undo is deliberately small, so know what it covers:

- Only the **most recent** rename is offered — starting another rename supersedes it.
- A rename is recorded whatever its size, and is kept for **7 days** before it expires. The dry run
  is the check to make before that window closes.
- A file that cannot go back — its original folder is gone, or something else now occupies the old
  name — stays pending, so undoing again after you fix the cause finishes the job.
- Undo does **not** re-create a source folder that ["Delete the source folder when a move leaves it
  empty"](./settings#destination-routing) removed.

### If a rename can't be undone

Re-running with a different template is a real recovery path: change the template, dry-run it, and
rename again — Renamer computes each name from the item's metadata, so it will produce the new names
just as reliably as it produced the ones you don't want.

What it cannot do is take you back to the names your files had **before Renamer first ran**. Nothing
records those — Renamer reads a file's current name, and Cove stores only its current name too. So a
re-run is how you change your mind about a template. Before the first run on a set of files, the
[dry run](#preview-with-a-dry-run) is the check that matters.

## Common tasks

- **Rename only curated items** — turn on _Only rename organized items_ (What gets renamed).
- **Keep files organized into folders by studio/year** — set a folder template like
  `$studio / $year` and, if the destination is outside the source folder, add that root under
  _Allowed roots_ (Destination routing → Advanced).
- **Route certain studios or tags to specific drives** — use _Per-studio destinations_ or
  _Per-tag destinations_ (Destination routing).
- **Skip certain items entirely** — add exclude rules by tag, studio, or path (Advanced → Excludes).

Every one of these is documented field-by-field in the [Settings reference](./settings).
