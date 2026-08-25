---
slug: architecture
sidebar_position: 5
---

# Architecture

Rename turns an option change into a file moved on disk and a matching database update. This page
traces that path for a contributor reading the code for the first time.

Rename is a Cove extension in two halves:

- **Backend** — a .NET 10 C# class library (`src/Renamer/`, built to `Renamer.dll`) that implements
  Cove's `IExtension` contract (deriving `FullExtensionBase` from `Cove.Plugins` / `Cove.Sdk`).
- **Frontend** — a React 19 + TypeScript bundle (`src/Renamer.Ui/`, built to `dist/index.mjs`) that
  renders the settings panel, live preview, and bulk-action handler inside Cove's own UI.

## The pipeline at a glance

```text
  ┌──────────┐    ┌──────────────┐    ┌──────────┐    ┌──────────────┐
  │ Options  │ -> │    Engine    │ -> │ Planner  │ -> │  Execution   │
  │ (config) │    │ (render name)│    │ (old→new │    │ (move + DB,  │
  └──────────┘    └──────────────┘    │  + status)│   │  revert log) │
                                       └──────────┘    └──────────────┘
        ^                                                      │
        │                                                      v
  ┌──────────────┐                                      ┌──────────────┐
  │   Frontend   │  <----------- preview / rename ----> │     Api      │
  │ (panel + UI) │              undo / samples          │ (minimal API)│
  └──────────────┘                                      └──────────────┘
```

A **preview** runs Options → Engine → Planner and stops — zero mutation. A **rename** runs the whole
chain through Execution. **Undo** replays the Execution layer's revert journal in reverse.

The revert journal lives in two tables the extension owns, created by a migration the host applies at
load. A row records only what reversal needs — the entity, the file, the path it came from, and what
moved alongside it. The file's current path is not stored, because Cove's database is authoritative
for it. Rows are read a page at a time, so neither writing nor replaying a batch holds all of it in
memory. Two things bound the journal: a batch of more than 5,000 files is not recorded at all, and
the preview says so before the rename runs, so a rename is either fully reversible or plainly not —
never half-restorable; and a recorded batch expires WHOLE after seven days, which is what keeps the
table from growing with how much the library is edited. An installation upgrading from the stored
journal has it moved into the table once, on first load, after which both legacy keys are gone.

## Layer by layer

### Options — `src/Renamer/Options/`

The user's saved configuration: the filename and folder templates, multi-value rules, character and
length safety settings, case transforms, required-field gating, and the auto-rename toggle.

- `RenamerOptions.cs` — the options model and its JSON (de)serialization settings.
- `OptionsStore.cs` — loads and saves options through Cove's per-extension data store, so the
  configuration persists in Cove and survives extension upgrades.

### Engine — `src/Renamer/Engine/`

A pure, side-effect-free renderer: given an item's tokens and the options, it produces the new
filename (and folder). Pure means it can be unit-tested exhaustively and a hostile template can't
escape or touch disk. The render is a small pipeline:

- `Tokenizer.cs` — parses the template into tokens and literal segments, including the optional
  `{}` groups that drop out when their token is empty.
- `MultiValue.cs` — applies the `$performers` / `$tags` rules (separator, max count, sort,
  whitelist/blacklist).
- `ResolutionLabel.cs` — derives the human resolution label (e.g. `1080p`).
- `Sanitizer.cs` — strips/replaces OS-illegal characters and applies the space replacement.
- `TemplateEngine.cs` — orchestrates the render and exposes helpers the preview uses
  (would-sanitize, resolve-one-field, render-with-dropped-fields).
- `LengthReducer.cs` — enforces the max-length cap by dropping fields in priority order, with
  explicit Windows MAX_PATH handling.

### Planner — `src/Renamer/Planner/`

Turns a rendered name into a concrete per-file plan against a real library item, performing **zero**
disk or database mutation.

- `RenamerPlanner.cs` — loads the item (read-only), renders each file's new name, applies the
  path-confinement gate, and classifies every file into a plan item with a status (rename, no-op,
  skip-collision, skip-gated, …). It owns collision suffixing, gating, and multi-file handling on the
  plan side.
- `RenamerPlan.cs` / the plan-item types — the dry-run result the API returns as the old→new diff.
- `IRenamerDataPort.cs` — the abstraction over Cove's entities, so the planner doesn't depend on the
  concrete DbContext or entity types directly (which keeps it testable).
- `MetadataProjector.cs` — projects a Cove media item into the token set the engine consumes.
- `ScanAggregator.cs` — folds a whole-library scan into per-kind counters as it goes, so the job never
  holds a per-file list and what it stores is a fixed size. The one part that would otherwise grow with
  the library's shape — the itemised list of volume pairs a move spans — is capped; the cross-volume
  count and byte total it summarises stay exact, and the stored value says when the list was topped.
- `ScanRowPager.cs` / `ScanBucket.cs` — the dry run's rows are not stored at all. A page is planned on
  demand through the same `PlanLoadedEntity` the scan job uses, walking the same ascending `(kind,
entity id)` order, so a page computes exactly what the full pass would have. It is the purity of
  planning — one item's plan depends on no other item in the run — that makes this equivalence hold
  rather than a second code path written to agree. A request also has a ceiling on how many items it
  will examine, so a narrow filter cannot turn one page into a full-library pass; when it stops there,
  it says so, because "I stopped looking" and "there is nothing" are different answers.

### Execution — `src/Renamer/Execution/`

The only layer that mutates anything. It moves the file and updates Cove's database **together**, so
the two never drift.

- `RenamerExecutor.cs` — runs a plan: for each file, move on disk, update the Cove record, and record
  the change in the revert journal. Move-first-then-DB with rollback so a failure leaves the file and
  the database consistent.
- `DiskMover.cs` — the actual filesystem move, including sidecar files (captions/subtitles sharing
  the stem) and collision-safe behavior.
- `CoveRenamerDataPort.cs` — the concrete `IRenamerDataPort` backed by Cove's DbContext.
- `Planner/IRevertJournal.cs` — the undo seam: the only surface between the rename and undo paths
  and where the journal is stored. A row exists exactly while its file still needs restoring, so what
  remains in the journal IS the work left.
- `CoveRevertJournal.cs` — the journal over two tables the extension owns (`renamer_revert_batches`,
  `renamer_revert_rows`), created by the migration in `RevertJournalStorage.cs` and applied by the
  host. Rows are read a page at a time through a keyset cursor, so an undo restores a batch without
  holding it in memory. A batch expires whole after a fixed retention window, and a batch offered
  over the row cap is refused outright rather than recorded in part.
- `RevertDelta.cs` — the sidecar and caption moves that rode along with one renamed file, recorded in
  the forward direction so undo replays what happened rather than recomputing a target from the names.
- `UndoStopReason.cs` — why one entry stopped short, as a value; exactly one reason is terminal, so
  every other leaves its row pending for a later retry.
- `RevertLog.cs` — the legacy stored journal, kept only as a one-way migration source.
- `JournalBlobMigration.cs` — moves an upgrading installation's stored journal into the table exactly
  once, then deletes both legacy keys.
- `UndoReplayer.cs` — reverse-replays one page of a batch, reading each file's current location from
  the database rather than from the journal.

### Api — `src/Renamer/Renamer.Api.cs` (+ `src/Renamer/Api/`)

Minimal-API endpoints the frontend calls, mounted under
`/api/extensions/com.alextomas955.renamer`:

- `POST /preview` — runs the planner over selected item IDs and returns the old→new plan (no
  mutation).
- `POST /rename` — enqueues the background rename job for selected items.
- `POST /preview-sample` — renders the engine over fixed sample data with the in-flight options;
  powers the live preview without touching the database or disk.
- `POST /undo` — reverse-replays the last batch.
- `GET /last-batch` — a paths-free summary of the most recent batch for the undo panel.
- `POST /scan-library` — enqueues the whole-library dry run.
- `GET /last-scan` — the last dry run's summary: per-status counts and the move summary, merged down
  to the kinds the caller may read.
- `POST /scan-rows` — one page of that dry run's rows, planned on demand, with an optional path search
  and status-bucket filter.

Every endpoint re-checks the caller's permission **in the handler** (`videos.read` to preview,
`videos.write` to rename or undo) — Cove's attribute-based permission filter is inert on minimal-API
routes, so the check is explicit and runs before any work.

The bulk-action registration, the job definition, and the optional auto-rename event hook live
alongside in `src/Renamer/Renamer.cs` (shared batch core) and `src/Renamer/Renamer.Events.cs`
(`video.updated` / `image.updated` auto-rename, opt-in and re-entrancy-guarded), with the
background job runner in `src/Renamer/Jobs/`.

### Frontend — `src/Renamer.Ui/src/`

A Vite library build that Cove loads as `index.mjs`. Its home is a dedicated **Settings → Extensions
→ Rename** tab; it also registers the "Rename selected" bulk action on video and image lists.

- `index.ts` — the bundle entry that registers the components and the bulk-action handler.
- `RenamePage.tsx` / `RenameSettingsPanel.tsx` — the settings tab and its body (the controls + the
  debounced live preview that calls `/preview-sample`).
- `DryRunModal.tsx` — the full-screen dry-run modal: scans the whole library, reads the scan's
  summary for its counts, walks its rows a page at a time through `useScanRows.ts` /
  `scanRowsStore.ts`, and runs `/rename` after confirmation. `Dialog.tsx` is the shared modal shell it
  and the undo-confirm dialog use.

  The table has no column sorts. A sort needs the whole result set, and the whole result set is
  exactly what neither the store nor the browser holds any more; moving the sort to the server would
  not help, because rows do not exist until they are planned, so ordering by new name or destination
  would mean planning the entire library to answer one page. The one order that is free is the
  cursor's own — kind, then entity id — so that is the order rows are served in, and the table states
  it rather than offering a header that could not act. The two controls that _can_ be answered cheaply
  stay: the status filter, answered by the summary's counts, and the path search, answered by the page
  query using the same match rule the browser used to apply.

- `renameSelected.ts` — the bulk-action handler: preview → confirm → `/rename`, cancellable.
- `UndoSection.tsx` — the undo control backed by `/undo` and `/last-batch`.
- `EntitySelectField.tsx` / `StudioMap.tsx`: the adapter over Cove's own entity selector (every
  studio/tag/performer field in the panel goes through it, with the create affordance off) and the
  per-studio destination-map editor. A rule stores the entity's stable id, and the host resolves that
  id to a name for display: one cached lookup per configured rule, never a list sized by the library.
- `PreviewCard.tsx`, `WarningBadge.tsx`, `TokenLegend.tsx`, `templateValidation.ts`, `presets.ts`,
  `options.ts`, `preview.ts`, `primitives.tsx` — supporting UI, types, and the inline token
  validation. The `*Logic.ts` files hold the pure logic split out of their `.tsx` components.

## Safety invariants

These are the guarantees the design exists to protect. Preserve them when you change code.

- **DB-authoritative move.** A file is never moved on disk without its Cove record being updated in
  the same operation. The database stays the source of truth; nothing is orphaned.
- **Rollback on failure.** The executor moves first, then updates the database, and rolls back the
  move if the database update fails — so a partial failure never leaves the two inconsistent.
- **Never overwrite.** A rename never clobbers an existing target; the planner suffixes to avoid
  collisions, and gives up cleanly (skip-collision) rather than overwrite.
- **Never force a lock.** If another process holds a file, the rename skips and reports it — it never
  force-kills the locking process.
- **A move between volumes is copied, verified, then deleted.** There is no atomic rename across
  volumes, so a cross-volume move copies the file, verifies the copy (size plus an XxHash3 content
  hash), and only then removes the source. This is also what arms the free-space check and the
  heavy-batch confirmation, so which moves count as cross-volume decides whether those run at all.
  A volume is identified by its drive root on Windows and by its **mount point** on Linux and macOS —
  two different mounts are two volumes even though they share the `/` root.
- **Preview before disk.** Every rename is previewable as an old→new diff first, and the preview path
  performs zero mutation.
- **Nothing scales with the library.** Libraries reach millions of files, so no stored value, no
  in-memory accumulator and no response may hold one entry per file. The whole-library dry run stores
  only counts and the move summary — a size fixed by the number of media kinds and statuses — and
  serves its rows a page at a time. Capping the rows instead would be worse than the failure it
  replaces: a bricked settings page is visible, a silently shortened dry run is not. Paging is safe
  here because planning is pure per item: a page computes exactly what a full pass would have, which is
  asserted by comparing a paged walk against a single full plan at several page sizes.
- **Options persist and survive upgrades.** Configuration lives in Cove's per-extension store, not in
  a local file.
- **No host assemblies shipped.** The extension must never bundle host-provided assemblies
  (`Cove.Core` / `Cove.Plugins` / `Cove.Sdk`, EF Core, Npgsql, …). They're stripped from the publish
  set, and the packer copies only the names the catalog entry's `artifacts` array declares, so an
  assembly that is not declared cannot reach a package however the build emits it. Bundling them
  would cause load-context type-identity mismatches at runtime.
- **ABI-matched local-source build.** When building against a local Cove checkout, the extension
  references the host's own Cove projects so it's binary-compatible with the running host. This is the
  path the deploy script uses.

## Where to start reading

- To understand a rename end to end: `RenamerPlanner.cs` then `RenamerExecutor.cs`.
- To understand the preview: `TemplateEngine.cs` and `Renamer.Api.cs`'s `PreviewSampleAsync`.
- To understand the UI: `RenameSettingsPanel.tsx` and `renameSelected.ts`.
