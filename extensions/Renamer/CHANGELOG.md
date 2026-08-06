# Changelog

User-facing changes, newest first.

## 0.4.0 (unreleased) — Renamer needs Cove 1.1.0

<!-- Release step for whoever cuts `renamer/v0.4.0`, before pushing the tag: set
     `src/Renamer/extension.json` `version` to 0.4.0, then PREPEND a row to
     `extensions/com.alextomas955.renamer.json` `versions[]` with version 0.4.0 and minCoveVersion
     1.1.0. The tag push fails in validate if either is missing. Do NOT satisfy the second by
     editing the 0.3.0 row: it describes an immutable artifact that genuinely runs on a 1.0.0 host.
     The full rule is in the repo-wide Releasing guide, under "Raising minCoveVersion". -->

- **Requires Cove `1.1.0`.** Renamer now uses the authenticated fetch Cove hands to extension pages.
  Cove serves that for the first time in the 1.1.0 release; a 1.0.0 host does not serve it at all. So
  `minCoveVersion` is `1.1.0`, and on anything older Renamer does not load — there is no Rename tab
  under Settings → Extensions, and no "Rename selected" action on your video and image lists. Nothing
  degrades; the extension is simply absent until you upgrade Cove. Renamer 0.3.0 stays installable on
  Cove 1.0.0 and keeps working there.
- **The studio names shown in Per-studio destinations load with your session's credentials.** Renamer
  used to look them up with a plain unauthenticated request of its own. That one lookup now goes
  through Cove's request path, which sends your access token — or your share token and password on a
  share link — and retries once when an access token has expired. Every other request Renamer makes —
  the dry run, the rename, undo, and the studio picker itself — is unchanged in this release. Same
  names, same place; that one lookup now behaves like the rest of Cove when your session needs
  proving.

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
