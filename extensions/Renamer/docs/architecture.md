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

Two kinds of persisted state, and the boundary between them is load-bearing. The host's per-extension
key/value store holds what is bounded by configuration — the options, and the last scan's summary. The
undo journal is deliberately **not** there: it is the two extension-owned database tables described
under Execution below. A journal grows with the library, and a growing value under one store key put
every writer into a read-modify-write race and once grew large enough to fail this extension's whole
settings page, survive a reinstall, and need SQL to remove. A row insert has neither problem.

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
- `MetadataProjector.cs` — projects a Cove media item into the token set the engine consumes. It also
  derives the filename-as-title fallback, once per item rather than once per file. The executor records
  that title in the same save as the rename, which is what makes the rename settle: a title re-derived
  on every run is read from the name the previous run wrote, so a template holding more than `$title`
  would wrap its own decorations again on each pass.
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
- `UndoStopReason.cs` — why one entry stopped short, as a value. Exactly one reason is terminal — the
  file has left the library, which no retry can improve on — so a lock, an unmounted drive or a
  narrowed allowlist all leave the row pending for a later retry. The decision reads the typed reason,
  never the human-readable note beside it.
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
- `POST /renamer` — enqueues the background rename job for selected items.
- `POST /preview-sample` — renders the engine over fixed sample data with the in-flight options;
  powers the live preview without touching the database or disk.
- `POST /undo` — reverse-replays the newest batch that still holds rows, a page at a time. It answers
  with the restored count and, for each of the three problem channels (a reverse move that failed, one
  that was skipped, and a file restored with a companion left behind), a total plus a sample capped at
  a fixed number of entries. The totals are what any sentence states; a sample exists only to name a
  reason. So the response is one fixed size whatever the batch held, and the per-entry detail goes to
  Cove's log. Nothing here rests on the 5,000-file journal cap: that cap bounds what the journal
  records, not what this endpoint replies.
- `GET /last-batch` — a paths-free summary of the most recent batch for the undo panel.
- `POST /scan-library` — enqueues the whole-library dry run.
- `GET /last-scan` — the last dry run's summary: per-status counts and the move summary, merged down
  to the kinds the caller may read.
- `POST /scan-rows` — one page of that dry run's rows, planned on demand, with an optional path search
  and status-bucket filter.
- `POST /renamer-library` — enqueues the whole-library rename job.
- `GET /library-paths` — Cove's configured library paths. Every destination root is chosen from this
  list rather than typed, so a rule holds a reference to a folder Cove owns instead of a copy of its
  path.

The committed `wire/openapi.json` is the contract these ten routes answer to. A test builds a real
host over the shipped registrations, emits the document from them, and fails when the committed copy
no longer matches — so the document cannot go stale without a red build, and the UI's TypeScript wire
types are generated from it rather than declared a second time by hand.

Every endpoint re-checks the caller's permission **in the handler**, and it asks for the permission of
the _kind_ it is about: that kind's read permission to preview it (`videos.read`, `images.read`,
`audios.read`) and its write permission to rename or undo it (`videos.write`, `images.write`,
`audios.write`). A caller holding only some of them is not refused outright — the whole-library
endpoints narrow to the kinds that caller may read. Cove's attribute-based permission filter is inert
on minimal-API routes, so the check is explicit and runs before any work.

Two routes are deliberately coarse instead of per-kind. `/last-batch` and `/library-paths` return
counts and configured roots — never a path from the library, never a kind — so holding any renamer read
permission is enough, and neither can disclose what the library holds. `/undo` starts from the same
coarse gate so an unauthorized caller cannot learn whether a batch exists, then re-checks the write
permission of the kind the batch turns out to name, before it touches disk.

Three surfaces reach different numbers of kinds, and the difference is deliberate:

| Surface                               | Kinds               | Where               |
| ------------------------------------- | ------------------- | ------------------- |
| Endpoints and their permission checks | video, image, audio | `Renamer.Api.cs`    |
| The "Rename selected" bulk action     | video, image        | `GetUIManifest`     |
| The opt-in auto-rename hook           | video, image        | `Renamer.Events.cs` |

So audio is renamed from the Rename tab or through the API, and an audio list carries no "Rename
selected" action. The manifest's description states the endpoint reach and the bulk action's narrower
one together, because that description is what an operator reads before granting the extension access.

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
  `scanRowsStore.ts`, and runs `/renamer-library` after confirmation. `Dialog.tsx` is the shared modal shell it
  and the undo-confirm dialog use.

  The table has no column sorts. A sort needs the whole result set, and the whole result set is
  exactly what neither the store nor the browser holds any more; moving the sort to the server would
  not help, because rows do not exist until they are planned, so ordering by new name or destination
  would mean planning the entire library to answer one page. The one order that is free is the
  cursor's own — kind, then entity id — so that is the order rows are served in, and the table states
  it rather than offering a header that could not act. The two controls that _can_ be answered cheaply
  stay: the status filter, answered by the summary's counts, and the path search, answered by the page
  query using the same match rule the browser used to apply.

- `renameSelected.ts` — the bulk-action handler: preview → confirm → `/renamer`, cancellable.
- `pollJob.ts` / `jobPollLogic.ts` — the single poller over the host's `GET /jobs/{id}`, and the pure
  decision it takes on each read. Both bounds live in the logic module: a job that stops reporting
  progress and a job id that stops answering each end the wait. An expiry is kept distinct from the
  job's own reported failure, because only the second one means nothing was written.
- `UndoSection.tsx` — the undo control backed by `/undo` and `/last-batch`.
- `EntitySelectField.tsx` / `StudioMap.tsx`: the adapter over Cove's own entity selector (every
  studio/tag/performer field in the panel goes through it, with the create affordance off) and the
  per-studio destination-map editor. A rule stores the entity's stable id, and the host resolves that
  id to a name for display: one cached lookup per configured rule, never a list sized by the library.
- `PreviewCard.tsx`, `WarningBadge.tsx`, `TokenLegend.tsx`, `templateValidation.ts`, `presets.ts`,
  `options.ts`, `preview.ts` — supporting UI, types, and the inline token validation. The `*Logic.ts`
  files hold the pure logic split out of their `.tsx` components; `warningBadgeLogic.ts` is keyed on
  the generated status union, so a status the backend grows fails the build rather than reaching a row
  with no badge. The shared UI primitives these render with live in `shared/ui-shared`.

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
- **Detached bodies read as System; request paths do not.** The two job bodies, the load-time
  migrations, the shared batch core and the auto-rename hook all run outside any request, so they
  carry whichever principal happened to reach them, or none at all. Cove's per-principal query filters answer an under-privileged principal with zero rows and
  no error, so each of those bodies takes its scope from the one elevating seam
  (`Cove.Extensions.Shared/RunAsSystem.cs`) rather than opening a plain one. A request path is the
  opposite case and stays on its caller's principal, because elevating it would hand a restricted
  caller rows their own read is denied. Both halves are asserted per entry point, on the principal in
  effect at each database command — never on a row count, which cannot fail on a provider that
  installs no filters.
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
  (`Cove.Core` / `Cove.Plugins` / `Cove.Sdk`, EF Core, Npgsql, …). `Cove.Sdk.targets` strips them from
  the publish set, and the packer copies only the names the catalog entry's `artifacts` array declares,
  so an assembly that is not declared cannot reach a package however the build emits it. A leak on the
  current host is ignored — it loads its own copy and warns once naming the assembly — so the cost is
  package weight, not correctness.
- **ABI-matched local-source build.** When building against a local Cove checkout, the extension
  references the host's own Cove projects so it's binary-compatible with the running host. This is the
  path the deploy script uses.

## Where to start reading

- To understand a rename end to end: `RenamerPlanner.cs` then `RenamerExecutor.cs`.
- To understand the preview: `TemplateEngine.cs` and `Renamer.Api.cs`'s `PreviewSampleAsync`.
- To understand the UI: `RenameSettingsPanel.tsx` and `renameSelected.ts`.
