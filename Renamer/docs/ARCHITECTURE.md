# Architecture

This document walks the Rename extension from "user changed an option" to "file moved on disk and
Cove's database updated." It's meant for a contributor reading the code for the first time.

Rename is a Cove extension in two halves:

- **Backend** — a .NET 10 C# class library (`src/Rename/`, built to `Rename.dll`) that implements
  Cove's `IExtension` contract (deriving `FullExtensionBase` from `Cove.Plugins` / `Cove.Sdk`).
- **Frontend** — a React 19 + TypeScript bundle (`src/Rename.Ui/`, built to `dist/index.mjs`) that
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
chain through Execution. **Undo** replays the Execution layer's revert log in reverse.

## Layer by layer

### Options — `src/Rename/Options/`

The user's saved configuration: the filename and folder templates, multi-value rules, character and
length safety settings, case transforms, required-field gating, and the auto-rename toggle.

- `RenameOptions.cs` — the options model and its JSON (de)serialization settings.
- `OptionsStore.cs` — loads and saves options through Cove's per-extension data store, so the
  configuration persists in Cove and survives extension upgrades.

### Engine — `src/Rename/Engine/`

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

### Planner — `src/Rename/Planner/`

Turns a rendered name into a concrete per-file plan against a real library item, performing **zero**
disk or database mutation.

- `RenamePlanner.cs` — loads the item (read-only), renders each file's new name, applies the
  path-confinement gate, and classifies every file into a plan item with a status (rename, no-op,
  skip-collision, skip-gated, …). It owns collision suffixing, gating, and multi-file handling on the
  plan side.
- `RenamePlan.cs` / the plan-item types — the dry-run result the API returns as the old→new diff.
- `IRenameDataPort.cs` — the abstraction over Cove's entities, so the planner doesn't depend on the
  concrete DbContext or entity types directly (which keeps it testable).
- `MetadataProjector.cs` — projects a Cove media item into the token set the engine consumes.

### Execution — `src/Rename/Execution/`

The only layer that mutates anything. It moves the file and updates Cove's database **together**, so
the two never drift.

- `RenameExecutor.cs` — runs a plan: for each file, move on disk, update the Cove record, and record
  the change in the revert log. Move-first-then-DB with rollback so a failure leaves the file and the
  database consistent.
- `DiskMover.cs` — the actual filesystem move, including sidecar files (captions/subtitles sharing
  the stem) and collision-safe behavior.
- `CoveRenameDataPort.cs` — the concrete `IRenameDataPort` backed by Cove's DbContext.
- `RevertLog.cs` — the append-only batch log that makes undo possible.
- `UndoReplayer.cs` — reverse-replays the most recent batch from the revert log.

### Api — `src/Rename/Rename.Api.cs` (+ `src/Rename/Api/`)

Minimal-API endpoints the frontend calls, mounted under
`/api/extensions/com.alextomas955.rename`:

- `POST /preview` — runs the planner over selected item IDs and returns the old→new plan (no
  mutation).
- `POST /rename` — enqueues the background rename job for selected items.
- `POST /preview-sample` — renders the engine over fixed sample data with the in-flight options;
  powers the live preview without touching the database or disk.
- `POST /undo` — reverse-replays the last batch.
- `GET /last-batch` — a paths-free summary of the most recent batch for the undo panel.

Every endpoint re-checks the caller's permission **in the handler** (`videos.read` to preview,
`videos.write` to rename or undo) — Cove's attribute-based permission filter is inert on minimal-API
routes, so the check is explicit and runs before any work.

The bulk-action registration, the job definition, and the optional auto-rename event hook live
alongside in `src/Rename/Rename.cs` (shared batch core) and `src/Rename/Rename.Events.cs`
(`video.updated` / `image.updated` auto-rename, opt-in and re-entrancy-guarded), with the
background job runner in `src/Rename/Jobs/`.

### Frontend — `src/Rename.Ui/src/`

A Vite library build that Cove loads as `index.mjs`. Its home is a dedicated **Settings → Extensions
→ Rename** tab; it also registers the "Rename selected" bulk action on video and image lists.

- `index.ts` — the bundle entry that registers the components and the bulk-action handler.
- `RenamePage.tsx` / `RenameSettingsPanel.tsx` — the settings tab and its body (the controls + the
  debounced live preview that calls `/preview-sample`).
- `ReviewDialog.tsx` — the "Review & Rename" dialog that runs `/preview` then `/rename` after
  confirmation (the same path the in-list bulk action uses).
- `renameSelected.ts` — the bulk-action handler: preview → confirm → `/rename`, cancellable.
- `UndoSection.tsx` — the undo control backed by `/undo` and `/last-batch`.
- `PreviewCard.tsx`, `WarningBadge.tsx`, `TokenLegend.tsx`, `templateValidation.ts`,
  `presets.ts`, `options.ts`, `preview.ts`, `primitives.tsx`, `dialog.tsx` — supporting UI, types,
  and the inline token validation.

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
- **Preview before disk.** Every rename is previewable as an old→new diff first, and the preview path
  performs zero mutation.
- **Options persist and survive upgrades.** Configuration lives in Cove's per-extension store, not in
  a local file.
- **No host assemblies shipped.** The extension must never bundle host-provided assemblies
  (`Cove.Core` / `Cove.Plugins` / `Cove.Sdk`, EF Core, Npgsql, …). They're stripped from the publish
  set, and the deploy script's strip-verify gate refuses to deploy if any leak in. Bundling them
  would cause load-context type-identity mismatches at runtime.
- **ABI-matched local-source build.** When building against a local Cove checkout, the extension
  references the host's own Cove projects so it's binary-compatible with the running host. This is the
  path the deploy script uses.

## Where to start reading

- To understand a rename end to end: `RenamePlanner.cs` then `RenameExecutor.cs`.
- To understand the preview: `TemplateEngine.cs` and `Rename.Api.cs`'s `PreviewSampleAsync`.
- To understand the UI: `RenameSettingsPanel.tsx` and `renameSelected.ts`.
