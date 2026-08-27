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

1. In the **Filename & folder** section, either pick a **preset** chip (for example
   _Date – Title [Resolution]_) or type your own template.
2. Watch the **live preview** below the field — it shows the new name for a few sample items and
   updates as you type. Nothing is renamed yet.
3. To move files into folders as well as rename them, fill in the **folder template** (for example
   `$studio/$year`). Leave it blank to rename each file in place. The folders are made under the
   library path shown in **Under**, which defaults to whichever of Cove's library paths already
   holds the file.

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
5. Rows load as you scroll, in scan order — videos, then images, then audio, each in library
   order. On a large library the server reads it in stages, and a stage can pass with nothing in it
   that matches your filter; the table keeps asking for the next stage until the rows in view are
   covered, so a narrow filter fills in on its own. The line under the table says how many rows are
   loaded and, until the whole library has been read, how many items have been checked so far.
   **Load more** (**Keep searching** with a search active) asks for the next stage straight away.
6. The dry run uses your current settings, including edits you haven't saved yet, so you can
   iterate on the template and re-run until the preview looks right. If something looks wrong,
   adjust the template or the relevant setting and dry-run again.

Every row names its own outcome, so you never have to infer a skip from the destination cell. A
row that will change carries a badge only when its name was **Numbered to avoid a clash** or
**Cleaned for the filesystem**; a row with nothing to do carries a gray **No change needed**. A row
that needs attention always says why:

| Badge                                                        | Why the row stopped                                                                            |
| ------------------------------------------------------------ | ---------------------------------------------------------------------------------------------- |
| Skipped — needs a required field                             | A token listed in _Required fields_ resolved to nothing for this item.                         |
| Skipped — an exclude rule matched                            | One of your exclude rules covers it.                                                           |
| Skipped — name conflict                                      | Another file already holds the computed name, and Renamer never overwrites.                    |
| Skipped — file missing on disk                               | Cove holds a record for a file that is not there.                                              |
| Skipped — file in use                                        | Something else held the file open, and Renamer never forces a lock.                            |
| Skipped — permission denied                                  | Cove is not allowed to write where the row would go, or to move the file at all.               |
| Skipped — file is outside your Cove library                  | The destination measures from the file's own library path, and the file is under none of them. |
| Skipped — the rule's destination is no longer a library path | The root the matched rule names is no longer one of Cove's library paths.                      |
| Skipped — destination outside your allowed roots             | _Allowed roots_ does not cover where the rule would write.                                     |
| Skipped — path too long                                      | The full path would exceed _Full-path max length_.                                             |

A stop that is not a defect is gray instead: **Skipped — cancelled** means Cove shut down part-way
through the run. Nothing was half-written, and starting the rename again picks the row up.

Two badges are red rather than amber. **Too long to copy across drives** means that row moves to a
different drive, and Renamer copies to a temporary name beside the destination before putting the final
name in place. The temporary path is a little longer than the one the row shows, and it does not fit
_Full-path max length_, so the move cannot complete even though the new path does fit. Shorten the
destination folder or the filename template for that row. The confirm shown before a rename counts
these files too, so you see the warning whether you started from the dry run or from a list.
**Skipped — copy did not verify** means a cross-drive copy was written and then read back different, so
the file was left where it was — check the destination drive before running that row again.

## Rename

1. When the preview looks right, **save** your settings (the sticky Save bar at the bottom).
2. Click **Rename all files** (in the panel or from the dry-run footer). If you started it from the
   dry-run footer, a progress bar and the current phase (planning, then per-file) show while it runs.
3. Renamer renames each file and updates its Cove record together. A file is never renamed onto an
   existing file — a collision gets a numbered suffix such as `(1)` instead.

The run leaves one of three banners behind, and the difference between the last two decides whether
you have anything to check:

- **It worked**: "Renamed 412 files, 9 skipped. Undo covers only the last media kind in this run."
  The skipped figure is the _Needs attention_ count from the scan. The closing sentence is explained
  under [Undo the last rename](#undo-the-last-rename).
- **The job reported failure**: "Couldn't rename — [reason]. Nothing was changed; you can try again."
  Cove reported that the work stopped, so nothing was written. Fix the cause it names and run again.
- **The outcome is unknown**: "Couldn't confirm the rename — [reason]." Renamer stopped watching before
  the job reached a verdict, either because the job went ten minutes without reporting progress or
  because Cove stopped answering about it. It deliberately does _not_ say nothing changed: the job
  may still be running and may already have renamed files. Reload the page, check your library and
  the **Undo last rename** section, and only then run it again.

## Undo the last rename

If a rename wasn't what you wanted, open the **Undo last rename** section. One line there describes
the last rename — how many items it renamed, how long ago, and the date its undo window closes
("undo available until" plus the date) — with the button beside it.

Click **Undo last rename**. The confirmation quotes how many files it will move; confirm it, and
Renamer moves those files back to the names and folders they came from and updates their Cove records
to match. The companion files that travelled with them come back too — a same-name neighbour such as
a `.srt` subtitle, and the captions Cove tracks for the item.

Know what undo covers:

- A recorded rename is kept for **7 days**, and the panel states the expiry date rather than a
  countdown. Past it the line reads "undo expired" and the button is withheld: the files may still be
  where the rename left them, but the next rename drops that record with no further warning, so
  Renamer stops offering a restore it cannot promise. A record expires as a whole — including any
  part you had not restored yet.
- **A whole-library rename records each media kind separately**, which is why its banner closes with
  "Undo covers only the last media kind in this run." One undo restores that kind; the rule below
  decides which record the button offers next.
- Several renames can be waiting at once, and the button reaches the most recent one that **still has
  files to put back**. What you cannot do is reach past a newer rename to get to an older one.
- **After a partial undo, the line and the button quote what is left rather than what the rename
  started as** — "37 of 500 restored", "463 remaining", and a button offering those 463. Files that
  can never go back are counted separately in the same line, so a partly-undone rename describes
  itself instead of looking finished.
- A rename of more than **5,000 files** is **not recorded at all**, and both the rename confirmation
  and the dry-run footer say so before it runs. A whole-library rename usually lands here. It also
  clears any undo that was still pending, because a rename that large may have moved those files too.
- A file that cannot go back — something else now occupies the old name, the drive is unmounted, the
  file is locked — stays pending, so undoing again after you fix the cause finishes exactly the work
  that is left. One case is final rather than worth retrying: a file that is no longer in your library
  cannot be restored, because Renamer reads its current location from Cove.
- **A companion file can be stranded even when its media file comes back.** The result then reads
  "Undone — 40 files moved back to their original names. 2 companion files stayed behind ([which one, and why])." The
  video is where you wanted it; the subtitle beside it is not, and nothing else reports that.
- A pending undo survives an update or a reinstall of Renamer, because the record lives in Cove's
  database rather than in the extension's own folder.
- Undo does **not** re-create a source folder that ["Delete the source folder when a move leaves it
  empty"](./settings#sidecar-files-and-empty-folders) removed.

The panel's own standing note is narrower than this: it says only the most recent rename is kept,
which described an earlier version. The list above is what the current version does.

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
  `$studio/$year`. To put them under a different library path, pick it in **Under** beside the
  template.
- **Route certain studios or tags to specific drives** — use _Per-studio destinations_ or
  _Per-tag destinations_ (Destination routing).
- **Skip certain items entirely** — add exclude rules by tag, studio, or path (Advanced → Excludes).

Every one of these is documented field-by-field in the [Settings reference](./settings).
