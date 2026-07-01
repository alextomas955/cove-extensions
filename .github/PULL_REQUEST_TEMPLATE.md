## What changed

A short description of the change and why it's needed.

## How it was verified

- [ ] `dotnet test extensions/Renamer/src/Renamer.Tests` passes
- [ ] `cd extensions/Renamer/src/Renamer.Ui && npm run verify` passes
- [ ] Built and checked in a running Cove (if the change affects runtime/UI behavior)

Describe what you actually ran and observed.

## Safety check

If this touches how files move, the database is updated, collisions, locks, or the publish set:

- [ ] DB and disk still update together (no orphaned files)
- [ ] Never overwrites an existing target; never force-unlocks a held file
- [ ] No host-provided assemblies bundled into the publish output

## Notes for reviewers

Anything reviewers should pay special attention to, or follow-up work left out of scope.
