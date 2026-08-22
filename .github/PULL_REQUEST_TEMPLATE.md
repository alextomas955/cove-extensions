## What changed

A short description of the change and why it's needed.

## How it was verified

Tick only the boxes for the extensions this PR touches.

If this PR touches **Renamer**:

- [ ] `dotnet test --project extensions/Renamer/src/Renamer.Tests/Renamer.Tests.csproj` passes
- [ ] `cd extensions/Renamer/src/Renamer.Ui && npm run verify` passes

Always:

- [ ] `dotnet build CoveExtensions.slnx` (the whole monorepo) succeeds
- [ ] **Cove-present safety gate (required local pre-merge):** with the `../cove` sibling checked out,
      `dotnet test` both test projects passes — green modulo the documented Windows-only skipped cases on
      macOS/Linux. CI's bare leg Compile-Removes the rollback/undo/ingest/loop-safety tests (Cove.Data is
      source-only), and the containerized e2e job is their CI backstop; this local run gates the C# tier.
- [ ] Built and checked in a running Cove (if the change affects runtime/UI behavior)
- [ ] Docs updated for any settings, config, public API, or user-facing behavior change (or none needed)

Describe what you actually ran and observed.

## Safety check

Every extension: any operation that mutates the Cove library or an external system must be
previewable/reversible in spirit — no silent, unrecoverable changes.

- [ ] No host-provided assemblies bundled into the publish output

If this touches **Renamer** — how files move, the database is updated, collisions, or locks:

- [ ] DB and disk still update together (no orphaned files)
- [ ] Never overwrites an existing target; never force-unlocks a held file

## Notes for reviewers

Anything reviewers should pay special attention to, or follow-up work left out of scope.
