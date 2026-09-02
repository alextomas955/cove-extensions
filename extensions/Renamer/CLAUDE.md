# Renamer

Cove extension `com.alextomas955.renamer`. It renames and optionally relocates media files from
metadata templates, previews every change before touching disk, and keeps Cove's database
authoritative. If everything else is cut, dry-run-then-rename that never loses track of a file must
still work.

The repo-root `CLAUDE.md` rules apply here. This file adds only what is specific to Renamer.

## Facts the code does not show

- Cove has no core rename service. `POST /api/files/move` changes the folder and keeps the
  basename, so Renamer does the disk rename itself.
- `extension.json` names `Renamer.dll` as `entryDll` and `index.mjs` as the bundle.
- The backend is one rich capability layered by domain: `Engine/`, `Planner/`, `Execution/` beside
  `Api/`, `Contracts/`, `Jobs/`, `Options/`. Keep that layering. Do not split it into per-verb
  folders.
- UI slices: `settings/` (with the dry-run modal nested at `settings/dry-run/`) and
  `rename-action/`. Extension-local shared code is `common/`.
- `settings/options.ts` is a hand-written REQUEST shape. The options blob travels in the PascalCase
  spelling of the C# record, which the wire document does not describe. Its casing must match the
  record exactly. Everything else the UI reads comes from the generated `wire/api.ts`.

## Build and deploy

- `pwsh scripts/deploy-dev.ps1` builds against the local Cove checkout, builds the UI, assembles the
  catalog's file set, installs it, and restarts the host. Always `pwsh`. Set `COVE_HOME` off
  Windows.
- `@cove/extension-sdk` is not on npm. It is vendored as a tarball under `src/Renamer.Ui/vendor/`
  and installs offline. Regenerate it with `scripts/update-cove-sdk.ps1` when the SDK version
  changes.

## Where comments are needed

The safety invariants: TOCTOU windows, copy-verify-delete across volumes, MAX_PATH re-anchoring,
the single-writer revert log, and the destination routing precedence documented on
`DestinationResolver`.
