---
id: guide
title: User guide
sidebar_position: 2
---

Renamer bulk-renames — and optionally relocates — your Cove library items from the metadata Cove
already has, using a naming template you control. It previews every change before touching disk,
updates the file and its Cove database record together, and can undo the last rename.

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

A row can also be flagged because its move would cross to another drive and the temporary copy's path
would be too long. Those rows are worth resolving before you rename — see
[If the confirmation warns about a path being too long](#if-the-confirmation-warns-about-a-path-being-too-long).

## Rename

1. When the preview looks right, **save** your settings (the sticky Save bar at the bottom).
2. Click **Rename all files** (in the panel or from the dry-run footer). If you started it from the
   dry-run footer, a progress bar and the current phase (planning, then per-file) show while it runs.
3. Renamer renames each file and updates its Cove record together. A file is never renamed onto an
   existing file — a collision gets a numbered suffix such as `(1)` instead.

If the panel says it **couldn't confirm the rename**, the job stopped telling Renamer how it was
getting on — it did not necessarily stop working. Some of your files may already have been renamed.
Run a dry run again to see where your library actually stands before you start another rename.

When a move crosses to another drive, Renamer copies the file, verifies the copy, and only then
removes the original. If the machine loses power in the middle of that, you may find one stray file
in the destination folder, named after the file being moved with `.rnm` and eight characters added.
Nothing reads it, the next attempt writes a different one, and you can delete it. Renamer will not
delete it for you, because it never removes a file it did not create.

### If the confirmation warns about a path being too long

That temporary name is longer than the final one, so a file can fit its new name and still not fit
while the copy is in flight. Renamer checks for this before you approve, and the confirmation says so:

```text
⚠ 3 cannot be copied across drives — the temporary copy's path would be too long.
  Shorten the destination folder or the filename template for them.
```

Two remedies, either of which is enough:

- **Shorten the destination folder.** A destination closer to the drive root leaves more room for the
  name. This is usually the quicker fix, because it applies to every file heading there.
- **Shorten the filename template.** Drop a token, or shorten a separator, so the generated name is
  smaller. See [Templates](./templates.md).

The check only reports — it never renames a file differently to make it fit. Your names are exactly
what the preview showed, whether or not this warning appears, so you can dry-run again after changing
a setting and compare the two previews directly.

This applies only to moves that cross drives. A rename within one drive never mints the temporary
name, so it is never affected.

## Undo the last rename

If a rename wasn't what you wanted, open the **Undo last rename** section and click **Undo last
rename** (behind a confirmation). Undo moves the files back to the names and folders they came from
and updates their Cove records to match. The companion files that travelled with them come back too
— a same-name neighbour such as a `.srt` subtitle, and the captions Cove tracks for the item.

The panel states what it will act on before you press it:

```text
Last rename: 2 items renamed · undo available until August 18, 2026
```

Know what undo covers:

- Undo is kept for **7 days**. Several renames can be waiting at once, so starting another rename no
  longer discards the one before it.
- The panel and the button both reach the most recent rename that **still has files to put back**.
  When two renames are waiting, the newer one is offered first. The older one is not lost — it keeps
  its own 7 days, and the panel offers it again as soon as the newer one has nothing left to restore.
  What you cannot do is reach past the newer rename to get to it.
- A rename expires as a whole. When its 7 days are up, everything it holds goes, including any part
  you had not restored yet.
- Any rename can be undone, whatever its size.
- **Rename all files** renames each media kind as its own batch, so one undo restores one kind — the
  **last kind** the run reached. If your library holds videos and images, undoing once after a
  whole-library rename brings back one of them, not both. The success message says so when the run
  finishes.
- A pending undo survives an update or a reinstall of Renamer, because the record lives in Cove's
  database rather than in the extension's own folder.
- Undo does **not** re-create a source folder that ["Delete the source folder when a move leaves it
  empty"](./settings#destination-routing) removed.

### If some files could not be moved back

A file can be locked by another program, a drive can be unmounted, or something can already sit in
the slot a file came from. Undo restores the rest and names the first problem it hit, and the panel
then reads:

```text
Last rename: 1 of 2 restored · 1 remaining · undo available until August 18, 2026
```

Clear the cause and undo again — the second run acts only on what is left, for as long as the rename
is still inside its 7 days. If you started another rename in between, the panel offers that newer one
first; once it has nothing left to put back, yours is offered again. One case is final rather than
worth retrying: a file that is no longer in your library cannot be restored, because Renamer reads its
current location from Cove. The panel counts those apart.

A file can also come back without a companion, when the neighbour's original slot is taken. The media
file is restored and its Cove record is correct, and the message names the companion that stayed
behind, because clearing that slot is yours to do.

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
