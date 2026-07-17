## Project

A Cove extension (**Renamer**, `com.alextomas955.renamer`) — a C# class library that plugs into a
self-hosted Cove media-library instance. It lets users rename — and optionally relocate — their
media files based on metadata, using configurable naming templates, all configured from a settings
panel inside Cove's own UI and persisted in Cove's backend.

**Core Value:** Users can rename their media files from metadata **safely** — the operation never
loses track of a file (Cove's database stays authoritative) and is previewable before it touches
disk. If everything else is cut, a reliable dry-run-then-rename that keeps the library intact is
the thing that must work.

> The monorepo-wide rules — the extension-authoring contract, build wiring and Cove source
> selection, the bans on bundling host assemblies and writing to the DB directly, the C#
> comment / XML-doc policy, and documentation upkeep — live in the repo-root `CLAUDE.md` and apply
> here too. This file adds only what is specific to Renamer.

## What NOT to do (Renamer-specific)

| Avoid | Why |
|-------|-----|
| Assuming a core "rename/move file" service exists on the host | **It does not.** Only `POST /api/files/move` (changes folder, keeps basename). The extension does the disk rename itself. |

The monorepo-wide bans (shipping host assemblies, direct SQLite/Postgres writes) are in the root
`CLAUDE.md`.

## Contract

- `extension.json` is the manifest Cove loads the built assembly through; its `entryDll` MUST be
  `Renamer.dll` and the JS bundle is `index.mjs`.
- Renamer subclasses `FullExtensionBase` (`IExtension` from `Cove.Plugins`) and references `Cove.Sdk`
  through the root build wiring — `Renamer.csproj` adds no direct Cove reference of its own.

## Working on this extension

- Renamer lives at `extensions/Renamer/` inside the monorepo. It is **not** its own git repo — no
  own remote and no own CI workflow. CI is defined once at the monorepo root
  (`.github/workflows/build.yml`) and driven by Renamer's entry in `extensions/catalog.json`.
- Launch your editor/agent tooling with the sibling Cove core checkout also available (e.g. via an
  `--add-dir`-style flag) so the local Cove source is available for SDK/source reference — the exact
  relative path depends on where you launch from, so follow your workspace's routing convention
  rather than hardcoding a path here.

## Dev build & deploy

- The **dev local-source build** is the path used to load into the running dev Cove: it resolves
  `Cove.Sdk` from a local Cove checkout so the extension is ABI-identical to the running host (the
  source-selection precedence and host-assembly stripping are handled at the repo root). Use
  `scripts/deploy-dev.ps1` for the full build → strip-verify → deploy → restart loop.
- **Publish-readiness** targets the published NuGet packages (the pinned `CoveSdkVersion`,
  `Private=false`), where `Cove.Sdk.targets` is imported transitively from the package.

## Frontend SDK (`Renamer.Ui`)

- Renamer ships a frontend — the settings/preview panel bundle built from `src/Renamer.Ui/` to
  `dist/index.mjs` — using **`@cove/extension-sdk`**, the Cove *frontend* host SDK (separate from
  `Cove.Sdk`). It is not published to npm, so it is vendored as a committed tarball at
  `src/Renamer.Ui/vendor/` and consumed through a `file:` dependency that `npm ci` installs offline.
  Regenerate the tarball with `scripts/update-cove-sdk.ps1` when the SDK version changes.

## Comments — where they are earned in Renamer

The monorepo C# comment / XML-doc policy (root `CLAUDE.md`) applies. Renamer's value is *safe*
renaming, so the invariants that earn a comment here are the safety-critical ones: TOCTOU windows,
copy-then-verify-then-delete across volumes, MAX_PATH re-anchoring, the single-writer revert log,
and the routing precedence (Tags over Studio over Default). Comment those; leave the obvious code
uncommented.
