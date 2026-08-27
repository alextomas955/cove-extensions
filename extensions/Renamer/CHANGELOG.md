# Changelog

User-facing changes, newest first.

## 0.4.0 — Undo you can retry, and one that survives the next rename

**Needs Cove 1.3.0.** An older host does not load Renamer at all — no Rename tab under Settings →
Extensions, no "Rename selected" on your lists — so stay on 0.3.0 until you have upgraded Cove.
Nothing here is a feature you lose. The floor rose because Renamer now relies on the host publishing
entity events for bulk mutations; without those, editing several items at once renames one of them
and says nothing about the rest.

- **A rename no longer lengthens the filename every time it runs.** With _Use filename as title when
  none is set_ on, an item with no title borrowed one from its own filename — the name the previous
  run wrote — so any template holding more than `$title` added its decorations again on each pass:
  `raw clip.mkv` became `2021-03-14 - raw clip [4k].mkv`, then
  `2021-03-14 - 2021-03-14 - raw clip [4k].mkv`, and on until the path was too long to write. The
  derived title is now saved onto the item in the same save as the rename, so a second run finds a real
  title and changes nothing. A title you typed yourself is never overwritten, and nothing is written
  when the setting is off. With _Auto-rename on update_ also on, an item holding more than one file
  could keep re-triggering itself after a single edit; that stops too.
- **The duration format setting now works.** `$duration` renders through whatever you pick under Token
  settings, so a template using it produces the name the live preview showed. Before, the setting was
  offered and ignored: every file got its duration as a raw number of seconds whichever format was
  chosen. A format .NET rejects falls back to those raw seconds rather than failing the rename.
- **A destination is now a library path you pick, plus the folders made under it.** Every
  destination - the default _Where files go_, the unorganized route, and each per-studio, per-tag and
  source-path rule - has the same two parts: **Under**, chosen from the library paths you configured
  in Cove, and a **folder template** rendered beneath it. You no longer type a path anywhere, so
  moving a folder in Cove no longer leaves a rule pointing at nothing.
- **A folder template no longer buries files one level deeper on every run.** A rule with a folder
  template but no destination used to measure from the file's own parent folder, which is the
  previous run's output, so each pass appended the rendered folder again until the path got too long
  to write. It now measures from the Cove library path that holds the file, so a second run over the
  same library changes nothing.
- **A rule that cannot be honoured says so instead of guessing.** The dry run and the log now name
  four reasons an item was left alone: its destination measures from a library path and the file is
  under none; the root the rule names is no longer one of Cove's library paths; the destination falls
  outside the folders _Allowed roots_ permits; or the resulting path is longer than the path limit.
  None of them fall back to another destination, so a broken rule never relocates files somewhere you
  did not choose.
- **Your existing destination rules are converted once, on the first load after this upgrade.** Each
  stored path is split into the library path containing it plus the rest as a folder template, which
  names the same folder - so nothing moves on the first run afterwards. A rule whose path lies under
  no Cove library path is dropped and named in the log; its items follow the default destination from
  then on. The conversion needs at least one library path configured in Cove and waits until there is
  one. Until it has run, the settings page says so and turns **Save** off: your stored folders show as
  blank there, so a save from it would have replaced them with nothing and left no copy anywhere.
- **The _Relocate unmatched items_ switch is gone, and the default destination replaces it.** An item
  matching no rule now takes _Where files go_, the same field the live preview has always shown. It
  ships naming no root and no folder, which renames in place and moves nothing.
- **_Allowed roots_ is now only a narrowing.** Every destination is a Cove library path plus a
  relative template, so a rename is inside the library by construction; the list can still restrict
  it to a smaller subtree, and an empty list restricts nothing.
- **A cross-drive move that cannot fit is warned about before it runs, not after it fails.**
  Renaming onto another drive copies to a temporary name beside the destination first, and that
  temporary path is longer than the final one. The dry run only ever measured the final path, so a
  destination just inside _Full-path max length_ previewed as a clean move and then failed at the
  filesystem. Such a row now carries a red **Too long to copy across drives** badge, and the confirm
  before a rename says how many files are affected and what to shorten.
- **Numbering a name to avoid a clash can no longer push the path over the length limit.** _Full-path
  max length_ was measured against the name your template produced, and the number appended to free a
  name already taken was added afterwards — so a file sitting just inside the limit was previewed as a
  clean rename and then written, or attempted, at a path past it. The length is now measured again once
  the final numbered name is settled, both when the dry run plans it and again at the moment of the
  rename, where a name already taken since the dry run can add a longer number. Such a file is reported
  as **Skipped — path too long** and left exactly where it is.
- **A rename no longer erases the undo of the one before it.** The undo record moved out of Cove's
  extension-data store and into a table Renamer owns, so a background auto-rename no longer destroys
  the record of a deliberate 500-file run. Each rename is kept for 7 days and then expires. The
  5,000-file limit is unchanged: a rename past it is still not recorded, and the confirmation dialog
  and the dry-run footer still say so before it runs.
- **A file that can't go back stays pending instead of spending the whole undo.** Undoing used to
  mark a rename spent as soon as one file came back, so files blocked by an occupied name or an
  unmounted drive could never be retried. Now each file is retired only when it is actually settled;
  fix the cause and undo again, and it finishes exactly the work that is left.
- **Subtitles and other companion files come back with their video.** An undo replays the companion
  moves the rename actually made, and writes each caption's original filename back only when its file
  really moved back. A companion that cannot be restored no longer blocks its video's undo — the
  video is restored and the companion is reported.
- **A companion file's stored name is now really written on rename.** The caption rename in the
  forward direction silently did nothing outside tests, leaving Cove's record naming a file the move
  had just taken away.
- **An undo pending from an earlier version is carried over, not thrown away.** The first load after
  this upgrade moves it into the new table and removes the two old keys. It runs once.
- **Undoing a very large rename no longer risks the settings page.** The undo response listed every
  file that could not go back, so a big undo answered with a list as long as the problem. It now
  reports how many hit each kind of problem plus a short example of each, and the full detail goes to
  Cove's log where it belongs. The message you see counts every affected file, not just the examples.
- **The undo panel can say how much of a rename is left.** The last-rename line now carries how many
  files are still restorable and how many can never go back, so a partly-undone rename describes
  itself instead of looking finished. The confirmation and the button quote the number of files still
  to move rather than the size the rename started at.
- **The undo panel shows the date its window closes, and stops offering an undo it cannot promise.**
  The last-rename line ends with that rename's own expiry date instead of a static "kept for 7 days"
  note. Past it the line reads "undo expired" and the button is withheld: the files may still be where
  the rename left them, but the next rename drops the record with no further warning.
- **A whole-library rename now says how far one undo reaches.** A run across several media kinds
  records each kind separately and a single undo replays one record, so the success banner closes with
  "Undo covers only the last media kind in this run." rather than leaving that to be discovered.
- **Renaming into a folder that does not exist yet, on a drive you allowed, now works.** The safety
  check that keeps a move inside your allowed folders was refusing every destination whose nearest
  existing parent was a drive root - which is the ordinary case of allowing a whole drive and letting
  Renamer create the folder. Nothing was lost or moved wrongly; the rename was simply refused.
- **A cross-drive copy can no longer collide with another copy of the same file.** The temporary name
  a copy writes under is now unique per attempt, so two runs touching the same destination cannot
  overwrite each other's in-progress file.
- **A skipped file says which kind of problem it hit.** "Locked or already there" is now two separate
  reasons, and a permission refusal, a copy that read back wrong, and a cancelled run each carry their
  own reason instead of being grouped with ordinary skips. Every reason a dry run can produce now has
  a badge of its own in the table, so no row lands in _Needs attention_ without saying why.
- **Auto-rename can no longer chase its own tail.** Renaming a file makes Cove announce that the item
  changed, which is what wakes auto-rename in the first place - so auto-rename was hearing its own
  work. It stopped only when a second pass found nothing left to do, and two destination rules that
  send a file back and forth never reach that point. Auto-rename now ignores the announcement its own
  rename caused, so one edit means one rename whatever your rules say. A later edit of the same item
  is still picked up.
- **Renamer's entry in Cove's extension list now shows the right link and the full description.** The
  link pointed at a repository that does not hold this extension, and the description was a single
  sentence that left out which permissions a rename needs and the fact that Renamer makes no network
  calls. Both were duplicated in code, where they silently won over the manifest that had the correct
  text; the manifest is now the only place either is written.
- **A tag or performer rule now follows a rename.** Tag and performer rules were remembered by name,
  so renaming one in Cove quietly stopped its rule from matching, and two spellings of one name routed
  to two separate destination folders. Every rule now remembers the tag or performer itself: rename it
  in Cove and the rule keeps applying, while the rendered filename picks up the new name.
- **Your existing tag and performer rules are converted once, on the first load after this upgrade.**
  A rule naming a tag or performer that no longer exists in your library is dropped, and each dropped
  rule is named in the log so you can see what went. Two spellings of one name collapse into a single
  rule. Studio rules and path rules are untouched. Until that conversion has run, the settings page
  says so and turns **Save** off: it cannot show rules stored by name, so a save from it would have
  replaced them with nothing and left no copy anywhere. Restart Cove and reload the page.
- **Picking a studio, tag or performer now uses Cove's own picker.** It searches your library as you
  type instead of loading every tag and performer in the library the moment you open the field, so the
  settings page stays fast on a large library. It also will not create a new tag or performer from the
  settings page - add it in Cove first, then pick it here.
- **A rename or dry run that stops responding now ends with a message instead of waiting forever.** If
  the job stops reporting progress, or Cove stops answering about it at all, Renamer stops waiting and
  tells you, rather than leaving the button disabled and asking about the job once a second for as long
  as the page stays open. A rename that ends this way says the library may already have changed,
  because it may have - the job can still be running. A job that keeps reporting is never given up on,
  however long it takes.
- **Renamer now states that it renames audio, which it has been doing all along.** The manifest
  described video and image only, so Cove's extension list understated both what Renamer touches and
  the permissions it asks for — which is what you read before granting it access. It now declares all
  three kinds and all three pairs: `videos.read`/`videos.write`, `images.read`/`images.write` and
  `audios.read`/`audios.write`. Nothing about renaming changed. **Rename selected** and _Auto-rename on
  update_ remain video and image only, so rename audio from the Rename settings page.
- **A rename that had nothing to do no longer repeats itself on Windows.** A file already sitting at
  its computed destination compared unequal to its own path, because Cove supplies a folder path with
  the platform's own separator while Renamer's target came back forward-slashed. So the file was planned
  as a move to where it already was, and with _Auto-rename on update_ on, each pass raised the event
  that started the next. Both paths are now normalized before the comparison, and a file at its
  destination is reported as needing no change.
- **A settings page left open no longer breaks once your session refreshes.** The panel's calls went
  out with no credential attached, and two of the three routes it used to read and write your settings
  were not routes Cove serves at all. Every call now goes through the host's authenticated fetch and the
  routes Cove actually exposes, so a save or a dry run started after your access token lapses succeeds
  instead of failing.
- **The Rename settings page recovers instead of coming up blank.** Two host behaviours could leave it
  unreachable: a failed fetch of the settings chunk painted an error on the right address, and a page
  opened before Cove finished loading extensions was switched to a built-in tab with the address
  rewritten. The panel now retries through both cases within one budget rather than waiting forever.
- **The dry run no longer stops part-way through a large library.** The server reads your library in
  stages, and a stage can go by holding nothing that matches the filter you picked. Such a stage
  changed nothing on screen, and the table only asked for another one when the row count moved — so a
  narrow filter came up a handful of rows in, said the server had paused, and told you to scroll a
  table with nothing left to scroll. The table now follows the search through empty stages by itself,
  and the line under it reports how many items have been checked so far rather than asking you to
  keep going.
- **The live preview no longer shows the result for settings you have moved on from.** Each edit sends
  a fresh preview, and a slow one issued earlier could answer after a later one and repaint the pane
  over it — so the sample names under your template could be the names some earlier version of it
  produced. A superseded answer is now discarded, and the request it replaces is cancelled.
- **The dry run's warning badges are visible again on a released Cove.** The amber and red pills asked
  for two Tailwind utilities that Cove's prebuilt stylesheet does not contain, so they rendered with no
  fill at all and no error anywhere. They now carry their fill directly, built from Cove's own colour
  variables so they still follow your theme.
- **A sidecar listed in a different case than the file on disk now moves with it.** _Also move sidecar
  files with these extensions_ documented a case-insensitive match, but the lookup only behaved that
  way where the filesystem did — so `SRT` next to a `clip.srt` worked on Windows and silently did
  nothing on a case-sensitive volume, which is what Cove's own container runs on. The comparison is now
  done in Renamer, and a moved sidecar keeps the extension casing it had on disk.
- **An undo that did not happen no longer reports success.** When the undo request failed outright —
  Cove restarting, the network dropping — the panel still showed _Undone — your files were moved back to
  their original names_. It now reports the failure and leaves the undo available to retry.
- **The rename confirm counts every kind of skip.** The dialog you approve a rename from counted only
  some of the reasons an item is left alone, so a selection where every item was excluded by a rule
  showed no skip line at all and read as though everything would be renamed. Every reason is counted
  now, and the box says which.
- **A cross-drive move states a size you can read.** The line naming how much is about to move across
  drives was always written in gigabytes, so anything under roughly 50 MB read as _0.0 GB_ — nothing,
  in the one dialog whose job is to say how much. Each size now uses a unit that fits it.
- **A failed preview stops pretending it is still loading.** When the live preview could not be built,
  the pane showed the error and a _Rendering preview…_ spinner underneath it, forever.
- **Three smaller corrections in the settings panel.** The `mm-ss` duration example showed `83-45` for
  a 1h 23m 45s file where the format produces `23-45`; a destination hint told you to pick a library
  root "beside it" on a Cove with no library paths configured, where no picker is drawn; and the dot
  marking a failed save drew no colour at all, because the shade it asked for is not in Cove's
  stylesheet.
- **A skipped rename now says why it was skipped.** A move that did not happen was reported as
  _Skipped — file in use_ whatever stopped it, so a permission Cove does not have, a copy that failed
  its content check, and a shutdown part-way through a large move all read as a locked file — and the
  advice that follows from each is different. Each now reports its own reason: _Skipped — permission
  denied_, _Skipped — copy did not verify_, and _Skipped — cancelled_.
- **With _Auto-rename on update_ on, an edit is no longer swallowed after a rename that could not
  run.** When every file of an edited item was skipped — a name already taken at the destination, say —
  the hook stayed muted for that item, so your next edit of it was ignored and nothing said so. It
  now acts on the next edit.
- **A dry run or library rename now finishes for an account that is not the owner.** Renamer watched a
  run through one of Cove's own endpoints, and from Cove 1.3.1 that endpoint answers only to accounts
  with unrestricted read. So for anyone else the run started and then appeared to stall, ending in
  _"Cove stopped answering when asked about this job"_ — the work usually finished, but the panel could
  not see it. Renamer now reports its own progress, and only for its own runs.

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
