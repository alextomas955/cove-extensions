## What changed

A short description of the change and why it's needed.

## How it was verified

Tick only the boxes for the extensions this PR touches.

If this PR touches **Renamer**:

- [ ] `dotnet test extensions/Renamer/src/Renamer.Tests/Renamer.Tests.csproj` passes
- [ ] `cd extensions/Renamer/src/Renamer.Ui && npm run verify` passes

Always:

- [ ] `dotnet build CoveExtensions.slnx` (the whole monorepo) succeeds
- [ ] Built and checked in a running Cove (if the change affects runtime/UI behavior)
- [ ] Docs updated for any settings, config, public API, or user-facing behavior change (or none needed)

Describe what you actually ran and observed.

The C# safety tier is gated by the `test-cove-present` job, not by a box here — a box a contributor can
skip is not a gate. Running `dotnet test` locally with the `../cove` sibling checked out is still worth
doing for one reason CI cannot cover: it compiles against whatever branch that sibling happens to be on,
where CI compiles against the released image, so a regression on your fork branch is visible locally and
nowhere else.

## Safety check

Every extension: any operation that mutates the Cove library or an external system must be
previewable/reversible in spirit — no silent, unrecoverable changes.

- [ ] No host-provided assemblies bundled into the publish output

If this touches **Renamer** — how files move, the database is updated, collisions, or locks:

- [ ] DB and disk still update together (no orphaned files)
- [ ] Never overwrites an existing target; never force-unlocks a held file

## Notes for reviewers

Anything reviewers should pay special attention to, or follow-up work left out of scope.
