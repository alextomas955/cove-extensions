## What changed

A short description of the change and why it's needed.

## How it was verified

Tick only the boxes for the extensions this PR touches.

If this PR touches **Renamer**:

- [ ] `dotnet test extensions/Renamer/src/Renamer.Tests/Renamer.Tests.csproj` passes
- [ ] `cd extensions/Renamer/src/Renamer.Ui && npm run verify` passes

If this PR touches **WhisparrSync**:

- [ ] `dotnet test extensions/WhisparrSync/src/WhisparrSync.Tests/WhisparrSync.Tests.csproj` passes
- [ ] `cd extensions/WhisparrSync/src/WhisparrSync.Ui && npm run verify` passes

Always:

- [ ] `dotnet build CoveExtensions.slnx` (the whole monorepo) succeeds
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

If this touches **WhisparrSync** — anything that writes to Whisparr or reads its secret:

- [ ] Outbound mutations to Whisparr are origin-tagged and idempotent
- [ ] Never moves or deletes files inside a Whisparr-owned root
- [ ] No secret (API key / webhook token) is echoed to the UI or written to logs

## Notes for reviewers

Anything reviewers should pay special attention to, or follow-up work left out of scope.
