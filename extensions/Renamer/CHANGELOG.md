# Changelog

User-facing changes, newest first.

## Unreleased

- **Scan results respect your permissions** — reading back a whole-library scan now returns only the
  media kinds you can read, so a scan run by a higher-permission account never exposes other kinds'
  file paths to a narrower account.
- **Auto-rename is now undoable** — an auto-rename triggered by a metadata update is recorded as its
  own batch, so the Undo button reliably reverses it. Previously an auto-rename could leave an
  unrecoverable or misread undo record.
- **The Undo panel refreshes after a rename** — running a rename from the settings panel or the Dry
  Run modal now immediately updates the Undo section to show the batch you just created, instead of
  showing stale state until you reload.
- **Safer failure handling on a rename** — in the rare case where a file is moved on disk and saved
  to the database but the two disagree afterward, the move is now rolled back and the mismatch is
  reported, rather than leaving the file moved with no undo record.
- **Live progress in the Dry Run modal** — the library scan now shows a real progress bar, an
  N-of-M count, and an estimated time left instead of just a spinner (and reads "Finalizing…" at the
  end rather than sitting at 99%). Starting a rename from the dry-run footer shows the same bar plus
  the current phase while it runs.
- **Cross-drive concurrency in the settings panel** — the cross-volume and same-volume concurrency
  knobs are now editable Advanced settings (clamped to 1–16) instead of stored-only options.
- **Faster planning on large libraries** — a whole-library dry run/scan now loads each item once
  instead of twice, roughly halving the database round-trips in the planning pass.

## 0.1.0 — Initial release

The first release of Renamer — bulk-rename and optionally relocate your Cove media from metadata,
safely and previewably.

- **Naming templates** — token templates for the filename and an optional folder path, with
  optional `{ … }` groups that drop out when their tokens are empty. Multi-value controls for
  performers and tags, character/length safety (including Windows MAX_PATH handling), case
  transforms, and ASCII transliteration.
- **Preview, rename, and undo** — a "Rename selected" bulk action on video and image lists with a
  confirm-before-disk dialog and a progress-reporting background job; a strictly read-only live
  dry-run of the planned old→new changes; and one-click undo of the most recent batch.
- **Destination routing** — route files to per-studio, per-tag, per-source-path, default, and
  unorganized destinations, including across drives, using a copy → verify → delete move that never
  loses a file. Field rewriting (studio-name squeeze, per-field find/replace, article stripping,
  duplicate-segment collapse) and an opt-in pre-routing exclude system (by tag, studio, or path).
- **Safety** — DB-authoritative rename/move that never orphans a file: collision suffixing, sidecar
  handling, volume-aware undo, and a revert log. Each action requires the permission for the entity
  kind it touches (videos, images, or audios). Every rename, move, undo, and auto-rename is written
  to Cove's log, with a per-batch summary you can audit.
- **A dedicated settings home** — a **Settings → Extensions → Rename** tab with friendly controls
  (dropdowns, toggles, inline token hints with "did you mean" suggestions), a sticky live preview,
  and a sortable, searchable dry-run table. Optional, off-by-default auto-rename on metadata update.
- **Verified against Cove 0.8.0** — installs, previews, renames, and undoes correctly on the 0.8.0
  runtime; `minCoveVersion` is `0.7.1`.
