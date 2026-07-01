# Read-only repository audit - 2026-06-29

This document captures the read-only audit of the Rename Cove extension repository performed on
2026-06-29. It is intended to be the durable planning artifact for future cleanup and
contributor-readiness work.

No source changes were made during the audit. Verification commands generated normal ignored build
artifacts only, and the final working tree check was clean.

## Executive summary

The repository is structurally close to a contributor-ready Cove extension, but it should not be
treated as release-ready until the preview purity, entity permission, and reproducible frontend
build gaps are fixed.

Strong points:

- The repo follows Cove's single-extension shape: `extension.json`, backend project, frontend bundle,
  tests, release packaging, and open-source metadata.
- Backend restore/build/test passes from the NuGet dependency path.
- Frontend verification and production bundle build pass locally when the sibling Cove checkout is
  present.
- Runtime extension permissions are minimal: no network, scraper, or downloader runtime permissions
  are requested.
- The test suite is broad and covers planning, execution, undo, routing, events, and concurrency.

Primary risks:

- The documented dry-run preview path can create Cove folder rows in the database.
- Image and audio rename paths are guarded by video permissions.
- CI packages the committed UI bundle without rebuilding or typechecking it from a clean checkout.
- Local development and deployment still assume the author's Windows/sibling-Cove layout in several
  places.
- Release/version metadata is not yet a single coherent source of truth.

## Verification performed

Commands run from the extension repo root (`<cove-dev>/extensions/rename`):

```powershell
dotnet restore Rename.slnx -p:UseLocalCovePlugins=false
dotnet build Rename.slnx -c Release -p:UseLocalCovePlugins=false --no-restore
dotnet test src\Rename.Tests\Rename.Tests.csproj -c Release --no-build --filter "Tier!=Integration"
```

Results:

- Restore passed.
- Release build passed with 0 warnings and 0 errors.
- Non-integration test tier passed: 356 passed, 0 failed, 0 skipped.

Commands run from `src\Rename.Ui`:

```powershell
npm run verify
npm run build
```

Results:

- Typecheck, ESLint, Prettier check, and `check-classes` passed.
- Vite production build passed and produced `dist/index.mjs`.

Additional checks:

- `node --version`: `v24.11.0`.
- `.NET SDK`: `10.0.301`.
- Current local branch: `master`.
- Public NuGet search found `Cove.Sdk` and `Cove.Plugins` latest versions at `0.7.1`.
- Repository pin is `CovePluginsVersion` `0.6.2`.
- Registry packaging dry run passed at the basic artifact level:
  - `dotnet publish src/Rename/Rename.csproj -c Release -o artifacts/audit-registry-publish -p:UseLocalCovePlugins=false`
  - copied `extension.json` and `src/Rename.Ui/dist/index.mjs` into the publish folder
  - created `artifacts/audit-registry/com.alextomas955.rename-0.1.0.zip`
  - ZIP entries were at archive root: `extension.json`, `index.mjs`, `Rename.dll`,
    `Rename.deps.json`, `Rename.runtimeconfig.json`, `Rename.xml`, `Rename.pdb`,
    and `System.IO.Hashing.dll`
  - host assembly leak check found no `Cove.*`, EF Core, Npgsql, Pgvector, or MediatR DLLs
  - audit-time ZIP checksum format was valid: `sha256:<64 lowercase hex>`
- Final `git status --short` was clean.

## Evidence map

Repository files inspected:

- `README.md`
- `CONTRIBUTING.md`
- `CHANGELOG.md`
- `Directory.Build.props`
- `.github/workflows/build.yml`
- `.github/PULL_REQUEST_TEMPLATE.md`
- `.github/ISSUE_TEMPLATE/*`
- `scripts/deploy-dev.ps1`
- `src/Rename/extension.json`
- `src/Rename/Rename.cs`
- `src/Rename/Rename.Api.cs`
- `src/Rename/Rename.Events.cs`
- `src/Rename/Execution/*`
- `src/Rename/Planner/*`
- `src/Rename.Tests/*`
- `src/Rename.Ui/package.json`
- `src/Rename.Ui/package-lock.json`
- `src/Rename.Ui/vite.config.ts`
- `src/Rename.Ui/src/*`

Local Cove source inspected (sibling Cove checkout, `<cove>/src/...`):

- `<cove>/src/Cove.Sdk/CoveExtensionBase.cs`
- `<cove>/src/Cove.Sdk/FullExtensionBase.cs`
- `<cove>/src/Cove.Sdk/buildTransitive/Cove.Sdk.targets`
- `<cove>/src/Cove.Plugins/IExtension.cs`
- `<cove>/src/Cove.Plugins/ExtensionManager.cs`
- `<cove>/src/Cove.Core/Auth/Permissions.cs`
- `<cove>/src/Cove.Api/Services/JobService.cs`
- `<cove>/src/Cove.Api/Program.cs`

External Cove sources consulted:

- [Cove extension overview](https://yourcove.net/docs/developer/extensions/overview/)
- [Cove extension manifest](https://yourcove.net/docs/developer/extensions/manifest/)
- [Cove extension permissions](https://yourcove.net/docs/developer/extensions/permissions/)
- [Cove extension API](https://yourcove.net/docs/developer/extensions/api/)
- [Cove extension UI](https://yourcove.net/docs/developer/extensions/ui/)
- [Cove jobs and events](https://yourcove.net/docs/developer/extensions/jobs-events/)
- [Cove data storage](https://yourcove.net/docs/developer/extensions/data-storage/)
- [Cove packaging](https://yourcove.net/docs/developer/extensions/packaging/)
- [Cove local development](https://yourcove.net/docs/developer/extensions/local-development/)
- [Single-extension template](https://github.com/yourcove/single-extension-repo-template)
- [Multi-extension template](https://github.com/yourcove/multi-extension-repo-template)
- [Community downloaders example](https://github.com/yourcove/communitydownloaders)
- [Community scrapers example](https://github.com/yourcove/communityscrapers)
- [Recommendations example](https://github.com/yourcove/recommendations)
- [Official extension registry](https://github.com/yourcove/officialextensionregistry)
- [Official registry validation workflow](https://github.com/yourcove/officialextensionregistry/blob/main/.github/workflows/validate.yml)
- [Cove.Sdk NuGet](https://www.nuget.org/packages/Cove.Sdk)
- [Cove.Plugins NuGet](https://www.nuget.org/packages/Cove.Plugins)

## Findings

| ID | Severity | Status | Finding | Rationale | Evidence |
| --- | --- | --- | --- | --- | --- |
| F-01 | Blocker | Confirmed | Preview can mutate the Cove database by creating destination folder rows. | Preview is documented as a dry run and should not create durable DB state. A user can preview a routed move to a not-yet-existing folder and leave behind an unused folder row even if they cancel. | `docs/ARCHITECTURE.md` says preview performs zero mutation; `Rename.Api.cs` `PreviewAsync` calls the planner; `RenamePlanner.cs` calls `GetOrCreateFolderIdAsync`; `CoveRenameDataPort.cs` creates and saves folders. |
| F-02 | High | Confirmed | Entity permissions are wrong for image and audio flows. | Cove has separate `videos.*`, `images.*`, and `audios.*` permissions. Rename accepts `video`, `image`, and `audio` kinds internally, but API/UI checks use `videos.read` and `videos.write`. | `Rename.Api.cs` action exposes `video` and `image` but requires `Permissions.VideosWrite`; preview/rename/undo/last-batch endpoints use video permissions; `Rename.cs` accepts audio/image; Cove `Permissions.cs` defines distinct image and audio permissions. |
| F-03 | High | Confirmed | Release CI cannot prove the shipped frontend bundle matches source. | The release workflow packages the committed `dist/index.mjs` but avoids `npm install`, typecheck, and build because the Cove frontend SDK is a local `file:` dependency. A stale or locally built bundle can ship. | `src/Rename.Ui/package.json` depends on `@cove/extension-sdk` via `file:../../../../cove/sdk/frontend`; `.github/workflows/build.yml` explicitly skips Node/build in the packaging job and uses a CI-only lint path. |
| F-04 | Medium-High | Confirmed | Destructive rename jobs are non-exclusive and same-volume work is effectively unbounded. | Concurrent destructive jobs can plan against stale snapshots or target the same paths. Same-volume fan-out can create disk, DB, and event-bus pressure. | `Rename.Api.cs` enqueues with `exclusive: false`; Cove `JobService.Enqueue` defaults to exclusive jobs; `Rename.cs` uses parallel execution for same-volume units. |
| F-05 | Medium | Confirmed | Version/source-of-truth drift exists. | Manifest, runtime metadata, package metadata, changelog labels, README compatibility, and public NuGet versions do not tell one coherent release story. | `extension.json`, `Rename.cs`, and `package.json` say `0.1.0`; `CHANGELOG.md` uses `v1.0` through `v1.3` as milestones; `Directory.Build.props` pins Cove SDK packages to `0.6.2` while public latest is `0.7.1`. |
| F-06 | Medium | Confirmed | Branch naming is inconsistent. | Contributors are told to branch from and open PRs against `main`, and CI only runs pull requests to `main`, but the local branch is `master`. If the hosted default branch is also `master`, PR CI will not run as expected. | `CONTRIBUTING.md`, `.github/workflows/build.yml`, and `git branch --show-current`. |
| F-07 | Medium | Confirmed | Contributor setup is not fully portable across Docker, macOS, and Windows. | Backend NuGet builds are portable, but frontend and deploy paths assume a sibling Cove checkout or Windows-specific Cove installation layout. | `Directory.Build.props` auto-detects `..\..\cove`; `package.json` uses `file:../../../../cove/sdk/frontend`; `scripts/deploy-dev.ps1` uses Windows concepts and includes the author's local absolute-path references. |
| F-08 | Medium | Confirmed | Open-source operations hardening is incomplete. | The repo has a license, contributing guide, issue templates, and PR template, but lacks common security and dependency-maintenance files. Workflow permissions are broader than needed for PR jobs. | No `SECURITY.md`, `CODE_OF_CONDUCT.md`, Dependabot config, or CodeQL config was present; `.github/workflows/build.yml` sets global `contents: write`. |
| F-09 | Medium | Confirmed | Preview can do heavy synchronous request work. | The preview endpoint accepts up to 1000 IDs and loops over planner work in the request handler. This can create latency and load even after the mutation bug is fixed. | `Rename.Api.cs` defines `MaxEntityIdsPerRequest = 1000` and loops through IDs in `PreviewAsync`. |
| F-10 | Low-Medium | Confirmed | Public-facing docs/scripts contain stale AI/process/local-machine residue. | Contributor-facing files should explain durable project facts, not private assistant workflow or local paths. Some residue is stale and contradicts the current UI setup. | Root `CLAUDE.md` says the template ships no frontend SDK usage; `.github/workflows/build.yml` includes planning references; `scripts/deploy-dev.ps1` includes local path references and milestone IDs. |
| F-11 | Low | Confirmed | Logs include full old/new media paths. | Full paths are useful for audit/debugging, but media paths can be sensitive. The project should document that logs contain filenames and paths or consider redaction options. | `Rename.cs` logs old and new paths for renamed/failed items. |
| F-12 | Medium | Confirmed | Registry publication is not yet codified as a local release gate. | The workflow creates a convention-named ZIP on `v*` tags, and the local dry run produced a root-level ZIP with expected files and no host DLL leaks. However, the repo does not yet include a registry metadata draft/checklist, release-asset URL validation, or source-manifest/download URL guidance. Cove registry CI requires `extensions/{id}.json`, `repositoryUrl`, `versions[]`, per-version `downloadUrl`, per-version `minCoveVersion`, and generated `checksum`. The release asset must exist before registry PR validation can compute its checksum. | `.github/workflows/build.yml` packages `com.alextomas955.rename-<version>.zip`; official registry README documents `extensions/{extension-id}.json`; registry `validate.yml` computes checksums from `downloadUrl`; Cove `GitHubExtensionRegistry` downloads and verifies the ZIP before extraction. |
| F-13 | Medium | Confirmed | Publish docs do not yet give destructive-operation backup and clean-install smoke-test guidance. | Rename changes disk paths and Cove DB rows. Before registry publication, user-facing docs should tell users to back up the Cove DB and media files before large first runs, and maintainers should test the exact release ZIP in a clean Cove instance. | `README.md` explains preview/undo and release ZIP install, but does not mention backup guidance or a clean-release smoke checklist; `OptionsStore` and `RevertLog` persist extension state through Cove's extension store. |

## Cove alignment review

Patterns already aligned with Cove:

- Single-extension repository shape.
- `extension.json` with `entryDll`, `jsBundle`, categories, minimum Cove version, and runtime
  permission declarations.
- `FullExtensionBase` is acceptable for a multi-capability extension that contributes API, UI, jobs,
  events, and state.
- Empty network, scraper, and downloader runtime permissions match the extension's current behavior.
- UI manifest actions and settings panel are wired through the Cove extension SDK model.
- Backend build uses Cove SDK host-assembly stripping rules and has local-source and NuGet paths.

Patterns not yet fully aligned:

- Cove's manifest-backed metadata model is undermined by redeclared C# metadata that already differs
  from `extension.json`.
- Cove's entity-specific permission model is not followed for image and audio operations.
- Cove's dry-run/preview expectation is violated by folder creation during planning.
- Cove template release flows expect reproducible packaging from a clean checkout; this repo relies
  on a committed frontend bundle because CI cannot rebuild it.
- Cove examples/templates are more portable than this repo's current local development scripts.

## Contributor-readiness review

Current state:

- Backend contributors can build and test without a local Cove source checkout by using published
  NuGet packages.
- Frontend contributors need a sibling Cove checkout at the expected relative path, because
  `@cove/extension-sdk` is a local file dependency.
- Windows contributors using the author's layout have the smoothest path.
- macOS, Linux, and Docker contributors can likely work on backend code, but frontend install/build
  and local deploy are fragile unless they mirror the author's directory structure.

Target state after the proposed changes:

- `dotnet restore`, `dotnet build`, and `dotnet test` work from a clean clone without a sibling Cove
  checkout.
- `npm ci`, `npm run verify`, and `npm run build` work from a clean clone or documented bootstrap
  flow.
- Optional Cove source integration is controlled by explicit configuration such as `COVE_REPO`, not
  an implicit relative path.
- Local installation/deployment accepts `COVE_HOME` or an explicit argument and works regardless of
  where Cove is installed.
- A dev container pins the SDK/tooling and proves the cross-platform contributor path.

## CI/CD recommendations

Recommended CI shape:

1. Backend verification:
   - `dotnet restore Rename.slnx -p:UseLocalCovePlugins=false`
   - `dotnet build Rename.slnx -c Release -p:UseLocalCovePlugins=false --no-restore`
   - `dotnet test src/Rename.Tests/Rename.Tests.csproj -c Release --no-build --filter "Tier!=Integration"`
   - `dotnet format --verify-no-changes`

2. Frontend verification:
   - Make the Cove frontend SDK reproducible in CI through one of:
     - a published npm package,
     - a generated local tarball,
     - a checked-out Cove SDK source step,
     - a submodule/workspace bootstrap,
     - or committed generated SDK types with a clear update script.
   - Run `npm ci`.
   - Run `npm run verify`.
   - Run `npm run build`.
   - Fail if `src/Rename.Ui/dist/index.mjs` changes after build.

3. Packaging verification:
   - Validate `extension.json`.
   - Validate manifest/runtime/version/package metadata parity.
   - Validate package zip layout.
   - Validate host-provided assemblies are stripped from the artifact.
   - Validate `jsBundle` exists and matches the rebuilt bundle.

4. Security and maintenance:
   - Set default workflow permissions to `contents: read`.
   - Grant `contents: write` only to the tag-release job.
   - Add Dependabot.
   - Add CodeQL or an equivalent security analysis workflow.
   - Add `SECURITY.md`.
   - Consider pinning third-party actions by SHA for release workflows.

5. Registry publication:
   - Publish the GitHub release asset before opening the registry PR.
   - Validate that the release asset URL resolves.
   - Compute or let registry CI compute `versions[].checksum` from `downloadUrl`.
   - Keep `minCoveVersion` on each `versions[]` entry.
   - Keep `index.json` out of local/manual edits; registry CI generates it from `extensions/*.json`.

## Testing strategy

Add tests for the critical gaps:

- Preview purity:
  - A routed preview to a new destination folder must not increase `Folder` row count.
  - A preview must not call any create/save method on the data port.
  - Preview and rename should still agree on collision outcomes after folder creation is deferred to
    execution.

- Permissions:
  - Video preview/rename requires video read/write.
  - Image preview/rename requires image read/write.
  - Audio preview/rename either requires audio read/write or audio support is removed from the API.
  - A principal with only video write must not rename images or audio.
  - A principal with only image write must be able to use image rename if image support remains.

- CI bundle freshness:
  - Rebuilding the frontend bundle in CI must leave `dist/index.mjs` unchanged.
  - CI should fail when source changes but the committed bundle is stale.

- Concurrency:
  - Two rename jobs targeting overlapping source or destination paths cannot both mutate the same file.
  - Same-volume worker count is bounded.
  - Job exclusivity behavior is explicitly tested.

- Portability:
  - Backend NuGet-path restore/build/test succeeds with no sibling Cove checkout.
  - Frontend install/build succeeds through the documented clean-checkout path.
  - Docker/devcontainer smoke test runs backend and frontend verification.

- Publishing and registry:
  - Build the exact release ZIP.
  - Install from that ZIP into a clean Cove instance.
  - Verify extension discovery, settings tab load, settings save/load, preview, rename, undo,
    restart/reload persistence, and disabled/re-enabled behavior.
  - Verify registry metadata `downloadUrl` resolves and checksum matches.

## AI/process residue review

Confirmed cleanup targets:

- Root `CLAUDE.md` contains private workflow guidance and stale claims about frontend SDK usage.
- `.claude/CLAUDE.md` is probably acceptable as local agent context if it remains ignored/private, but
  should not be the contributor-facing source of truth.
- `scripts/deploy-dev.ps1` includes milestone/task identifiers and local path references.
- `.github/workflows/build.yml` includes planning-era comments such as option labels and plan section
  references.
- Some tests and comments include milestone/task phrasing that can be replaced with durable rationale.

Recommended cleanup principle:

- Keep comments that explain enduring architectural constraints.
- Remove comments that narrate who or what process produced the code.
- Move agent-specific instructions to ignored local files or a neutral `AGENTS.md` only if they are
  genuinely useful to future maintainers.

## Concurrency and reliability review

Reliability strengths:

- Executor moves disk first, updates DB second, and rolls back the disk move if the DB save fails.
- Execution performs a collision re-check because the planner snapshot may be stale.
- Undo is backed by an append-only revert log.
- Reindex/update events are published after successful mutations.
- Cross-volume moves have a dedicated copy/verify/promote/delete path.

Reliability risks:

- Planning currently creates folder rows, so dry-run and execution concerns are mixed.
- Non-exclusive destructive jobs can run concurrently.
- Same-volume worker fan-out is not clearly bounded.
- Preview performs potentially heavy work on the request path.
- Full path logging may expose sensitive filenames in logs.

## Registry publication review

The Cove registry is a separate GitHub repository that stores metadata, not extension binaries.
Cove installs registry extensions by reading `index.json`, fetching `extensions/{extension-id}.json`,
downloading the extension's GitHub release ZIP, verifying SHA-256, and extracting the ZIP into the
local extension directory.

Registry requirements confirmed from the public registry README and validation workflow:

- Add `extensions/com.alextomas955.rename.json` in the registry repository.
- Use reverse-domain ID notation; this repo already uses `com.alextomas955.rename`.
- Provide `repositoryUrl`.
- Prefer `sourceManifestUrl` pointing at the raw `extension.json` in this repo so summary metadata can
  sync from source.
- Put release data in `versions[]`, not top-level fields.
- Each version needs `version`, `changelog`, `downloadUrl`, and `minCoveVersion`.
- Registry CI computes `versions[].checksum` from `downloadUrl`; the release ZIP must already exist
  and be reachable.
- Registry CI stamps missing `releasedAt` on merge to `main`.
- `index.json` is generated by registry CI and should not be hand-edited.

Current repo status against those requirements:

- Good: release workflow uses the conventional asset name
  `com.alextomas955.rename-<version>.zip` on `v*` tags.
- Good: local dry run produced a root-level archive layout that Cove's registry extractor can install.
- Good: local dry run found no host-provided assembly leaks in the publish folder.
- Gap: no registry metadata draft/checklist exists in this repo yet.
- Gap: release CI does not currently compute or display the release ZIP SHA-256.
- Gap: release CI does not currently prove the GitHub release asset URL resolves after release.
- Gap: release-readiness docs do not yet require a clean-Cove install smoke test from the exact ZIP.

Recommended registry PR template for this extension:

```json
{
  "id": "com.alextomas955.rename",
  "sourceManifestUrl": "https://raw.githubusercontent.com/alextomas955/rename/main/src/Rename/extension.json",
  "name": "Rename",
  "description": "Bulk-renames Cove library items using configurable metadata templates.",
  "author": "alextomas955",
  "repositoryUrl": "https://github.com/alextomas955/rename",
  "categories": ["tools", "automation"],
  "versions": [
    {
      "version": "0.1.0",
      "changelog": "Initial public release.",
      "downloadUrl": "https://github.com/alextomas955/rename/releases/download/v0.1.0/com.alextomas955.rename-0.1.0.zip",
      "minCoveVersion": "0.6.2"
    }
  ]
}
```

Update the version, branch name, changelog, and URLs to match the actual public release before
submitting. Do not copy the audit-time checksum from this document; the real checksum must be based on
the final release asset.

Recommended order:

1. Make preview truly read-only.
2. Make rename jobs exclusive by default.
3. Add bounded parallelism.
4. Reintroduce concurrency only with explicit per-file or per-destination locking if it is still needed.

## Proposed follow-up milestones

### Milestone 1 - Preview purity hardening

Goal: make `/preview` and planner dry-run paths perform zero disk and database mutation.

Scope:

- Split read-only folder lookup/collision planning from folder creation.
- Move destination folder creation fully into execution.
- Add DB-level regression tests around routed preview into missing folders.

Acceptance criteria:

- Preview cannot create `Folder` rows.
- Existing preview tests still pass.
- Rename execution still creates destination folders when confirmed.

### Milestone 2 - Entity permission matrix

Goal: align Rename permissions with Cove's entity-specific permission model.

Scope:

- Add permission mapping by `RenameFileKind`.
- Decide whether audio is officially supported; either expose it correctly or remove it from public API
  acceptance.
- Update UI action declarations and API handlers.

Acceptance criteria:

- Video, image, and audio read/write checks use their matching Cove permissions.
- Tests cover allowed and denied principals by entity type.

### Milestone 3 - Reproducible frontend and release CI

Goal: make CI rebuild and verify the frontend bundle that release packages.

Scope:

- Remove the clean-runner blocker around `@cove/extension-sdk`.
- Run real frontend install, typecheck, lint, formatting, class guard, and build in CI.
- Add stale-bundle detection.

Acceptance criteria:

- CI can build the UI from a clean checkout.
- Release package uses the verified bundle.
- A source-only frontend change without rebuilding `dist/index.mjs` fails CI.

### Milestone 4 - Cross-platform contributor setup

Goal: make backend, frontend, and packaging flows work on Docker, macOS, and Windows without matching
the author's local directory layout.

Scope:

- Add `global.json`.
- Align README Node/npm guidance with `package.json`.
- Replace implicit sibling-Cove paths with explicit env vars or script arguments.
- Add devcontainer or Docker build/test smoke path.
- Keep Windows deploy script as a convenience, not the only supported path.

Acceptance criteria:

- Clean clone build/test instructions work without the author's local absolute path layout.
- Optional local Cove source path is configured through `COVE_REPO` or equivalent.
- Local Cove install path is configured through `COVE_HOME` or an explicit deploy argument.

### Milestone 5 - Release and metadata consistency

Goal: make versioning and manifest/runtime metadata coherent.

Scope:

- Decide whether `CHANGELOG.md` milestone labels should become real release versions or be renamed.
- Ensure manifest, runtime metadata, UI package, release tag, and zip filename agree.
- Prefer manifest-backed metadata where Cove SDK supports it.
- Decide whether to stay on Cove SDK `0.6.2` or upgrade to current public `0.7.1`.

Acceptance criteria:

- A test or script validates version/metadata parity.
- Release notes and package filenames use the same version.
- No stale duplicate description remains between manifest and C# metadata.

### Milestone 6 - OSS hardening and contributor polish

Goal: make the repo safe and clear for outside contributors.

Scope:

- Add `SECURITY.md`.
- Add `CODE_OF_CONDUCT.md` if desired for the project.
- Add Dependabot.
- Add CodeQL or equivalent security workflow.
- Reduce workflow permissions.
- Clean stale local/AI/process residue from public-facing files.
- Update PR template with permission, manifest, bundle, and human-review checkboxes.

Acceptance criteria:

- A new contributor can understand how to build, test, package, and manually install without private
  local context.
- Security reporting and dependency update paths are documented.
- Public docs contain durable project facts rather than local workflow residue.

### Milestone 7 - Registry publication readiness

Goal: prepare a release that can be added to the official Cove extension registry with a low-friction
registry PR.

Scope:

- Add a release checklist for destructive-operation extensions.
- Add user-facing backup guidance for first large batches.
- Add exact-release-ZIP clean install smoke-test instructions.
- Add a registry metadata draft or template for `extensions/com.alextomas955.rename.json`.
- Add release asset URL and checksum validation to the publish process.
- Confirm upgrade, disable/re-enable, uninstall, and restart behavior around persisted options and the
  revert log.

Acceptance criteria:

- Maintainers can publish a GitHub release, verify the ZIP, and prepare the registry PR without
  rediscovering registry schema requirements.
- The exact release ZIP has been installed into a clean Cove instance and tested through settings,
  preview, rename, undo, restart, and disable/re-enable flows.
- README or release notes advise users to back up Cove's database and media files before first large
  rename batches.
- Registry metadata passes the official registry validation workflow.

## Confirmed versus investigate further

Confirmed:

- Preview mutation through folder creation.
- Entity permission mismatch.
- CI's inability to rebuild the frontend bundle in the packaging job.
- Non-exclusive destructive job enqueue.
- Version and branch drift.
- Windows/local-layout bias.
- Missing OSS hardening files.
- Registry publication requirements are not yet captured in repo docs or release gates.
- Backup and clean-install smoke guidance is missing from user-facing docs.

Investigate further:

- Whether Cove maintainers consider direct `CoveContext` mutation by extensions a stable supported
  pattern or an internal dependency tolerated for this extension.
- Whether `Cove.Sdk` and `Cove.Plugins` should be upgraded to `0.7.1` immediately or remain pinned to
  the extension's current `minCoveVersion`.
- The best distribution path for `@cove/extension-sdk`: published npm package, tarball, submodule,
  generated types, or CI checkout of Cove source.
- Whether audio rename should become an explicit supported feature or be removed from accepted API
  entity kinds until UI and permissions are complete.
- Exact upgrade, disable/re-enable, uninstall, and restart behavior in a real Cove instance after a
  release ZIP install.
- Whether registry reviewers expect screenshots or a more detailed registry description for a
  destructive disk/DB extension.

## Contributor-ready target checklist

- [ ] Preview is read-only.
- [ ] Permissions are entity-specific.
- [ ] Backend build/test works from a clean clone.
- [ ] Frontend install/verify/build works from a clean clone or documented bootstrap.
- [ ] CI rebuilds and verifies the shipped frontend bundle.
- [ ] Package artifact is validated before release.
- [ ] Tool versions are pinned and documented.
- [ ] Cove source and Cove installation paths are configurable.
- [ ] Docker/devcontainer path exists and runs the standard verification commands.
- [ ] Public docs no longer assume the author's local machine layout.
- [ ] Security and dependency maintenance files are present.
- [ ] Registry metadata draft exists and matches the final release.
- [ ] Exact release ZIP has passed clean-Cove smoke testing.
- [ ] User-facing docs include backup guidance for first large batches.
