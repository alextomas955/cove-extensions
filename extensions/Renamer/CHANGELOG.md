# Changelog

User-facing changes, newest first.

## 0.4.0 (unreleased) — Renamer needs Cove 1.1.0

<!-- Release step for whoever cuts `renamer/v0.4.0`, before pushing the tag: set
     `src/Renamer/extension.json` `version` to 0.4.0, then PREPEND a row to
     `extensions/com.alextomas955.renamer.json` `versions[]` with version 0.4.0 and minCoveVersion
     1.1.0. The tag push fails in validate if either is missing. Do NOT satisfy the second by
     editing the 0.3.0 row: it describes an immutable artifact that genuinely runs on a 1.0.0 host.
     The full rule is in the repo-wide Releasing guide, under "Raising minCoveVersion". -->

- **Your last whole-library dry run is discarded once, on the first load of this version.** Renamer
  labels each planned file with a status, and the label for "renamed where it sits" was misspelled
  `renamer`; it is now `rename`, and the never-used `gallery` file kind is gone with it. A dry-run
  summary stored by an earlier version spells the old label, so Renamer reads it as "no scan yet"
  rather than showing you figures it cannot interpret. **Nothing else is affected** — no setting, no
  template and no filename changes, and your undo history is untouched. Run the dry run again to get
  the summary back. If you call Renamer's HTTP API yourself, this is a breaking change to the `status`
  field: match `rename` instead of `renamer`.
- **_Default destination_ and _Relocate unmatched items to the default destination_ are gone.** The
  relocate switch shipped off and was never going to be turned on — it moved every item that matched no
  rule, with whole-library reach and no way to undo a move across drives — and the default destination it
  fed did nothing on its own. An item matching no rule is renamed where it already sits, exactly as
  before. **Nothing moves differently:** to move a group of items, give them a per-studio, per-tag or
  source-path rule, which is what already did the work. Your stored settings still load; the two retired
  values are ignored.
- **The _Duration format_ setting now takes effect.** `$duration` used to render the raw number of
  seconds whatever you picked, so a template using it produced `My Film [5025]` where the setting's own
  example column promised `My Film [01-23-45]`. It now renders in the format you chose, so the dry run,
  the preview samples and the finished filename all agree. **If a template of yours uses `$duration`,
  your names will change** — check a dry run before your next rename. One example was also wrong and is
  corrected: `mm-ss` renders the minutes within the hour, so 1h 23m 45s is `23-45`, not `83-45`. A format
  the .NET duration formatter rejects no longer stops the whole run; that file's `$duration` falls back
  to the seconds, and every other file renames normally.
- **A file your exclude rules skip now says so in the dry run, and is counted with the other skips.**
  Such a file used to appear flagged in the dry-run table with nothing naming the reason, so the row read
  as a problem you could not act on; and it was left out of the skipped total the rename confirmation
  shows, so that total read lower than the number of files Renamer would actually leave alone. Both now
  cover it. **No renaming behaviour changed** — the same files are excluded as before, and no template
  and no setting moved.
- **A rename whose copy will not fit across drives is now flagged before you approve it.** A move to
  another drive copies the file to a temporary name beside its destination and then promotes it, and that
  temporary name is longer than the final one. A destination path close to the length limit therefore
  passes the plan and still fails part-way through the move. Renamer now counts those files in the rename
  confirmation and marks each one in the dry run, a dry run over your whole library included, so you see
  them before anything runs. **Your filenames are unchanged.** Shortening them to fit would change the
  result for every file near the limit, including the ones that were never at risk. Where you see the
  warning, shorten the destination folder or the name yourself, or keep the move on one drive.
- **Renamer says that it renames audio files, which it has been doing all along.** The manifest and the
  docs described video and image only, so the Extensions list understated what Renamer touches — and
  understated the permissions it asks for, which is what you read before granting it access. It now
  states all three kinds and all three permission pairs: `videos.read`/`videos.write`,
  `images.read`/`images.write` and `audios.read`/`audios.write`. Nothing about renaming changed. Worth
  knowing where the reach is genuinely narrower: **Rename selected** is on video and image lists only,
  and _Auto-rename on update_ covers those two kinds as well, so rename audio from the Rename settings
  page or a whole-library run.
- **Requires Cove `1.1.0`.** Renamer now uses the authenticated fetch Cove hands to extension pages.
  Cove serves that for the first time in the 1.1.0 release; a 1.0.0 host does not serve it at all. So
  `minCoveVersion` is `1.1.0`, and on anything older Renamer does not load — there is no Rename tab
  under Settings → Extensions, and no "Rename selected" action on your video and image lists. Nothing
  degrades; the extension is simply absent until you upgrade Cove. Renamer 0.3.0 stays installable on
  Cove 1.0.0 and keeps working there.
- **Renamer's requests now carry your session the way the rest of Cove does.** Renamer used to make
  them with a plain request of its own; in a signed-in browser Cove accepted those anyway, on your
  session cookie. They now go through the request path Cove hands to extension pages, which sends your
  access token — or your share token and password on a share link — and retries once when an access
  token has expired. What changes for you: a settings page left open long enough for your access token
  to lapse no longer fails the next save or dry run; it renews and carries on.
- **Undo now brings back subtitles and captions, not just the media file.** A rename moves a
  same-name neighbour — a `.srt` subtitle, say — along with the video, and rewrites the caption
  filenames Cove stores for it. Undo used to move only the video back, leaving both under the names
  the rename gave them. They now come back with it, to their original names and locations. Worth
  knowing if you relied on the old behaviour: an undo you ran before this upgrade left those
  companion files behind, and they are still where that rename put them. Where one cannot come back
  — something already sits in its old slot — the media file is restored anyway and the message names
  the file that stayed behind, because clearing that slot is yours to do.
- **Undo is kept for seven days, and several renames can be waiting at once.** Only the most recent
  rename used to be kept, so starting another one discarded it. The case that bit hardest was silent:
  with _Auto-rename on update_ turned on, one background rename of a single edited item threw away
  the undo of the deliberate rename you had run minutes earlier, and nothing said so. Each rename now
  keeps its own record for seven days, and the undo panel shows the date the one it is offering stops
  being available. A rename expires as a whole — when its seven days are up, everything it still
  holds goes with it, including any part you had not restored yet.
- **No rename is too large to undo.** A rename of more than 5,000 files was not recorded at all,
  which put a whole-library rename beyond undo. That ceiling is gone, and so are the warnings that
  announced it in the rename confirmation and the dry-run footer.
- **An undo that stopped part-way can now be retried.** A locked file, an unmounted drive or
  something already sitting in a file's old slot stops that file coming back. Undo restored the rest
  and then spent the whole record, so the files it had not reached were beyond recovery. It now
  restores what it can and leaves the rest waiting: clear the cause, press Undo again, and the second
  run acts only on what is left. The panel states where it got to and until when, in the shape
  _12 of 500 restored · 488 remaining · undo available until August 18, 2026_. One case is genuinely
  final rather than worth retrying: a file that is no longer in your library cannot be restored, and
  those are counted apart.
- **A rename you only partly undid no longer vanishes from the panel.** Anything that started a newer
  rename — including a single background _Auto-rename on update_ edit — took the panel over, and once
  that newer one had been put back the panel read _No rename to undo._ while your own files were still
  waiting. They were never lost, but there was no way to reach them before their seven days ran out.
  The panel and the button now reach the most recent rename that still has files to put back, so your
  remainder comes back to the panel and **Undo last rename** acts on it. When two renames are waiting,
  the newer one is offered first.
- **An undo left pending from before this upgrade is carried over — once.** Whatever your previous
  version still had waiting is moved into the new record the first time this version loads, keeping
  its original date so it keeps its real age, and the old record is then cleared. It is a one-time
  step, not a recurring one, and nothing pending is lost. Undo also now survives an update or a
  reinstall of Renamer, because the record lives in Cove's database rather than in the extension's
  own folder.
- **A destination folder directly inside a drive or volume root now works.** On Windows, with
  _Allowed roots_ set, a destination such as `D:\Films` was refused whenever the folder did not exist
  yet — the item was skipped as blocked, with no explanation you could act on. It is accepted now,
  for both a rename and an undo.
- **A cross-drive move can no longer delete a file of your own.** While copying a file to another
  drive, Renamer wrote it under a fixed working name first — and deleted anything already at that
  name before starting, which could be your file. The working name is now unguessable and made fresh
  for each copy, and nothing is deleted before the copy. One consequence to know: if the machine
  loses power mid-copy, a stray file may be left in the destination folder, named after the file
  being moved with `.rnm` and eight characters added. It is inert and you can delete it; Renamer will
  not, because it never removes a file it did not create.
- **Tag and performer rules now follow the item, not its name.** Whitelists, blacklists, _Exclude by
  tag_ and _Per-tag destinations_ used to be stored as the tag's or performer's **name**, so renaming
  one in Cove quietly broke every rule pointing at it. They now store Cove's own stable id for that
  item, and a rename no longer breaks anything. **Your existing settings convert automatically** the
  first time the Rename settings page loads after this upgrade — there is nothing to re-enter. Three
  consequences are worth knowing before you upgrade. A stored name that matches no tag or performer in
  your library is **dropped**, because it could never have matched anything. Where two of your tags or
  performers differ only in capitalisation, a rule that used to match both now matches one of them,
  since both names resolve to a single item. And if a tag or performer is later deleted from your
  library, a rule still targeting it shows a **loading placeholder** in place of the name until you
  remove that entry.
- **The tag, performer and studio fields now search your library as you type.** They used to list
  everything the moment you clicked into them, which is what made them slow on a large library. Type
  at least one character to see matches; an empty field shows nothing.
- **A failed search now reads the same as a search with no matches.** Both show _No tags found_. If a
  value you know exists does not appear, change the search text and try again — there is no separate
  message to tell a lookup that failed from a lookup that genuinely found nothing.

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
