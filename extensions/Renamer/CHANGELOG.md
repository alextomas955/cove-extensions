# Changelog

User-facing changes, newest first.

## 0.4.1 — The dry run finishes by itself, and a numbered name cannot outgrow your path limit

- **Dry run → Will change now loads every matching row on its own.** It used to stop after a handful —
  6 of 102 on a 7,459-item library — and clicking **Load more** often added one row or none at all, so
  reaching the end took a dozen clicks or more. The cause was that Cove's scan answers in fixed
  chunks of work rather than of rows, so a chunk containing no matches is a normal answer meaning
  "keep asking", and the list treated it as the end. It now keeps asking until it has filled the view,
  and the footer tells you how much of the library has been checked instead of asking you to scroll a
  list with nothing in it to scroll. The narrower your filter, the more this was costing you.
- **A file whose name has to be numbered can no longer be written past your path-length limit.** When
  the target name is already taken, Renamer appends a number — and that happened _after_ the path
  length was checked, so an item sitting at your configured limit was renamed to a path longer than
  the limit allows. The length is now re-checked once the final name is settled, both while previewing
  and while running, and such an item is skipped as too long rather than written. If you have never
  lowered the path-length setting you are very unlikely to have hit this.
- **An item holding more than one file is no longer renamed over and over without stopping.** With
  **Rename on update** switched on, editing such an item could start a rename that never finished:
  every pass wrote to your disk and to Cove's database, and it kept going until you restarted Cove. It
  now stops by itself. Nothing was lost when this happened, and your files came out correctly named —
  but it could run for as long as the host stayed up, so if you have **Rename on update** on and any
  item with two or more files, this is the reason to take this update.

## 0.4.0 — Destinations you pick, not paths you type

**Needs Cove 1.3.0.** An older host does not load Renamer at all — no Rename tab under Settings →
Extensions, no "Rename selected" on your lists — so stay on 0.3.0 until you have upgraded Cove.
Nothing here is a feature you lose. The floor rose because Renamer now relies on the host publishing
entity events for bulk mutations; without those, editing several items at once renames one of them
and says nothing about the rest.

**Preview a dry run before your first rename after upgrading.** Two of the changes below move files
once, including files that no rule of yours matches.

- **Every destination is now a library path you pick from Cove's own list, plus a folder template
  made under it.** _Where files go_, the per-studio and per-tag maps, the source-path rules and the
  unorganized route all share this one shape, and **you no longer type a path**. A typed copy of a
  library path silently pointed at nothing the moment you changed it in Cove; a picked root follows
  it. When Cove has one library path there is nothing to choose, and that path is the root of every
  rule you make.
- **Your destination rules convert on first load — except one kind, which is removed.** A rule that
  pointed inside a Cove library path keeps sending its items to the same folder, and those items do
  not move: `I:\Downloads\P\videos` with a folder template of `$studio` becomes the root
  `I:\Downloads\P` plus the template `videos/$studio`, the identical destination. A rule pointing
  **outside** every library path is **removed**, because there is no root to pick and inventing one
  would move files somewhere you never chose; each removed rule is named in Cove's log at first load,
  and its items now follow _Where files go_. To send files to another drive, add that drive as a
  library path in Cove's own settings, then pick it as the rule's root.
- **A folder template no longer buries a file one directory deeper every run.** It used to be applied
  to the folder the file was sitting in _at that moment_ — the previous run's own output — so
  `…/Ann Miller` became `…/Ann Miller/Ann Miller`, and with _Auto-rename on update_ on, a single edit
  ran the loop until the path was too long to write. A template is now measured from the destination's
  root, which no rename can move. **This moves an item that no rule matches, once:** with the default
  root, a file sitting below its library path moves up to directly under that path, however deep it
  was — `/media/library/2024/incoming/batch7/clip.mp4` with a template of `$performers` becomes
  `/media/library/Ann Miller/clip.mp4`, and stays there on every later run. To keep an intermediate
  level, put it in the template — `videos/$performers` rather than `$performers`.
- **Undo is much harder to lose.** It keeps **seven days** instead of only the most recent rename, so
  a background _Auto-rename on update_ can no longer silently discard the undo of the rename you ran
  minutes earlier; several renames can be waiting at once, and the panel offers the most recent one
  that still has files to put back. The 5,000-file ceiling is gone, so a whole-library rename is
  undoable. Subtitles and captions now come back with the video rather than being left under their
  new names. An undo stopped part-way — a locked file, an unmounted drive — can be retried and acts
  only on what is left, and the panel says where it got to. And the record now lives in Cove's
  database, so undo survives an update or reinstall of Renamer; anything pending from your previous
  version is carried over once, keeping its original date.
- **Tag and performer rules now follow the item, not its name.** Whitelists, blacklists, _Exclude by
  tag_ and _Per-tag destinations_ stored the tag's or performer's **name**, so renaming one in Cove
  quietly broke every rule pointing at it. They now store Cove's own stable id, and **your settings
  convert automatically** the first time the Rename settings page loads — there is nothing to
  re-enter. A stored name matching nothing in your library is dropped, since it could never have
  matched anything.
- **A cross-drive move can no longer delete a file of your own.** Renamer wrote the copy under a fixed
  working name and deleted anything already at that name before starting — which could be your file.
  The name is now unguessable and made fresh for each copy, and nothing is deleted before the copy.
  One consequence: if the machine loses power mid-copy, an inert leftover named after the file with
  `.rnm` and eight characters added may remain, and you can delete it. Renamer will not, because it
  never removes a file it did not create.
- **_Use filename as title when none is set_ now saves the title, and ships off.** When an item has no
  title, Renamer works one out from the filename and now **stores it on the item**. That is what stops
  the name growing: the old behaviour re-read the title out of the filename on every run, so a template
  rendering anything besides `$title` wrapped its own additions around the name again each time
  (`Ann Miller.Delicacy` → `Ann Miller.Ann Miller.Delicacy.Delicacy`). A title you have already set is
  never overwritten. Because it now writes metadata the setting ships off, but a value you have already
  saved is kept exactly as it is — so this changes nothing for an existing install.
- **Two settings retired, one narrowed.** _Default destination_ and _Relocate unmatched items to the
  default destination_ are **gone**; an item matching no rule is renamed where it already sits, exactly
  as before, and to move a group of items you give them a per-studio, per-tag or source-path rule.
  _Allowed roots_ now only narrows **within** your library — every destination is inside it by
  construction — so leaving it empty restricts nothing, and it can no longer send files outside the
  library. Your stored settings still load; the retired values are ignored.
- **The _Duration format_ setting now takes effect.** `$duration` used to render the raw number of
  seconds whatever you picked, producing `My Film [5025]` where the setting's own example column
  promised `My Film [01-23-45]`. **If a template of yours uses `$duration`, your names will change** —
  check a dry run before your next rename. One example was also wrong: `mm-ss` renders the minutes
  within the hour, so 1h 23m 45s is `23-45`, not `83-45`.
- **The dry run explains more before you commit.** A file your exclude rules skip now names that as the
  reason and is counted in the skipped total, instead of appearing flagged with nothing you could act
  on. A cross-drive move whose temporary copy would exceed the path limit is flagged too — such a move
  used to pass the plan and fail part-way through. And two new skip reasons cover a destination root
  that is no longer one of Cove's library paths, and a file that sits outside every library path.
  Worth knowing for the first: **renaming a library path in Cove reads as removing it**, so a rule
  pointed at the old name skips until you re-pick it.
- **Renamer now states that it renames audio, which it has been doing all along.** The manifest and
  docs described video and image only, so the Extensions list understated both what Renamer touches and
  the permissions it asks for — which is what you read before granting it access. It now declares all
  three kinds and all three pairs: `videos.read`/`videos.write`, `images.read`/`images.write` and
  `audios.read`/`audios.write`. Nothing about renaming changed. **Rename selected** and _Auto-rename on
  update_ remain video and image only, so rename audio from the Rename settings page.
- **Smaller fixes.** The tag, performer and studio fields search your library as you type rather than
  listing everything the moment you click in, which is what made them slow on a large library. A
  settings page left open until your access token lapses no longer fails the next save or dry run — the
  request renews and carries on. A destination folder directly inside a drive root such as `D:\Films`
  is accepted when _Allowed roots_ is set, instead of being skipped as blocked. And your last
  whole-library dry-run summary is discarded once on first load, because a status label was misspelled
  `renamer` and is now `rename`; run the scan again to get it back. If you call Renamer's HTTP API
  yourself, that is a breaking change to the `status` field.

## 0.3.0 — Undo that cannot grow without bound

- **Undo no longer grows without limit, and says up front when it won't cover a rename.** The undo
  record kept one line per renamed file, for every rename that had not been undone, in a single Cove
  extension-data value — the same shape that made the dry run break a large library in 0.2.1. It is
  now bounded three ways: it holds only the most recent rename, each line carries only what a restore
  needs, and a rename of more than **5,000 files** is not recorded at all. When a rename is over that
  limit, the confirmation dialog and the dry-run footer both say so **before** it runs, so a rename
  is either fully reversible or plainly not — never half-restorable. In practice a rename you start
  from a list selection stays undoable; a whole-library rename usually does not.
- **Any undo pending from before this upgrade is cleared — once.** An oversized record left by an
  earlier version is discarded the first time this version loads, without Cove having to read it;
  reading it is the failure being fixed. It is a one-time step, not a recurring one: after that, undo
  persists across restarts exactly as before. Nothing else about your library or settings is touched.
- **If a rename can't be undone, re-running is the recovery path.** Change the template, dry-run it,
  and rename again — the names come from your metadata either way. What no re-run can do is restore
  the names your files had before Renamer first touched them; nothing records those. The dry run is
  the check that matters before a first rename, and the docs now say so where they used to point at
  undo.

## 0.2.1

- **The whole-library dry run no longer breaks the settings page — or the server — on a large
  library.** Each scan stored one row per file in a single Cove extension-data value. Because Cove
  serves an extension's stored values as one payload, a library of about a million files grew that
  value past the limit: every read of the Rename settings page failed with a server error, and
  because each attempt had to hold the whole value in memory, repeated attempts could exhaust the
  server's memory and stop Cove altogether. Reinstalling did not clear it, because Cove keeps
  extension data by design. A scan now stores only counts and the move summary, and the dry-run rows
  are computed a page at a time as you scroll. If you are already affected, upgrading fixes it: the
  oversized value is deleted the first time this version loads, with no database surgery needed.
- **Dry-run search now runs on the server.** The search box filters the same three things it always
  did — current path, new path, destination folder — case-insensitively, but the matching now happens
  as pages are fetched instead of over a fully-downloaded table. Status filtering is driven by the
  scan's own counts, so the segment totals are exact and no longer shift as you change the filter.
- **The dry-run table loads as you scroll, and says where it has got to.** Rows arrive a page at a
  time instead of all at once, so the modal opens at any library size. The line under the table
  reports how many rows are loaded and whether more remain; on a large library, a narrow filter makes
  the server search in stages, and it tells you it paused rather than implying there is nothing left
  to find.
- **Dry-run column sorting has been removed.** Sorting by name or destination needs every row at once,
  which is exactly what no longer exists anywhere — and sorting on the server would mean planning your
  whole library to answer one screenful. Rows now appear in scan order (videos, then images, then
  audio, each in library order), which the table states, and the filter and the search cover what the
  sorts were mostly used for. A control that cannot tell the truth is better removed than left looking
  like it works.
- **Moves between two mounts are now recognised as cross-drive on Linux and macOS.** A volume was
  identified by its path root, which is `/` for every path on those systems — so a move from one mount
  to another was treated as a same-drive rename. The file still arrived, but the cross-drive safeguards
  did not run: no free-space check before the batch, no copy verification, and no heavy-batch
  confirmation in the preview. A volume is now identified by its mount point there (drive root on
  Windows, unchanged), so those moves take the verified copy path and report as cross-drive in the
  preview. Renames within one mount are unaffected.

## 0.2.0 — Full-page settings

- **Settings render as a full page.** The Rename settings tab now uses Cove's page-layout settings
  (`SettingsTabLayout.Page`): the extension owns the whole tab canvas — one flat page with
  section-divider headers and the live preview — instead of the host wrapping it in a "Settings
  provided by…" card. Same location (Settings → Extensions → Rename) and same controls; only the
  container changed.
- **Requires Cove `1.0.0`.** Renamer 0.2.0 baselines on the Cove 1.0.0 release, so `minCoveVersion`
  is `1.0.0`. (The full-page settings capability it uses shipped in 0.9.1.)

## 0.1.0 — Initial release

The first release of Renamer — bulk-rename and optionally relocate your Cove media from metadata,
safely and previewably.

- **Naming templates** — token templates for the filename and an optional folder path, with
  optional `{ … }` groups that drop out when their tokens are empty. Multi-value controls for
  performers and tags, character/length safety (including Windows MAX_PATH handling), case
  transforms, and ASCII transliteration.
- **Preview, rename, and undo** — a "Rename selected" bulk action on video and image lists with a
  confirm-before-disk dialog and a progress-reporting background job; a strictly read-only live
  dry-run of the planned old→new changes; and one-click undo of the most recent batch. The undo
  panel refreshes as soon as a rename finishes.
- **Whole-library dry run** — preview every planned change across the library in a sortable,
  searchable table with a live progress bar, an N-of-M count, and an estimated time left. Run the
  rename from the same view once you're happy.
- **Destination routing** — route files to per-studio, per-tag, per-source-path, default, and
  unorganized destinations, including across drives, using a copy → verify → delete move that never
  loses a file. Field rewriting (studio-name squeeze, per-field find/replace, article stripping,
  duplicate-segment collapse) and an opt-in pre-routing exclude system (by tag, studio, or path).
  Cross-volume and same-volume concurrency are tunable from the Advanced settings.
- **Safety** — DB-authoritative rename/move that never orphans a file: collision suffixing, sidecar
  handling, volume-aware undo, and a revert log. A move that can't be reconciled with the database is
  rolled back rather than left half-applied. Each action requires the permission for the entity kind
  it touches (videos, images, or audios), and scan results are scoped to the kinds you can read.
  Every rename, move, undo, and auto-rename is written to Cove's log, with a per-batch summary you
  can audit.
- **A dedicated settings home** — a **Settings → Extensions → Rename** tab with friendly controls
  (dropdowns, toggles, inline token hints with "did you mean" suggestions), a sticky live preview,
  and a sortable, searchable dry-run table. Optional, off-by-default auto-rename on metadata update
  (recorded as its own undoable batch).
- **Verified against Cove 0.8.0** — installs, previews, renames, and undoes correctly on the 0.8.0
  runtime; `minCoveVersion` is `0.7.1`.
