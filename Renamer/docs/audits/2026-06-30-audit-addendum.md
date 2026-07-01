# Read-only repository audit addendum - 2026-06-30

This document is an addendum to the read-only audit pass performed on 2026-06-30. It extends, and
does not replace, the 2026-06-29 baseline audit of the Rename Cove extension. Like the baseline, it
is a durable planning artifact for contributor-readiness and release work, and no source, CI, test,
or behavior change was made while producing it.

The baseline established the first thirteen findings, F-01 through F-13. This addendum opens new
ground the baseline did not cover and continues finding numbering at F-14.

## Relationship to the baseline audit

This addendum builds on the [2026-06-29 read-only repository audit](./2026-06-29-read-only-repo-audit.md).

The baseline is preserved verbatim as the historical snapshot of the repository on 2026-06-29. It is
neither rewritten nor re-litigated here. Its findings, evidence, rationale, and recommendations stand
as they were recorded. Where this addendum refers to a baseline finding, it points to that finding by
its original ID and reports its current resolution status; it does not restate the original argument.

The baseline established findings F-01 through F-13. This addendum continues finding numbering at
F-14. Anything below F-14 belongs to the baseline; anything from F-14 onward is new ground opened
here.

## Status of the baseline findings (F-01-F-13)

The baseline's thirteen findings were carried through the shipped releases v1.5 through v1.8. The
table below maps each finding to where it was addressed. F-12 and F-13 were carried forward, not
fixed, and remain queued for a later release.

| ID | Subject | Resolution | Where resolved |
| --- | --- | --- | --- |
| F-01 | Preview can mutate the Cove database by creating destination folder rows. | Resolved | v1.6 |
| F-02 | Entity permissions are wrong for image and audio flows. | Resolved | v1.6 |
| F-03 | Release CI cannot prove the shipped frontend bundle matches source. | Resolved | v1.7 |
| F-04 | Destructive rename jobs are non-exclusive and same-volume work is unbounded. | Resolved | v1.6 |
| F-05 | Version / source-of-truth drift. | Resolved | v1.8 (the Cove SDK re-pin half landed in v1.5; v1.8 closed the remaining metadata coherence) |
| F-06 | Branch naming is inconsistent. | Resolved | v1.7 |
| F-07 | Contributor setup is not portable across Docker, macOS, and Windows. | Resolved | v1.7 |
| F-08 | Open-source operations hardening is incomplete. | Resolved | v1.8 |
| F-09 | Preview can do heavy synchronous request work / 1000-id cap. | Resolved | v1.6 — the 1000-id synchronous preview cap was retained as the intended request-thread read bound; the preview-mutation risk it shared with F-01 was removed |
| F-10 | Public-facing docs/scripts contain stale AI/process/local-machine residue. | Resolved | v1.7 |
| F-11 | Logs include full old/new media paths. | Resolved (documented) | v1.8 |
| F-12 | Registry publication is not yet codified as a local release gate. | Still queued (deferred) | not yet shipped |
| F-13 | Publish docs lack backup and clean-install smoke-test guidance. | Still queued (deferred) | not yet shipped |

This table is a resolution-status pointer, not a re-argument of the original findings. The original
rationale and evidence for each finding remain in the baseline document.

## Executive summary

### What the original audit covered well

The baseline did a thorough job on the repository's core correctness blockers and on its
release-engineering posture. It confirmed and evidenced the three blockers that gated release —
preview purity (F-01), entity permissions (F-02), and a reproducible frontend build in CI (F-03) —
and tied each to concrete code and workflow evidence. It also covered version coherence and
source-of-truth drift (F-05), open-source operations hardening (F-08), and a detailed
registry-publication review with a concrete metadata template and a clean-install checklist. Its
concurrency, reliability, and Cove-alignment reviews were solid and actionable.

### What this addendum adds

This addendum opens ground the baseline did not examine in depth:

- Structured threat models for the destructive paths — destructive-filesystem edge cases,
  consistency-failure splits (disk-moved-but-DB-not and the reverse), and an asset-to-bad-outcome
  mapping.
- Clean-room reproducibility from a fresh clone plus a Cove version-compatibility matrix.
- A deeper dependency, license, and supply-chain review, alongside the contributor and release
  policy surface.
- A staged Docker and end-to-end smoke-test policy.
- An over-engineering, naming, and test-quality review.
- A UI-safety and documentation-tone review.
- A Cove ecosystem comparison, an updated readiness checklist, and recommended follow-up work.

These are the addendum's intended coverage. The findings themselves are written in the sections that
follow, not here.

### Whether the release-readiness rating changed

The baseline withheld release-readiness pending three gaps: preview purity, entity permissions, and a
reproducible frontend build. All three have shipped — preview purity and entity permissions in v1.6,
the reproducible frontend build in v1.7. Those original release blockers are cleared.

Two items the baseline raised remain queued and are not yet shipped: the registry-publication release
gate (F-12) and the backup plus clean-install smoke-test guidance (F-13). The new areas this addendum
examines have not yet been assessed against a readiness bar.

The honest reading: the original release blockers are resolved; final readiness now depends on the
still-queued registry and backup work and on whatever the remaining sections of this addendum
surface. This is not a declaration that the extension is release-ready.

### Whether the priority order changed

The baseline's top-priority items — the three release blockers — have shipped. The active priorities
are therefore the still-queued registry-publication gate (F-12) and backup / clean-install guidance
(F-13), plus whatever the new sections of this addendum surface. The later synthesis in this addendum
folds any new findings into an updated priority order. No reordering of the still-open baseline items
is claimed here without evidence.

## New findings (F-14 and onward)

This table is the home for every new finding raised in the sections that follow. Numbering begins at
F-14, continuing from the baseline's F-13. Each confirmed finding must carry a concrete evidence
anchor — a file path, function, command output, documentation link, or observed pattern.

Severity is one of: Blocker, High, Medium, Low, Nit.

Status is one of: Confirmed, Investigate further.

| ID | Severity | Status | Area | Finding | Evidence | Why it matters | Recommended action | Suggested milestone |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| F-14 | Medium | Confirmed | Filename sanitization | The sanitizer has no Windows reserved-device-name guard. A metadata value that renders to a reserved stem — `CON`, `PRN`, `AUX`, `NUL`, `COM1`–`COM9`, `LPT1`–`LPT9` — or to a `reserved.ext` form (`CON.mkv`) passes through cleaning unchanged, because reserved names contain none of the illegal characters or control characters the cleaner removes. | `src/Rename/Engine/Sanitizer.cs` — `CleanSegment` filters only the illegal set `{ < > : " / \ \| ? * }`, control characters, edge spaces/dots, and run collapses; there is no reserved-name branch. | On Windows the OS refuses to create or open a file whose stem is a reserved device name regardless of extension. A render to `CON.mkv` would fail the move at `File.Move`, surfacing as a `SkipLocked` skip (`DiskMover` classifies the `IOException`) rather than a clean name. The file is never lost — the disk-first discipline and classify-not-throw hold — but a whole class of titles silently cannot be renamed on Windows. | Add a reserved-name check to `CleanSegment` that appends a disambiguating mark (for example an underscore) when a cleaned stem matches a reserved device name case-insensitively, with or without an extension. Cover it with a `SanitizerTests` case. | A correctness-hardening milestone alongside other sanitizer work. |
| F-15 | Medium | Confirmed | Sidecar handling | Only Cove-tracked caption rows are carried with a renamed file. Untracked sidecars that share the stem — `poster.jpg`, `movie.nfo`, `*.srt`/`*.idx`/`*.sub` not modeled as captions, generated thumbnails — are neither moved nor renamed, so a rename or cross-folder move orphans them under the old name and location. | `src/Rename/Execution/RenameExecutor.cs` builds `plannedSidecars` only from `srcFile.Captions` (`RetargetCaption`); `src/Rename/Execution/CoveRenameDataPort.cs` maps `Captions` only on `MapVideoFile`, and `ApplyAndSaveAsync` updates caption filenames only `if (file is VideoFile vf)` — `MapImageFile`/`MapAudioFile` carry no captions at all. | A user who keeps a Kodi/Jellyfin-style `poster.jpg`/`movie.nfo` next to a video, or `.srt` subtitles Cove has not indexed as captions, finds them stranded after a rename: the media moves, the companion art and metadata do not. No data is lost, but the library's on-disk grouping is broken and external scrapers re-detect the media as new. | Document the tracked-caption-only scope in the user-facing rename docs as a known limitation, and consider an opt-in same-stem sidecar sweep (move every file sharing the old stem in the source folder) guarded by the same skip-not-clobber discipline `DiskMover` already applies to captions. Treat the doc note as the near-term action and the sweep as a later opt-in. | A near-term docs milestone for the limitation; a later feature milestone for the optional sweep. |
| F-16 | Low | Investigate further | Same-volume rename | A case-only rename on a case-insensitive volume (`movie.mkv` → `Movie.mkv` on Windows/macOS default) is not specially handled. The collision re-check compares names ordinally but the destination volume treats the two as the same name, and the executor's `PathsEqual` post-save assertion compares `OrdinalIgnoreCase` on Windows. | `src/Rename/Execution/RenameExecutor.cs` — the collision loop tests `File.Exists(ToNative(newFull))` (the OS answers case-insensitively) and `CollisionExistsAsync`; `PathsEqual` uses `OrdinalIgnoreCase` on Windows. `src/Rename/Execution/VolumeClassifier.cs` compares roots `OrdinalIgnoreCase` on Windows. | On a case-insensitive volume a pure case-only rename can read as a self-collision (the existing file is "found" at the target) and either suffix needlessly or skip, and the post-save path-equality assertion would treat the old and new names as equal. The exact behavior depends on the volume's case sensitivity, which the author cannot exhaustively reproduce on a single Windows box. | Reproduce on a case-insensitive and a case-sensitive volume and record the observed behavior before deciding whether a case-only fast path (single-volume two-step rename via a temporary name) is warranted. Do not change behavior speculatively. | Investigate during a cross-platform verification pass. |
| F-17 | Low | Investigate further | Unicode normalization | A rendered name and an on-disk file that differ only by Unicode normalization form (NFC vs NFD — common for accented names originating on macOS) are compared ordinally everywhere, so they are treated as distinct even though they display identically. Normalization is only forced (to NFC) when transliteration is enabled. | `src/Rename/Engine/Sanitizer.cs` — `Transliterate` decomposes to FormD, strips non-spacing marks, recomposes to FormC, but runs only when transliteration is enabled; a non-transliterated name keeps its source normalization. Disk/DB compares in `RenameExecutor` and `VolumeClassifier` are ordinal. | An NFC-vs-NFD mismatch between the rendered name and an existing on-disk file could cause a spurious collision (or miss a real one) and could make the post-save path assertion fail even when disk and DB agree visually. The frequency depends on the source metadata's normalization, which varies by origin OS. | Decide whether to normalize both sides of every name/path comparison to a single form (NFC) before comparing. Confirm the failure mode against real NFD-origin files first; this is a correctness call, not a cosmetic one. | Investigate alongside the cross-platform verification pass. |
| F-18 | Low | Investigate further | Cross-volume move | None of the movers detect a hardlink. A same-volume rename preserves a hardlink (the move only re-points a directory entry), but a cross-volume move is copy → verify → delete-source, which produces an independent copy and severs the hardlink relationship the source participated in. | `src/Rename/Execution/CrossVolumeMover.cs` — `CopyVerifyPromoteDeleteAsync` copies bytes to a new file and deletes the source; nothing inspects link count or inode/MFT identity. `src/Rename/Execution/DiskMover.cs` uses `File.Move`, which preserves a same-volume hardlink. | A user who deduplicates a library with hardlinks (one physical extent shared by several paths) would find a cross-volume rename quietly breaks the sharing for the moved path — correct as a file copy, but a surprise if the user assumed the link survived. Cross-volume moves inherently cannot preserve a hardlink, so this may be acceptable-as-documented rather than a defect. | Confirm whether the target user base uses hardlink dedup, then either document that a cross-volume move breaks hardlinks or detect a multi-link source and warn. Lead with the doc note; do not add link detection speculatively. | Investigate during the cross-platform verification pass. |
| F-19 | Low | Confirmed | Undo durability | The revert log that backs undo lives in Cove's extension store, so clearing the extension's stored data — disabling and uninstalling it, or wiping its store — loses the undo history; the append-only blob also grows without compaction or retention. Neither corrupts a rename, but both bound how far back an undo can reach. | `src/Rename/Execution/RevertLog.cs` persists the newline-delimited blob under the single store key `revertlog` via `IExtensionStore`; there is no retention/compaction path, and the store is the only durable copy. | A user who renames a large batch, then disables/uninstalls the extension (or clears its data), can no longer undo that batch — the recovery record is gone even though the files moved. The unbounded blob is a slow-growth concern for a long-lived library. | Document that undo history is stored in the extension's data and is lost if that data is cleared, and recommend a database/media backup before large first runs (cross-links F-13). Consider a retention/compaction policy and an export of the revert log as later hardening. | The queued backup/clean-install milestone (alongside F-13). |
| F-20 | Low | Investigate further | Log privacy | Per-file old → new media paths are logged in full for audit and troubleshooting, with no redaction or path-verbosity option. This is the documented trade-off from baseline F-11 (audit value vs. path sensitivity), surfaced again here as the privacy-asset row of the asset map; it is an enhancement opportunity, not a regression. | `src/Rename/Rename.Logging.cs` `LogItemRenamed`/`LogItemFailed` and `src/Rename/Rename.cs` `LogBatchItem` log full old/new paths; baseline F-11 documented the exposure in v1.8. | Media paths can reveal titles and library structure to anyone who can read Cove's logs. The current posture is documented-and-accepted; a deployment with stricter log-handling requirements has no built-in way to reduce path detail. | Decide whether a redaction or path-verbosity option is worth adding for privacy-sensitive deployments; until then leave the documented-risk note from F-11 as the mitigation. Do not change logging speculatively. | A later privacy-hardening consideration, gated on real demand. |
| F-21 | Nit | Confirmed | Version prose/comment drift | The live version sources all agree at the current pin — `extension.json` `minCoveVersion`, the `Rename.cs` `MinCoveVersion` override, the `CovePluginsVersion` default, and the three artifact-version sources are all `0.7.1` / `0.1.0` — but four contributor-facing prose and comment locations still cite the old `0.6.2` Cove pin: the requirements line in `README.md`, two lines in `CONTRIBUTING.md`, and the publish-step comment in the release workflow. The version-parity gate does not catch these because it reconciles only the live version sources, not prose. | `README.md` line 110 (`Cove 0.6.x — built against minCoveVersion 0.6.2`); `CONTRIBUTING.md` lines 29 and 36 (`matching 0.6.2`, `Cove.Plugins / Cove.Sdk 0.6.2 from NuGet`); `.github/workflows/build.yml` line 84 publish-step comment (`Cove.Plugins/Cove.Sdk 0.6.2`); the live pin is `0.7.1` in `Directory.Build.props`, `src/Rename/extension.json`, and `src/Rename/Rename.cs`. `scripts/check-version-parity.cjs` reconciles version and `minCoveVersion` across the three live sources only; its own `0.6.2` mention is an intentional doc-comment example of the drift it guards, not a stale claim, and the baseline audit's `0.6.2` mentions are the preserved historical snapshot. | A contributor reading the README or CONTRIBUTING for the compatible Cove version is told `0.6.x` while the extension is built and pinned against `0.7.1`. No build, test, or runtime behavior is affected — the live sources are correct — but the prose understates the host requirement and could mislead a contributor or a registry reviewer about which Cove the extension actually targets. | Update the four prose/comment locations to cite `0.7.1` (or `0.7.x`) to match the live pin, in a docs-coherence pass. Leave the parity gate's example comment and the baseline audit snapshot untouched. | A documentation-coherence milestone alongside the registry-readiness work. |
| F-22 | Low | Confirmed | Release-workflow supply chain | GitHub Actions are pinned by mutable major-version tag, not by commit SHA. The single job that holds write scope is the `build` job, which elevates to `contents: write` for the tag-gated release steps, and the only action that runs under that scope is `softprops/action-gh-release@v3`. A re-pointed tag on that one action would run new code with release-write permission. | `.github/workflows/build.yml` — the `build` job's `permissions: contents: write` block and the tag-gated `softprops/action-gh-release@v3` step; every other action (`actions/checkout@v6`, `actions/setup-dotnet@v5`, `actions/setup-node@v5`, `DavidAnson/markdownlint-cli2-action@v23`) runs under the read-only top-level `permissions: contents: read`. `.github/workflows/codeql.yml` uses `github/codeql-action/init@v3` and `analyze@v3` under `security-events: write`. The baseline's CI/CD recommendations already suggested SHA-pinning release-workflow actions. | A tag pin trusts the action publisher's tag rather than a fixed commit; a compromised or re-pointed tag on the release action would run with `contents: write`. The blast radius is bounded — only the release action holds write scope — but it is the action a supply-chain pin matters most for. | SHA-pin at least `softprops/action-gh-release` (and optionally the CodeQL actions, which carry `security-events: write`) to a verified commit, with the major tag retained in a trailing comment. Dependabot's `github-actions` ecosystem keeps the SHA fresh, so pinning does not strand the action at a stale version. | The registry-readiness / OSS-hardening milestone alongside the release-gate work. |
| F-23 | Low | Confirmed | CI artifact verification | CI publishes the extension down the NuGet path (which `Cove.Sdk.targets` strips) but does not itself assert the published set is free of host-provided assemblies and absolute paths before the release job zips it. That strip-verify discipline currently lives only in the local `deploy-dev.ps1`; CI relies on the strip targets running correctly rather than verifying the result. | `.github/workflows/build.yml` — the Publish and Package steps run with no host-assembly leak check between them; the equivalent gate exists only in `scripts/deploy-dev.ps1` (the `$HostProvidedAssemblies` enumeration that blocks before copy). The baseline confirmed a clean publish set by a one-time dry run, not by a CI assertion. | The release artifact's cleanliness depends on the strip targets behaving as expected on the CI runner; if a future host dependency or a config change defeated the strip, CI would package and release the leak without a gate catching it. The local script catches it, but only on a local deploy, not on the tagged release CI builds. | Add a CI step after Publish that enumerates `artifacts/extension/*.dll`, fails on any name in the host-provided denylist, asserts `Rename.dll` and `System.IO.Hashing.dll` are present, and greps the publish set for absolute paths — porting the `deploy-dev.ps1` strip-verify gate into the always-on release path. | The registry-readiness milestone alongside F-12. |
| F-24 | Nit | Confirmed | Test-source planning residue | The production source tree (`src/Rename/*.cs`) is clean of internal planning vocabulary, but the test tree still carries it in five doc-comment locations across four files: a `research §Architectural Responsibility Map` citation, three `Phase 24 / VER-01` references, a `WR-03 EXECUTOR-SEAM` / `Phase 18` reference, and a `the live cross proof is Phase 24` line. These are shipped in the published AGPL repository's test source, where the repository's standing policy bans internal-process vocabulary. | `src/Rename.Tests/TestSupport/FakeRenameDataPort.cs` line 7 (`research §Architectural Responsibility Map`); `src/Rename.Tests/TestSupport/SubstDrive.cs` line 11 (`Phase 24 / VER-01`); `src/Rename.Tests/Execution/ExecutorAllowlistGuardTests.cs` line 11 (`WR-03 EXECUTOR-SEAM proof for the Phase 18 allowlist guard`); `src/Rename.Tests/Execution/CrossVolumeVerifyFailTests.cs` line 13 and `CrossVolumeUndoTests.cs` lines 13 and 38 (`Phase 24 / VER-01`). The `*.xml` / `bin` / `obj` copies are generated build output of the same comments, not separate sources. | A reader of the published test suite — a contributor or a registry reviewer reading how the safety spine is proven — meets requirement IDs and phase numbers that mean nothing outside the project's private planning history, the exact residue the repository removed from production code and from the user-facing docs. No build, test, or behavior effect; it is a tone-coherence gap in shipped source. | In a docs/comment-coherence pass, reword the five comments to state what the fake or test proves in durable terms (for example "the seam faked so the planner logic is testable without a live CoveContext" and "the live two-drive exercise is a manual cross-platform check") and drop the phase / requirement IDs. Do not touch test behavior. | A documentation-coherence milestone alongside F-21. |
| F-25 | Low | Confirmed | Confirmation proportionality (UI) | The blast-radius escalation the confirm wording is built to scale with — the `ConfirmLevel` (Light / Standard / Heavy) and the per-volume "N items (X GB) move from A to B" lines the backend computes — reaches only the in-list "Rename selected" bulk action, not the settings-panel "Review & Rename" dialog. The bulk action's confirm scales with the move's size and cross-drive span; the panel dialog shows the same flat count-and-table regardless of whether the batch is a single same-drive rename or a large cross-drive copy. | `src/Rename.Ui/src/renameSelected.ts` calls `/preview` and passes `response.summary` into `buildConfirmSummary` (`src/Rename.Ui/src/preview.ts` lines 103–195), which emits the `confirmLevel`-scaled call-to-action and the `volumePairs` blast lines. `src/Rename.Ui/src/ReviewDialog.tsx` calls the same `/preview` (line 65) but consumes only the per-item array — it computes its own `n`/`skipped`/`numbered`/`cleaned` counts (lines 78–83) and renders a fixed "Confirm & rename {n}" button (line 198) with no `summary`/`confirmLevel`/`volumePairs` read. A large cross-drive batch reviewed from the panel is confirmed as quietly as a one-file rename. | The two rename entry points present different confirmation weight for the same destructive operation. A user who reaches the rename through the panel's Review box — the documented second entry point — does not get the heightened "this is a LARGE cross-drive move, files will be COPIED across drives" wording that the bulk action shows, so the panel path under-signals the blast radius for exactly the batches where it matters most. No data is at risk (execution is authoritative and the move itself is unchanged), but the safety-layer signal is inconsistent between the two paths. | Have `ReviewDialog` read the `/preview` response's `summary` (it already calls the endpoint that returns it) and surface the same `ConfirmLevel`-scaled wording and per-volume blast lines the bulk action uses, so both rename entry points confirm a cross-drive batch with equal weight. No backend change is needed — the summary is already on the wire. | A UI-coherence milestone alongside the routing/destination panel work. |
| F-26 | Low | Confirmed | Undo-limitation messaging (UI/docs) | The interface tells the user a rename "can be undone afterwards" and that an undo "can't be undone again", but nowhere states the durability limit the code carries: the undo record lives in the extension's stored data, so disabling/uninstalling the extension or clearing its data loses the ability to undo (the consistency-failure and asset-map sections already filed this as F-19). The user-facing README likewise presents undo as a plain "reverse the most recent batch" without the data-clear caveat or a backup recommendation before a first large run (cross-links the still-queued F-13). | UI copy that promises reversibility without the caveat: `src/Rename.Ui/src/preview.ts` lines 181–185 ("You can undo this afterwards."), `src/Rename.Ui/src/UndoSection.tsx` lines 165–168 and 226–229 ("It can't be undone again."). `README.md` lines 31, 93, 95 describe one-click undo and "every change is reversible through the undo log" with no mention that the undo record is lost if the extension's data is cleared. The durability limit itself is established in F-19 (`src/Rename/Execution/RevertLog.cs`, single store key, no second durable copy). | The interface and the README set an expectation — "you can always undo" — that the storage model does not fully back: an undo is only available while the extension's data persists. A user who renames a large batch and then disables or reinstalls the extension would find the undo gone, with no prior warning from the UI or the docs. This is the honest-messaging gap the safety-layer frame cares about: the tool should not promise more recoverability than it can deliver. | Add a one-line caveat where undo is offered (the Undo section and the confirm copy) and in the README's usage/undo text: undo history lives in the extension's stored data and is lost if that data is cleared, and a database/media backup is recommended before a first large run. This is a copy/docs change recorded here; it cross-links F-19 (the durability limit) and F-13 (the backup guidance), and is not applied by this audit. | The queued backup/clean-install milestone alongside F-13 and F-19. |

These rows are the new findings the threat-model review surfaced: F-14 through F-18 from the
destructive-filesystem section, and F-19 and F-20 from the asset-to-outcome map. The
consistency-failure section raised no new finding — its scenarios are confirmed mitigations or
reported residuals. F-21 is the one new finding the reproducibility and compatibility sections below
surface: a version prose/comment drift that the live-source parity gate does not cover. F-22 and F-23
come from the dependency and supply-chain section below: SHA-pinning the write-scoped release action,
and porting the local strip-verify gate into CI. F-24 is the one new finding the code, naming, and
test-quality sections below surface: planning-process residue still present in five test-source
doc comments, even though the production source tree is clean of it. F-25 and F-26 come from the UI
safety and documentation-tone sections below: the panel's Review dialog does not apply the
blast-radius confirmation escalation the bulk action does, and the undo-limitation messaging in the UI
and the README promises more recoverability than the storage model backs. The remaining edge cases,
assets, dependency surface, abstractions, names, test patterns, UI safety dimensions, and documents
the sections examine are already sound and are recorded as leave-as-is.

## Threat model: destructive filesystem edge cases

This section grades the rename and move surface — the only paths in the extension that touch disk
destructively — against the actual code, from the operating-system and filesystem angle. For each
edge case it records what the code does today (cited to a concrete class and method), where a gap
remains, the test and the user-facing note that would close it, a severity, and a status. The status
separates a mitigation that is confirmed against the code from one that needs further investigation,
and it marks behavior that is already correct as leave-as-is rather than inventing a change for it.

This extends, and does not repeat, the baseline audit's concurrency and reliability review. That
review established the spine these edge cases ride on: the disk-move-first then DB-save ordering, the
execution-time collision re-check, the append-only revert log, and the cross-volume copy → verify →
promote → delete sequence. The rows below do not re-argue that spine; they take it as given and ask,
for each specific OS or filesystem hazard, whether a file can be lost, duplicated, corrupted, or
stranded — and what the code already does about it.

The destructive surface has three movers and one guard. `DiskMover` performs same-volume moves with
the two-argument `File.Move` (never `overwrite:true`) and classifies a locked source or existing
target into a skip instead of throwing. `CrossVolumeMover` performs cross-volume moves as a strict
copy → verify (size and XxHash3 content hash) → fsync (`Flush(flushToDisk:true)`) → atomic promote →
delete-source-last sequence, so an interruption leaves either the intact source or the verified
destination, never a lost or duplicated file. `CanonicalPathGuard` resolves a destination's real
on-disk target (following symlinks and junctions, expanding 8.3 names, refusing device/UNC/extended
syntaxes) and fails closed. `VolumeClassifier` decides same-versus-cross purely from the path root.
The pure string layer — `Sanitizer`, `LengthReducer`, `PathConfinement` — shapes and confines the
name before any of the movers run.

Severity is one of Blocker, High, Medium, Low, Nit. Status is one of Confirmed, Investigate further,
or leave as-is.

| Edge case | Current mitigation (evidence) | Current gap | Recommended test | Recommended user-facing doc | Severity | Status |
| --- | --- | --- | --- | --- | --- | --- |
| Case-only rename on a case-insensitive volume | `RenameExecutor` re-checks collisions against `File.Exists` (the OS answers case-insensitively) and `CollisionExistsAsync`; `PathsEqual` and `VolumeClassifier.SameVolume` compare `OrdinalIgnoreCase` on Windows. | A pure case-only rename can read as a self-collision or pass the post-save assertion as a no-op; behavior depends on the volume's case sensitivity, unverified on a single Windows box. | Add a `CollisionTests`/executor case that renames `movie.mkv` to `Movie.mkv` on a case-insensitive and a case-sensitive temp volume and asserts the observed outcome. | Note that a change affecting only letter case may be treated as a no-op on case-insensitive volumes. | Low | Investigate further (F-16) |
| OS-invalid filename characters | `Sanitizer.CleanSegment` strips or replaces the illegal set `{ < > : " / \ \| ? * }` and all control characters per segment (`SanitizerTests`). | None — every OS-illegal character is removed before a name reaches disk. | Already covered by `SanitizerTests`. | The existing sanitization note suffices. | — | leave as-is |
| Windows reserved device names (`CON`/`PRN`/`AUX`/`NUL`/`COM1`–`9`/`LPT1`–`9`) | `Sanitizer.CleanSegment` removes illegal and control characters but has no reserved-name branch — a reserved stem survives cleaning. | A rendered `CON.mkv` is refused by Windows and fails at `File.Move`, surfacing as a `SkipLocked` skip; the title cannot be renamed on Windows. The file is never lost. | Add a `SanitizerTests` case asserting a reserved stem (with and without an extension) is disambiguated. | Note that reserved device names are adjusted to remain valid on Windows. | Medium | Investigate further → Confirmed gap (F-14) |
| Long paths (MAX_PATH / `\\?\` extended-length) | `LengthReducer` enforces a relative `FilenameMax`/`FullPathMax` budget; `PathConfinement.Resolve` independently re-checks the absolute folder+basename against `FullPathMax`; `CanonicalPathGuard` refuses to trust an incoming `\\?\` destination (`PathConfinementTests`, `CanonicalGuardPrefixTests`). | `FullPathMax` defaults to 259 (`RenameOptions`), the classic Windows limit, not the live OS limit; it is a configured number, conservative by default. | Already covered by `LengthReducerTests`/`PathConfinementTests`. | Note the configurable path-length cap and its conservative default. | Low | leave as-is (default is conservative; confirm only if a long-path opt-in is added) |
| Trailing spaces and dots (Windows) | `Sanitizer.TrimEdges` trims leading and trailing spaces, dots, and the configured space-replacement token per segment (`SanitizerTests`). | A segment that is entirely dots or spaces reduces to empty; the downstream name then rests on the extension and the collision/suffix loop rather than a trim rule. | Add a `SanitizerTests` case for an all-dots / all-spaces segment and assert the resulting name is still valid. | The existing sanitization note suffices. | Nit | leave as-is (trim is correct; the empty-reduction case is a sanitizer detail, not a destructive hazard) |
| Unicode normalization (NFC vs NFD) | `Sanitizer.Transliterate` normalizes to NFC, but only when transliteration is enabled; otherwise the name keeps its source form and all disk/DB compares are ordinal. | An NFC-vs-NFD mismatch between a rendered name and an on-disk file can cause a spurious collision or a failed post-save assertion even when the names display identically. | Add a comparison test pairing an NFC and an NFD spelling of the same accented name and assert the intended behavior. | Note that accented names from different source systems may compare as distinct. | Low | Investigate further (F-17) |
| Locked files | `DiskMover.Move` catches `IOException` (Windows sharing violation) into `MoveOutcome.LockedOrExists`; `CrossVolumeMover` classifies a locked source identically. The locking process is never touched (`LockedFileTests`). | None — a locked file is skipped and reported, never forced. | Already covered by `LockedFileTests`. | Note that a file in use is skipped and reported. | — | leave as-is |
| Permission-denied files | `DiskMover.Move` and `CrossVolumeMover.CopyVerifyPromoteDeleteAsync` catch `UnauthorizedAccessException` into `MoveOutcome.PermissionDenied`, a skip. | None — a permission failure is a clean skip, never a throw. | Covered by the mover skip-classification tests. | Note that a file the process cannot access is skipped. | — | leave as-is |
| Symlinks | `CanonicalPathGuard.Check` resolves the destination ancestor with `Directory.ResolveLinkTarget(returnFinalTarget:true)` and fails closed on a target that escapes the allowed roots (`CanonicalGuardSymlinkTests`). | The guard runs only when `AllowedRoots` is configured (routing); with no allowlist the source-confine string gate applies and there is no allowlist to canonically re-check. | Already covered by `CanonicalGuardSymlinkTests`. | Note that destination routing resolves links and confines to allowed roots. | — | leave as-is (the conditional is by design: no allowlist, no routed destination) |
| Junctions | `CanonicalPathGuard` resolves reparse points the same way as symlinks and confines the real target (`CanonicalGuardJunctionTests`, `CanonicalGuard8Dot3Tests` for the 8.3 alias case). | Same `AllowedRoots`-configured conditional as symlinks. | Already covered by `CanonicalGuardJunctionTests`. | Same routing note as symlinks. | — | leave as-is |
| Hardlinks | `DiskMover` uses `File.Move`, which preserves a same-volume hardlink; `CrossVolumeMover` copies bytes to a new file and deletes the source, which severs the hardlink relationship. Neither mover inspects link count or inode/MFT identity. | A cross-volume move breaks a hardlink the source participated in. This is inherent to a copy-then-delete across volumes, so it may be acceptable-as-documented. | Add a cross-volume test that hardlinks the source, moves it, and asserts the documented behavior. | Note that a cross-volume move produces an independent copy and does not preserve hardlinks. | Low | Investigate further (F-18) |
| Docker bind mounts | `VolumeClassifier.SameVolume` keys on `Path.GetPathRoot`, pure string math with no I/O. | A bind mount can make two paths on the same physical device present different roots (or the reverse), so the same-versus-cross routing decision can misclassify; unprovable on the author's Windows host. | Add a classifier test with paths that share a device but differ by mount point once a Linux/Docker verification environment exists. | Note that container bind-mount layouts affect whether a move is treated as same-volume. | Low | Investigate further |
| NAS / network shares (UNC) | `CrossVolumeMover` forces bytes to media with `Flush(flushToDisk:true)` before promote and delete, and verifies by re-read; `CanonicalPathGuard` rejects an un-allowlisted UNC destination. The no-clobber and verify guarantees hold over UNC. | Whether an SMB server honors `FlushFileBuffers` durably, and whether the promote rename is atomic over UNC, is filesystem-dependent and unverified. | Add a cross-volume test against a UNC target in an environment that has one. | Note that durability and atomicity over network shares depend on the server and protocol. | Low | Investigate further |
| Two items generating the same destination (intra-batch) | `RenameExecutor`'s execution-time collision loop re-suffixes each candidate against both disk (`File.Exists`) and DB (`CollisionExistsAsync`) up to `MaxSuffixAttempts`; the caller resolves each distinct destination folder once before parallel execution (`CollisionTests`). | None — a within-batch destination clash is re-suffixed or skipped, never clobbered. | Already covered by `CollisionTests`. | Note that duplicate target names are auto-suffixed. | — | leave as-is |
| Destination file already exists | Two-argument `File.Move` is no-clobber in `DiskMover`; `CrossVolumeMover` adds a no-clobber pre-check and a `CreateNew` partial; the collision loop re-suffixes before the move (`CollisionTests`, `CrossVolumeMoverTests`). | None — an existing destination is never overwritten. | Already covered. | The auto-suffix note covers this. | — | leave as-is |
| Destination folder already exists | `CoveRenameDataPort.GetOrCreateFolderAsync` resolves an existing `Folder` row rather than failing; the movers' `EnsureParentDir` uses `Directory.CreateDirectory`, which is idempotent. | None — an existing destination folder is reused, not duplicated. | Covered by the executor move tests. | No user-facing note needed. | — | leave as-is |
| Sidecar files (subtitles / thumbnails / posters / NFO / generated metadata) | Cove-tracked captions are moved skip-not-clobber and retargeted to the new stem (`RenameExecutor.RetargetCaption`, `DiskMover` sidecar moves, `SidecarTests`). | Only tracked caption rows move, and only for video (`CoveRenameDataPort` maps `Captions` on `MapVideoFile` only; the save path updates captions only `if (file is VideoFile vf)`). Untracked `poster.jpg`/`movie.nfo`/un-indexed subtitles are orphaned by a rename. | Add a test that places an untracked same-stem sidecar and asserts the documented behavior (and, if a sweep is added, that it moves). | Document the tracked-caption-only scope as a known limitation. | Medium | Confirmed gap (F-15) |

The matrix above resolves to two confirmed gaps with concrete code anchors — the missing Windows
reserved-name guard and the untracked-sidecar orphaning — and four items that need cross-platform
reproduction before any behavior changes (case-only rename, Unicode normalization, hardlink
semantics, and the bind-mount/UNC environment questions). The destructive spine itself — no-clobber
moves, copy-verify-promote-delete across volumes, fail-closed path resolution, disk-first ordering,
and the collision re-check — is confirmed correct and is left as-is. The new findings are recorded as
F-14 through F-18 in the table above; the bind-mount and UNC investigation items are tracked in the
matrix rather than promoted to findings because each still needs an environment to confirm before it
can carry an evidence anchor stronger than the classifier's string-only basis.

## Threat model: consistency failures between disk and the Cove database

This section grades the rename, move, and undo paths against the actual code from the
data-consistency angle: every way the bytes on disk and the rows in Cove's database could come to
disagree, and whether the code keeps them consistent or recovers when something fails partway. Where
the previous section asked whether a file could be lost, duplicated, or stranded on disk, this one
asks the complementary question — whether the database can end up pointing at a file that is not
there, or a file can end up somewhere the database does not know about, and whether an undo can
always recover.

It extends, and does not repeat, the baseline audit's concurrency and reliability review. That
review recorded the spine in summary form: disk move first, database save second, a rollback on a
failed save, an execution-time collision re-check, an append-only revert log, and post-mutation
events. The rows below take that spine as given and grade each specific consistency split against the
code that implements it. As in the previous section, a mitigation confirmed against the code is
separated from one that needs further investigation, and behavior that is already correct and stable
is marked leave-as-is rather than carrying an invented change.

The spine itself is worth stating once so the rows can lean on it. `RenameExecutor.ExecuteItemAsync`
runs each item independently inside a per-item `try`/`catch`, so one item's failure never aborts the
batch. For an acting item it moves the file on disk FIRST (the same-volume `DiskMover.Move` or the
cross-volume `CrossVolumeMover.MoveAsync`), then calls `CoveRenameDataPort.ApplyAndSaveAsync`, which
sets `Basename`/`ParentFolderId` only — never `Path`, which Cove's `ComputeFilePaths` recomputes
inside the overridden `SaveChangesAsync` — and issues a single save. After the save it asserts at
RUNTIME (not `Debug.Assert`, which no-ops in Release) that the recomputed `Path` equals the on-disk
path it just moved to; a divergence is a `Failed` result, never a silent accept. Only after that
assertion passes does it append the revert-log row and publish the reindex event. If the save throws,
the `catch` rolls the disk back through the SAME mover that performed the move and surfaces the
rollback warnings.

Severity is one of Blocker, High, Medium, Low, Nit. Status is one of Confirmed, Investigate further,
or leave as-is.

| Scenario | Current mitigation (evidence) | Current gap | Recommended test | Recommended user-facing doc | Severity | Status |
| --- | --- | --- | --- | --- | --- | --- |
| Disk rename succeeds, the database update fails | Disk-first/database-second ordering means a failed save never leaves the database pointing at a moved-away file: on a save throw `RenameExecutor`'s `catch` rolls the disk back through the matching mover (`DiskMover.Rollback` same-volume, `CrossVolumeMover.RollbackAsync` cross-volume) so disk and database end consistent. The post-save runtime `PathsEqual` assertion catches a recompute that disagrees with disk and fails the item rather than accepting a divergence (`RenameExecutor.cs` save `try`/`catch` + the recomputed-Path assertion; `RollbackTests`). | A best-effort rollback can be INCOMPLETE — the old slot was re-occupied, or a cross-volume copy-back failed verify, or a target is locked. The code does not hide this: it concatenates the rollback warnings into the `Failed` item's reason (`rollback INCOMPLETE: …`) so the divergence is reported, not swallowed. | A `RollbackTests` case that forces a save throw after a successful move and asserts the file is restored to its old path; a second case that re-occupies the old slot before rollback and asserts the warning surfaces in the failed item's reason. | Note that a failed database save rolls the file back, and that on the rare incomplete-rollback path the run report names the file so an operator can reconcile it by hand. | Low | leave as-is (the ordering and rollback are correct; the incomplete-rollback residual is reported, not hidden — a doc note, not a code change) |
| Database update succeeds, the reindex event fails to publish | The reindex event is published AFTER the successful save and the passing path assertion (`RenameExecutor.cs` step 8: `_revertLog.AppendAsync` then `_eventBus.Publish`), so disk and database are already consistent and the revert-log row is already written before the event fires. An `IEventBus.Publish` that threw would be caught by the per-item `catch` and bucket the item as an unexpected-error `Failed`, but the rename itself already committed durably. | A failed publish leaves Cove's search index lagging the rename until the next reindex; it does not corrupt disk or database state. The file is correctly renamed and undoable; only the derived index is stale. | An executor test injecting a throwing `IEventBus` and asserting disk + database are consistent and the revert-log row exists even though the item is reported failed. | Note that search/index updates are derived and may briefly lag a rename if event delivery hiccups; a rescan reconciles them. | Low | Investigate further (confirm the host's reindex ret/rescan behavior before deciding whether a publish retry is warranted) |
| Partial success across a large batch | Every item is independent: `RenameExecutor.ExecuteAsync` loops items under per-item `try`/`catch` and returns `Renamed`/`Skipped`/`Failed` buckets; the batch driver tallies them with `Interlocked` across parallel workers and logs each item's outcome (`Rename.cs` PHASE B `RunUnitAsync`; `Rename.Logging.cs` `LogItemRenamed`/`LogItemSkipped`/`LogItemFailed`/`LogBatchDone`). The revert log records ONLY the successes (the append is on the success path after the assertion), so the recovery record matches exactly what changed. | None — a batch is designed to partially succeed without corrupting the items that did or did not move. | The existing executor bucket tests cover the mixed-outcome case. | Note that a batch can partially succeed: some files rename while others skip or fail, the log records each outcome, and undo reverses exactly the files that moved. | — | leave as-is |
| Undo after a partial success | The revert log appends a row only on a confirmed success, so a reverse replay sees exactly the files that actually moved — never an item that skipped or failed forward (`RevertLog.AppendAsync` is called only on `RenameExecutor`'s success path; `UndoReplayer.RevertAsync` replays `batch.Entries`). The batch header is opened only AFTER PHASE A produced acting work and cleared the free-space refusal, so an all-skip or refused batch writes no header and cannot shadow an earlier replayable batch (`Rename.cs` defers `BeginBatchAsync`). | None — undo replays precisely the recorded successes. | A revert-log/undo test that runs a mixed batch and asserts undo restores only the succeeded files (`RevertLog`/`UndoReplayer` tests). | Note that undo reverses the last batch's successful renames; items that were skipped or failed were never changed and need no undo. | — | leave as-is |
| Undo after files were moved externally | Undo is path-driven from the recorded old/new paths (NOT metadata-driven — it does not re-plan, because metadata may have changed), and before each reverse move it collision-re-checks that the OLD slot is free on BOTH disk and database, and the reverse move itself is the 2-arg never-overwrite move. A file the user moved out from under the rename surfaces as a reported skip, never a clobber (`UndoReplayer.RevertEntryAsync` step 2 collision re-check + the never-overwrite reverse `DiskMover.Move`/`CrossVolumeMover.MoveAsync`; `UndoReplayer` tests). | None — an externally moved or replaced file is skipped with a reason, not overwritten. | An undo test that relocates or replaces the file between rename and undo and asserts a skip-with-reason, never an overwrite. | Note that if you move or replace a renamed file yourself, undo will skip it rather than overwrite whatever now sits in either location. | — | leave as-is |
| Undo after Cove's folder state changed | Before any reverse mutation `UndoReplayer` checks `Directory.Exists` on the resolved OLD directory and skips with `"original directory no longer exists"` when it is gone, and it does NOT recreate it — recreating could restore a file to a wrong or relocated place when the original drive is offline or the folder was deleted, violating the never-lose-track-of-a-file value. `Directory.Exists` returns false (never throws) on an unmapped/offline drive, so the offline-old-drive case classifies cleanly. A relocating undo also re-gates the restore target through `CanonicalPathGuard.Check` against the configured allowlist, in case the allowlist changed or the old directory is now a junction-to-elsewhere (`UndoReplayer.RevertEntryAsync` steps 1a re-gate + 1b dir-missing skip). | None for the deleted/offline case — undo declines rather than guessing. The re-gate intentionally applies only to a relocation (`DirOf(old) != DirOf(new)`); an in-place restore writes back into the directory the file already legitimately occupies, so gating it would make ordinary renames non-undoable the moment any allowed root is set. | An undo test that deletes the original directory before undo and asserts the skip; a second that takes the old drive offline; a third that sets an allowlist and asserts an in-place undo still succeeds while a relocating undo outside the allowlist is skipped. | Note that undo will not recreate a destination folder you have since deleted, and will not restore a file to a drive that is currently offline — it skips and reports so nothing is lost. | — | leave as-is (the dir-missing skip and the relocation-only re-gate are both deliberate; a doc note, not a code change) |
| Preview goes stale before execution | The planner mutates nothing — no `File.Move`, no `SaveChangesAsync`, no `Directory.Create`; a routed preview resolves the destination folder id read-only via `TryGetFolderIdAsync` (the preview-purity fix, baseline F-01) — and the executor re-checks collisions against LIVE disk (`File.Exists`) and database (`CollisionExistsAsync`) at execute time, re-suffixing or skipping rather than trusting the planner's snapshot (`RenamePlanner.PlanFileAsync` read-only collision loop; `RenameExecutor.cs` step 3 execution-time collision loop). Execution is authoritative. | The blast-radius summary the user confirms (`BatchPreview.Summarize` — counts, cross-volume bytes, `ConfirmLevel`) is a snapshot; the library can change between preview and confirm, so the figures shown can differ slightly from what executes. The execution-time re-check keeps this safe (no clobber), but the displayed numbers are not a guarantee. | A test that mutates the target slot between plan and execute and asserts the executor re-suffixes/skips rather than clobbering (`RenameExecutor` collision tests); a preview-purity test asserting the planner creates no `Folder` rows. | Note that the preview is a snapshot of the library at preview time; the actual rename re-checks every target at execution, so a library that changed in between is handled safely even if the preview counts differ slightly. | Low | leave as-is (execution is authoritative; the snapshot caveat is a small UI doc note, not a code change) |

The consistency spine is heavily hardened, and the grading reflects that: six of the seven scenarios
are leave-as-is, because the disk-first/database-second ordering, the rollback-through-the-matching-mover
on a failed save, the runtime recomputed-path assertion, the append-on-success revert log, and the
path-driven undo with its collision re-check, dir-missing skip, and restore-target re-gate together
close every split where disk and database could silently disagree. Two residuals are reported rather
than hidden — an incomplete best-effort rollback is named in the failed item's reason, and a failed
reindex event leaves a derived index stale without touching the committed rename — and neither is a
correctness defect in the rename itself. The single Investigate-further item is the reindex-staleness
question, which depends on the host's rescan behavior rather than this extension's code; it is graded
Low and carries no new finding, because the rename, the database, and the undo record are all correct
regardless of how the host catches up its index. No new F-NN finding is raised by this section.

## Threat model: protected assets and the outcomes that would harm them

The previous two sections graded the operations — the filesystem hazards and the disk/database
consistency splits. This one inverts the frame: it lists what the extension is protecting and the bad
outcome that would befall each asset, then maps each to the current mitigation, the gap, and the
recommended test, release gate, or doc. This is the asset-protection angle that completes the three
threat-model views. Where an asset's protection is the resolution of a baseline finding shipped in
v1.5 through v1.8, this section cross-links that resolution rather than re-arguing it.

Each row group names a protected asset, the bad outcome that would harm it, the mitigation graded
against the code (or the cross-linked resolution), the remaining gap, and the recommended test,
release gate, or doc.

| Asset / bad outcome | Current mitigation (evidence) | Current gap | Recommended test / release gate / doc |
| --- | --- | --- | --- |
| Cove database rows — wrong files renamed / database-disk inconsistency | The move is database-authoritative through `CoveContext`: `CoveRenameDataPort.ApplyAndSaveAsync` sets `Basename`/`ParentFolderId` ONLY and never `Path` (Cove's `ComputeFilePaths` recomputes it on save), reads are `AsNoTracking` so a dry-run plan can never write back, and the executor asserts the recomputed `Path` matches disk before recording success (`CoveRenameDataPort.cs`; `RenameExecutor.cs` path assertion). No raw SQL, no schema-fragile direct writes. | None for the rename path — the database is the source of truth and the recompute assertion catches a disagreement. | Keep the recomputed-path assertion test and the `AsNoTracking` read-purity test; document that the extension mutates only `Basename`/`ParentFolderId` and lets Cove own `Path`. |
| Media files — data loss | Same-volume moves use the 2-arg no-clobber `File.Move`; cross-volume moves use copy → verify(size + XxHash3) → fsync → atomic promote → delete-source-LAST, so an interruption leaves either the intact source or the verified destination; a failed database save rolls the disk back (`DiskMover`, `CrossVolumeMover.CopyVerifyPromoteDeleteAsync`, `RenameExecutor` rollback). The destructive-filesystem section above grades the full mover surface. | None confirmed beyond the cross-platform investigate-further items already filed there (F-16 through F-18). | Covered by the mover and rollback tests; the user-facing backup guidance (baseline F-13, still queued) remains the recommended belt-and-suspenders for first large runs. |
| Folder structure — database-disk inconsistency | `CoveRenameDataPort.GetOrCreateFolderAsync` resolves an existing `Folder` row rather than duplicating it, and PHASE A resolves every distinct destination folder ONCE on a single sequential scope before parallel execution, so no worker does a check-then-act create on a shared row (`Rename.cs` PHASE A folder pre-resolution). Preview no longer creates folder rows (baseline F-01, resolved v1.6). Destructive directory operations (removing an emptied source folder) are deliberately deferred to a later release. | Emptied source directories are left behind after a cross-folder move — intentional for v1, since `rmdir` is the riskiest directory operation. | Document that a move does not delete the now-empty source folder; track the optional cleanup as a later feature behind a confirmation. |
| Extension state / options — corrupt or unreadable settings blocking use | Options persist as JSON in Cove's `IExtensionStore` via `OptionsStore`; the settings panel tolerates an unreadable options blob by falling back to defaults with a non-blocking notice and self-healing on the next Save, and it preserves fields it does not model so a newer routing/destination option is not dropped by an older panel (settings-panel blob resilience, shipped). | The resilience is in the panel; a programmatic consumer reading the blob directly would still need to handle a malformed value. This is acceptable as-is for the supported UI path. | Keep the panel's defaults-and-notice test and the unmodeled-field round-trip test; no code change recommended. |
| Revert log — undo cannot recover | The revert log is an append-only, newline-delimited, single-writer blob in `IExtensionStore` (`RevertLog`): a process-wide `SemaphoreSlim` keyed on the store key serializes every append across both intra-batch parallel workers and two non-exclusive jobs over the same key; a batch header marks each run open or consumed; parsing is defensive (a malformed line is skipped, never thrown) and a legacy header-less blob still reads (`RevertLog.cs`). | The log lives in the extension store, so a store wipe — disabling and uninstalling the extension, or clearing its data — loses the undo history. The append-only blob also grows unbounded; there is no compaction or retention policy. Neither corrupts a rename, but both bound how far back an undo can reach. | Document that undo history lives in the extension's stored data and is lost if the extension's data is cleared; recommend a database/media backup before large first runs (cross-links baseline F-13, still queued). Recorded as F-19. |
| User privacy / logs — log leakage of sensitive paths | Per-file old → new paths are logged through the source-generated `LoggerMessage` methods for audit and troubleshooting, at Information for success/skip and Warning for failure (`Rename.Logging.cs`, `Rename.cs` `LogBatchItem`). The privacy exposure of full media paths in logs was reviewed and documented in v1.8 (baseline F-11). | Paths are logged in full; there is no redaction option. This is a documented trade-off (audit value vs. path sensitivity), not a regression. | leave-as-is with the documented-risk note from F-11; a redaction/verbosity option is a Low investigate-further enhancement, not a defect. Recorded as F-20. |
| Registry / release-package integrity — a malicious or broken release artifact | The release workflow packages the conventional `com.alextomas955.rename-<version>.zip`; the audit-time dry run confirmed a root-level archive layout with no host-provided assembly leaks. Codifying the registry metadata, the release-asset URL check, and the published-checksum step as a local release gate is baseline F-12, still queued. | The registry-publication release gate (checksum display, asset-URL resolution, metadata draft) is not yet codified; Cove's registry CI verifies the SHA-256 from `downloadUrl`, but this repo does not yet prove the asset before opening the registry PR. | Release gate: build the exact release ZIP, install it into a clean Cove instance, and verify its SHA-256 against the registry `downloadUrl` before publishing (cross-links F-12). |
| Frontend-bundle integrity — a stale bundle ships the wrong UI | The reproducible-frontend-build CI gate rebuilds and verifies `dist/index.mjs` and fails the build if a source-only change leaves the committed bundle stale (baseline F-03, resolved v1.7). | None — the gate exists and is the protection. | leave-as-is; cross-links F-03. Keep the stale-bundle CI check as a release gate. |
| Contributor trust — a contributor cannot reproduce the build, or an AI refactor silently removes safety logic | Open-source operations hardening shipped in v1.8: `SECURITY.md`, `CODE_OF_CONDUCT.md`, Dependabot, a CodeQL analysis workflow, and least-privilege workflow permissions (baseline F-08, resolved). Clean-room reproducibility from a fresh clone is examined in its own section of this addendum. | The reproducibility-from-fresh-clone story and the contributor matrix are tracked in the reproducibility section rather than here. The "AI refactor hides safety logic" outcome has no automated guard beyond the existing tests and CodeQL. | Release gate / review discipline: the safety invariants — disk-first/database-second ordering, no-clobber moves, copy→verify→delete-source-last, the canonical-path allowlist guard, and the single-writer append-only revert log — are exactly what a code review and the test suite must protect; a change that removes any of them should fail a test or be caught in review. Cross-links F-08. |

The asset map raises two new findings — the revert-log durability across a store wipe (F-19) and the
absence of a log-path redaction option (F-20) — and otherwise resolves to mitigations that are either
confirmed correct in the current code or already shipped as baseline-finding resolutions (F-01, F-03,
F-08, F-11) or still-queued ones (F-12, F-13) that this section cross-links rather than re-litigates.
The untracked-sidecar orphaning that also threatens on-disk grouping was already filed as F-15 by the
destructive-filesystem section, so it is not duplicated here.

## Confirmed mitigations, items to investigate further, and behavior to leave as-is

This grading spans all three threat-model sections — the destructive-filesystem edge cases, the
disk/database consistency splits, and the asset-to-outcome map — and states plainly which mitigations
are confirmed against the code, which items still need investigation, and which behaviors are correct
and should not be changed.

Confirmed against concrete code, class, or test anchors:

- The destructive spine: no-clobber `File.Move` (`DiskMover`), the cross-volume
  copy → verify → fsync → atomic-promote → delete-source-last sequence (`CrossVolumeMover`), the
  fail-closed canonical-path allowlist guard (`CanonicalPathGuard`), and the pure string confinement
  gate (`PathConfinement`).
- The consistency spine: disk-first/database-second ordering with rollback-through-the-matching-mover
  on a failed save, the runtime recomputed-`Path` assertion, and the append-on-success revert log
  (`RenameExecutor`, `CoveRenameDataPort`, `RevertLog`).
- The undo spine: path-driven reverse replay with the old-slot collision re-check, the
  dir-missing/offline-drive skip, the relocation-only restore-target re-gate, and the same rollback
  discipline as the forward path (`UndoReplayer`).
- The single-writer serialization of the revert log across parallel workers and concurrent jobs
  (`RevertLog`'s store-key-scoped `SemaphoreSlim`).
- Preview purity: the planner mutates nothing and resolves destination folders read-only
  (`RenamePlanner`, `CoveRenameDataPort.TryGetFolderIdAsync`; baseline F-01, resolved v1.6).
- Entity-specific, in-endpoint permission checks that gate on the request's own kind and return 403
  before any mutation (`Rename.cs` `PermissionsFor`; `Rename.Api.cs` `ICurrentPrincipalAccessor`
  checks — the host's `[RequiresPermission]` filter is MVC-only and inert on minimal-API endpoints, so
  the check is done manually in the handler; baseline F-02, resolved v1.6).
- The shipped release-engineering and hardening gates: the reproducible frontend bundle (F-03), the
  open-source operations files (F-08), and the documented log-path trade-off (F-11).

Investigate further (each needs an environment or a host behavior confirmed before any change):

- Case-only renames on case-insensitive volumes (F-16), Unicode NFC/NFD comparison (F-17), and
  cross-volume hardlink severing (F-18) — all pending cross-platform reproduction.
- Docker bind-mount and UNC same/cross-volume classification — pending a Linux/Docker and a network
  environment.
- Reindex-event staleness after a failed publish — pending confirmation of the host's rescan
  behavior; the rename, database, and undo record are correct regardless.
- A log-path redaction/verbosity option (F-20) — an enhancement, not a defect.

Leave as-is (correct and stable; no speculative change):

- Every consistency scenario except the reindex-staleness investigate-further item — the ordering,
  rollback, assertion, append-on-success log, and path-driven undo are correct.
- The reported (not hidden) residuals: an incomplete best-effort rollback named in the failed item's
  reason, and the deliberate dir-missing/offline-drive undo skip.
- The preview blast-radius snapshot — execution is authoritative, so the snapshot caveat is a UI doc
  note, not a code change.
- The deferred destructive directory cleanup (leaving an emptied source folder) and the
  tracked-caption-only sidecar scope (the latter already filed as F-15 with a doc-note recommendation).
- The shipped baseline resolutions cross-linked above (F-01, F-02, F-03, F-08, F-11) and the
  still-queued registry/backup work (F-12, F-13), which this addendum points at rather than reopening.

This addendum is the deliverable of the threat-model work: the three threat-model sections above —
the destructive-filesystem edge cases, the disk/database consistency splits, and the
asset-to-outcome map — together with the two new findings they surfaced (F-19, F-20) added to the
findings table. No source, CI, test, manifest, or behavior change was made; the only file written was
this document.

## Clean-room reproducibility: proven versus assumed

This section extends, and does not repeat, the baseline audit's "Contributor-readiness review" and
"CI/CD recommendations" sections. Those sections described a target state — a clean clone that
restores, builds, and tests without a sibling Cove checkout, a reproducible frontend bundle, and a
container path that proves the cross-platform contributor flow. This section adds the actual
command-run evidence for that target state: it records what was demonstrated by running the safe,
read-only verification commands, and grades plainly what that evidence does and does not prove.

### The honesty frame

The verification commands below were run on the author's machine: Windows (OS build 10.0.26200),
.NET SDK `10.0.301` (RID `win-x64`), Node `v24.11.0`, npm `11.17.0`, **with a sibling Cove checkout
present at the conventional `../../cove` path** (the local checkout is at Cove tag `v0.7.1`). The
backend commands were run with `-p:UseLocalCovePlugins=false` precisely because the build selector in
`Directory.Build.props` would otherwise auto-detect that sibling Cove and build the extension from
local source; forcing the flag false drives the extension assembly down the published-NuGet path,
which is the closest this host can get to a clean clone.

Running these commands here proves the **author-machine path**. It does **not** prove clean-room
reproducibility. A fresh clone on a machine with no sibling Cove, a GitHub Actions clean runner, a
Docker or devcontainer environment, a non-Windows OS, and a clean Cove install from the exact release
ZIP were not exercised on this host. One observation makes the caveat concrete: even with
`-p:UseLocalCovePlugins=false`, the Release build of the solution still compiled `Cove.Core`,
`Cove.Plugins`, and `Cove.Data` **from the sibling local source** under `I:\cove-dev\cove\src\…`. The
*extension* assembly (`Rename.dll`) restored its `Cove.Plugins`/`Cove.Sdk` references from NuGet as
intended, but the **test project** (`src/Rename.Tests`) carries its own `Exists(...)`-guarded Cove
`ProjectReference`s for the integration tier that resolve whenever the sibling Cove is present,
independent of the `UseLocalCovePlugins` flag. So the solution-wide build on this machine is a hybrid:
the deployable extension built clean from NuGet, but the test project still leaned on the local Cove.
That is exactly the author-machine-versus-clean-room gap a single Windows box cannot close on its own.

### Proven on this author machine

Each line below cites the command that produced it; every figure is the real captured output.

- **Backend restore (NuGet path)** — `dotnet restore Rename.slnx -p:UseLocalCovePlugins=false`
  succeeded (exit 0); `Cove.Plugins`/`Cove.Sdk` `0.7.1` restored from nuget.org for the extension
  project.
- **Backend Release build (NuGet path for the extension)** —
  `dotnet build Rename.slnx -c Release -p:UseLocalCovePlugins=false --no-restore` succeeded with
  0 warnings and 0 errors. `Rename.dll` built without a local Cove path; the test project's Cove
  references resolved from the sibling source (the hybrid noted above).
- **Unit-tier tests** —
  `dotnet test src/Rename.Tests/Rename.Tests.csproj -c Release --no-build --filter "Tier!=Integration"`
  reported **358 passed, 0 failed, 0 skipped**. The integration tier was deselected by the filter, as
  on CI.
- **Frontend install (offline, vendored SDK)** — `npm ci` in `src/Rename.Ui` succeeded, adding 212
  packages with 0 reported vulnerabilities. It resolved the vendored `@cove/extension-sdk` tarball
  (`file:vendor/cove-extension-sdk-0.1.0.tgz`) offline against the lockfile integrity hash, with no
  Cove checkout consulted.
- **Frontend verify** — `npm run verify` (typecheck, ESLint, Prettier check, and the host-class guard)
  passed; `check-classes` reported "no host-absent classes, no raw-HTML rendering".
- **Frontend build + bundle freshness** — `npm run build` produced `dist/index.mjs` (66.48 kB), and
  `git diff --exit-code -- src/Rename.Ui/dist/index.mjs` showed the committed bundle is
  **byte-identical** to the freshly built one — the local equivalent of the stale-bundle gate passing.
- **Working tree** — after all runs, `git status --porcelain --untracked-files=no` showed no tracked
  source change; the only build output (`bin/`, `obj/`, `node_modules/`, `dist/`) is gitignored.

### Proven by continuous integration

Part of the clean-room story is already demonstrated by the existing CI on every pull request, which
the baseline's CI/CD recommendations called for and which has since shipped (baseline F-03 and F-07,
both resolved). The release workflow is designed for a **bare runner with no Cove monorepo**: a single
checkout of this repo only, the backend built and published down the NuGet path
(`-p:UseLocalCovePlugins=false`), the unit tier run with `--filter "Tier!=Integration"`, the frontend
installed offline from the vendored SDK tarball via `npm ci`, verified and rebuilt, and a stale-bundle
gate (`git diff --exit-code -- dist/index.mjs`) that fails the build if the committed bundle drifts
from source. CI pins the frontend toolchain to the exact Node `24.11.0` from the UI package's Volta
field so a Node patch delta cannot make the bundle bytes flaky.

Because that workflow runs on `ubuntu-latest` with no sibling Cove, the **fresh-Linux-runner path is
proven by CI**: a clean Linux checkout restores, builds, unit-tests, and rebuilds the bundle without a
Cove checkout on every PR. The `.devcontainer` (`mcr.microsoft.com/devcontainers/dotnet:1-10.0` plus
the Node 24 feature, running `.devcontainer/verify.sh` on create) describes a container path that runs
the same backend-plus-frontend smoke, though the devcontainer itself is exercised by whoever opens it,
not on every PR.

### Not yet demonstrated

The following scenarios were not exercised on this host. The "What would prove it" cell is a concrete
read-only validation command or a follow-up pointer — not a fix; the author cannot self-verify these
on one Windows box.

| Scenario | What would prove it | Status |
| --- | --- | --- |
| Fresh Windows clone with no sibling Cove | Clone to a temp directory outside `i:\cove-dev` so the `..\..\cove` auto-detect misses, then run `dotnet restore`/`build`/`test -p:UseLocalCovePlugins=false` and the `src/Rename.Ui` `npm ci`/`verify`/`build`. Confirms the test project compiles its unit tier with no Cove present, the clean-clone case the CI gate covers on Linux but not on Windows. | Pending a clean Windows environment |
| Fresh Linux clone | The `ubuntu-latest` `build` and `frontend` CI jobs already do this on every PR; reproduce locally with `.devcontainer/verify.sh` on a clean Linux checkout. | Proven by CI (cross-links resolved baseline F-03, F-07) |
| Fresh macOS clone | Run `.devcontainer/verify.sh` (or the backend NuGet-path commands plus the frontend pipeline) on a clean macOS checkout with no sibling Cove. | Investigate further (no macOS host available) |
| GitHub Actions clean runner | Already exercised: the bare-runner `build`, `frontend`, `csharp-format`, and `version-parity` jobs in `.github/workflows/build.yml`. | Proven by CI |
| Docker / devcontainer | Open the repo in the `.devcontainer` (`devcontainer.json` runs `bash .devcontainer/verify.sh` as `postCreateCommand`) on a clean container and confirm the backend-plus-frontend smoke passes. | Pending a container run |
| Clean Cove install from the exact release ZIP | Build the release ZIP, install it into a fresh Cove instance from that exact artifact, and confirm discovery, settings load, preview, rename, undo, restart persistence, and disable/re-enable. | Pending a clean-Cove environment (cross-links baseline F-13) |
| Backend-only contributor path | On a clean clone with no Cove and no Node, run only `dotnet restore`/`build`/`test -p:UseLocalCovePlugins=false`. | Pending a clean environment (the NuGet design supports it; CI's Linux run is the closest proof) |
| Frontend-only contributor path | On a clean clone, run only `npm ci`/`verify`/`build` in `src/Rename.Ui` with no .NET and no Cove. | Pending a clean environment (CI's `frontend` job is the closest proof) |
| No absolute path leaks into the artifact | `dotnet publish src/Rename/Rename.csproj -c Release -o artifacts/extension -p:UseLocalCovePlugins=false`, then grep the published files for an absolute path (`I:\`, `/home/`, `/Users/`) and re-run the host-assembly leak check the baseline performed. | Investigate further (baseline confirmed a clean root-level ZIP with no host DLL leaks; an absolute-path grep of the publish set was not re-run here) |
| From-scratch build after deleting build output | Delete `bin/`, `obj/`, `node_modules/`, and `dist/`, then re-run the full backend and frontend pipeline and confirm a green result with a byte-identical rebuilt bundle. | Pending a clean run (the incremental runs here passed; a deleted-output cold build was not exercised) |

### Summary

Three readiness tiers are honestly distinguishable. **Proven by CI:** the fresh-Linux-runner path —
clean checkout, NuGet-path backend, offline vendored-SDK frontend, and the stale-bundle gate — runs on
every pull request. **Proven on the author machine:** the backend NuGet-path restore/build and the
358-test unit tier, the offline `npm ci`, the frontend verify/build, and a byte-identical bundle — all
on Windows with a sibling Cove present, which means the test tier still leaned on the local Cove.
**Not yet proven:** a fresh Windows clone with no sibling Cove, a macOS clone, the Docker/devcontainer
runtime, and a clean-Cove install from the exact release ZIP. The reproducibility evidence surfaced one
new finding — the version prose/comment drift recorded as F-21 — because the README and CONTRIBUTING
still cite the old `0.6.2` Cove version while the live pin is `0.7.1`. Beyond that, the CI plus
vendored-SDK plus devcontainer story already covers the provable ground; the remaining scenarios need
an environment this author does not have, and they belong to a later cross-platform and clean-install
verification milestone rather than to a code change here.

## Cove version-compatibility matrix

This section is report-only: **no version upgrade was performed and none is recommended here.** It
records the version surface the extension presents to a Cove host and reads the compatibility risk; any
actual version-bump work belongs to a later compatibility milestone.

### Declared, pinned, and latest versions

| Item | Value | Source |
| --- | --- | --- |
| Declared minimum host version | `0.7.1` | `src/Rename/extension.json` `minCoveVersion` and the `Rename.cs` `MinCoveVersion` override; the version-parity gate (`scripts/check-version-parity.cjs`) keeps these two in agreement and fails CI on drift. |
| Pinned Cove SDK / plugin version the build restores | `0.7.1` | `CovePluginsVersion` default in `Directory.Build.props`; both the `Cove.Plugins` and `Cove.Sdk` `PackageReference`s in `src/Rename/Rename.csproj` resolve to it. |
| Extension artifact version | `0.1.0` | `extension.json` `version`, the `Rename.cs` `Version` override, and `src/Rename.Ui/package.json` `version` — the three sources the parity gate reconciles. |
| Latest public `Cove.Sdk` / `Cove.Plugins` on NuGet | `0.7.1` (both) | `dotnet package search Cove.Sdk --source https://api.nuget.org/v3/index.json` and the same for `Cove.Plugins`, run read-only during this pass; each reported latest `0.7.1`. The pin is current with the latest public SDK. |
| Latest Cove release / main-dev | `0.7.1` observable; newer not confirmed | The sibling Cove checkout on this machine is at tag `v0.7.1`; whether a newer Cove release or main-dev exists upstream was not discoverable read-only on this host. Investigate further. |

The pinned SDK, the declared minimum host version, and the latest public SDK all coincide at `0.7.1`,
so there is no SDK-version gap to close. The only version drift found is in contributor-facing prose,
not in any live source — recorded as F-21.

### Per-target expectations at the pinned 0.7.1

The "Basis" column distinguishes what was live-verified against a single running 0.7.1 host, what is
proven only by the test suite, and what is unproven against any host other than the author's 0.7.1 dev
instance. The live-verification facts are drawn from the v1.5 and v1.6 release records.

| Target | Expectation at pinned 0.7.1 | Basis |
| --- | --- | --- |
| Restore | `Cove.Plugins`/`Cove.Sdk` `0.7.1` restore from nuget.org with no Cove checkout. | Confirmed this pass (`dotnet restore -p:UseLocalCovePlugins=false`, exit 0) and on CI. |
| Build | Release build succeeds, 0 warnings / 0 errors, and the publish set strips all host-provided assemblies. | Build confirmed this pass (0/0); the no-host-leak strip was confirmed by the baseline's publish dry run and the `deploy-dev.ps1` strip-verify gate. |
| Load (into a running Cove) | The built assembly loads into a 0.7.1 host and the extension registers its API, UI, jobs, and events. | Live-verified against the running 0.7.1 dev host in v1.5. Not proven against any other 0.7.x patch or a future release. |
| Show UI (settings / preview panel renders) | The settings tab and live-preview panel render inside Cove's UI; an unreadable options blob falls back to defaults with a non-blocking notice. | Live-verified on the 0.7.1 host (load + settings) in v1.5; the settings-panel blob resilience shipped and is unit-tested. Not proven on another host. |
| Preview | A dry-run preview returns the planned old→new names and creates no `Folder` rows. | Live-verified on the 0.7.1 host in v1.5–v1.6 (preview purity is the resolution of baseline F-01); also covered by preview-purity tests. |
| Rename | A confirmed rename moves the file on disk and updates the matching Cove DB row together, disk and DB authoritative. | Live-verified byte-for-byte on the 0.7.1 host in v1.5 (video round-trips). Broadly covered by executor tests; not proven against a different host. |
| Undo | The last batch's successful renames reverse from the revert log; skipped/failed items are untouched. | Live-verified byte-for-byte on the 0.7.1 host in v1.5; covered by `UndoReplayer`/`RevertLog` tests. |
| Restart (survives a host restart) | Persisted options and the revert log survive a Cove restart because they live in Cove's extension store. | Architecturally backed by `IExtensionStore` persistence; a dedicated restart-persistence pass against the exact release ZIP remains queued (baseline F-13 / the registry-readiness work). Not yet live-verified end-to-end. |
| Disable / re-enable | Disabling and re-enabling the extension preserves stored options; clearing the extension's data loses the undo history (recorded as F-19). | The data-loss-on-clear behavior is confirmed against the code (F-19); a live disable/re-enable cycle against the exact release ZIP remains queued (baseline F-13). |

### Compatibility risk read

The chief compatibility risk is that the pin is a single point. The extension is ABI-bound to the
host's Cove assemblies: it builds and ships against `0.7.1`, declares `minCoveVersion 0.7.1`, and the
host-assembly strip plus the published-NuGet build path exist precisely to keep the deployable
ABI-identical to the host it runs against. A host on a Cove version that differs from the pinned SDK is
the failure mode that the `minCoveVersion` gate and the strip-and-NuGet design are built to bound — a
host older than `0.7.1` is refused by the declared minimum, and a host on a newer Cove with an
incompatible ABI would surface at load rather than silently misbehave. The live verification to date
covers exactly one host: the author's running `0.7.1` dev instance. Behavior against a different `0.7.x`
patch or a future Cove release is not proven and would need re-verification against that host. This
section remains report-only — it records the version surface and the risk; any decision to track a
newer Cove or to widen the supported host range belongs to a later compatibility milestone.

## Dependency, license, and supply-chain review

This section extends the baseline audit's "CI/CD recommendations" — specifically its security-and-maintenance
subsection (default `contents: read` permissions, Dependabot, CodeQL, `SECURITY.md`, and the
SHA-pinning suggestion) — by going deeper into the actual dependency tree the repository ships and
restores. The baseline recommended the hardening; this section grades the dependencies that hardening
now governs, gluing each grade to the file that proves it.

### NuGet dependencies

The extension assembly (`src/Rename/Rename.csproj`) carries exactly one non-host runtime dependency,
and it carries it deliberately. `System.IO.Hashing 10.0.9` is a first-party Microsoft BCL package
(`net10.0`, no transitive closure) that the cross-volume mover uses for its XxHash3 size-and-hash
verify. The `Rename.csproj` comment documents that it is placed OUTSIDE both `UseLocalCovePlugins`
conditional groups so it ships on the local-source and the published-NuGet path alike, and is NOT
marked `<Private>false</Private>` — it must copy-local and ship. The `deploy-dev.ps1` strip-verify
gate asserts `System.IO.Hashing.dll` IS present in the publish set, the mirror image of its
host-assembly leak check (`scripts/deploy-dev.ps1` — the `System.IO.Hashing.dll` presence assertion).
This is the only runtime dependency the deployable folder contains beyond `Rename.dll` and the UI
bundle, and it is graded Confirmed-safe: a pinned, dependency-free, first-party package whose presence
is a release gate.

The Cove references — `Cove.Plugins` and `Cove.Sdk` at the pinned `0.7.1` — are host-provided, not
bundled. On the published-NuGet path they restore from nuget.org and `Cove.Sdk.targets` (auto-imported
from the package's `buildTransitive/`) strips the host closure on publish; on the local-source path the
`ProjectReference`s carry `<Private>false</Private>` and `<ExcludeAssets>runtime</ExcludeAssets>`, and
`Rename.csproj` explicitly imports `Cove.Sdk.targets` so the local path strips identically
(`src/Rename/Rename.csproj` — the two conditional `ItemGroup`s plus the conditional `Import`). The
test project (`src/Rename.Tests/Rename.Tests.csproj`) carries the test-only NuGet surface — `xunit
2.9.3`, `xunit.runner.visualstudio 3.1.4`, `Microsoft.NET.Test.Sdk 17.14.1`, `Xunit.SkippableFact
1.5.61`, `coverlet.collector 6.0.4`, and the integration-tier `Microsoft.EntityFrameworkCore.Sqlite`
and `Microsoft.EntityFrameworkCore.InMemory` at `10.0.6` — none of which ship in the deployable, since
the release publishes `Rename.csproj` alone (`IsPackable=false` on the test project). The known
SQLite-native transitive vuln advisory (`NU1903` on `SQLitePCLRaw.lib.e_sqlite3`) is documented in the
test csproj as test-only and never part of the `Rename.dll` payload.

Transitive risk on the deployable is bounded by evidence, not assertion: the `deploy-dev.ps1`
strip-verify gate enumerates every published `*.dll`, blocks before any copy if a name matches the
`$HostProvidedAssemblies` denylist (`Cove.Core`/`Cove.Plugins`/`Cove.Sdk`/the EF Core trio/`Npgsql`/
`Pgvector`/`MediatR.Contracts`), and the baseline's publish dry run confirmed no host-DLL leak. The
deployable's NuGet closure is therefore one first-party BCL dll — graded Confirmed.

### npm dependencies

The frontend package (`src/Rename.Ui/package.json`) has a single runtime dependency, the vendored
`@cove/extension-sdk` resolved as `file:vendor/cove-extension-sdk-0.1.0.tgz`; everything else is the
dev/build-time toolchain — `vite 6.4.3`, `typescript 5.8.3`, `eslint 10.6.0` and its plugins,
`prettier 3.9.1`, `react`/`react-dom 19.2.7`, `lucide-react 0.513.0`, and the `@types/*` packages. The
committed lockfile is `lockfileVersion 3` with 213 package entries, exact-pinned. None of the
toolchain ships in the bundle: the Vite library build externalizes `react` and `lucide-react` to the
host import-map and bundles only the SDK, so the transitive npm surface is a build-time concern that
never reaches a user's Cove. The vendored SDK is a supply-chain STRENGTH rather than a risk —
`scripts/update-cove-sdk.ps1` re-packs it from a local Cove checkout into the committed tarball, and
`npm ci` resolves it offline against the lockfile integrity hash (`sha512-…`) with no registry fetch
and no Cove checkout, which both removes a network dependency from the build and pins the SDK bytes by
hash. Graded Confirmed: the runtime surface is one hash-pinned vendored package, the rest is
externalized dev tooling.

### Lockfiles, version pinning, and reproducibility

Reproducibility is Confirmed-strong and is the subject of its own section above; here it is graded
from the dependency angle. The npm lockfile is committed (`lockfileVersion 3`, exact pins,
integrity-hashed). The .NET SDK is pinned by `global.json`, the Node toolchain by the `volta.node
24.11.0` field that CI's `setup-node` mirrors exactly so a Node patch delta cannot make the bundle
bytes flaky (`.github/workflows/build.yml` — the `frontend` and `version-parity` jobs both pin
`node-version: '24.11.0'`), and the Cove SDK by `CovePluginsVersion` in `Directory.Build.props`. This
cross-links the reproducible-CI work (F-03) and the operations hardening (F-05); no dependency here is
floating.

### Unnecessary dependencies

No unused dependency was found. `System.IO.Hashing` is exercised by the cross-volume mover; the test
NuGet packages are each used by a test tier; the npm toolchain is exercised by `npm run verify` and
`npm run build`. The grade is Confirmed — there is no dependency to drop.

### License compatibility

The extension is `AGPL-3.0-or-later` (`Rename.csproj` `PackageLicenseExpression`, the `LICENSE` file's
GNU AGPL v3 text, and `package.json` `"license": "AGPL-3.0"`). The vendored `@cove/extension-sdk` is
AGPL-3.0 (`update-cove-sdk.ps1` documents this and notes vendoring it is license-compatible because it
matches this repo's license). The dev toolchain is permissive (MIT/ISC/BSD) and build-time-only, which
does not encumber an AGPL deliverable. Graded Confirmed compatible.

### Dependabot coverage

`.github/dependabot.yml` configures four ecosystems — `nuget` at `/src/Rename`, `nuget` at
`/src/Rename.Tests`, `npm` at `/src/Rename.Ui`, and `github-actions` at `/` — each weekly with a
five-PR limit. The file correctly documents that it does NOT and cannot bump the `file:`-vendored SDK
(that is re-vendored by hand), so the one runtime npm dependency is intentionally outside Dependabot's
reach and is pinned by tarball hash instead. This cross-links the operations hardening (F-08); the
coverage is present and correct. Tuning the config — grouping patch updates into a single PR, for
instance — is a low-value option for a repository this size: with one runtime npm dependency, a single
extension NuGet dependency, and a small action set, the five-PR weekly cadence is unlikely to generate
review churn, so no grouping change is recommended.

### SBOM

An SBOM (CycloneDX or similar) adds little marginal value for this specific deployable. The shipped
artifact is a folder, not a registry package; it strips every host-provided assembly and ships exactly
one first-party BCL dll plus the UI bundle; and that surface is already pinned (`global.json`, the
lockfile, `CovePluginsVersion`) and proven by the strip-verify gate. An SBOM would enumerate a
single non-host runtime component. It is a defensible add for a public release if a downstream consumer
or a registry policy asks for one, but it is not recommended as a self-motivated step here — the small,
pinned, stripped surface is the SBOM's content already, in the manifest and the lockfile. This is a
recommendation-with-reasoning, not a finding.

### GitHub Actions versions and SHA-pinning

The workflows use tag-pinned actions: `actions/checkout@v6`, `actions/setup-dotnet@v5`, and
`actions/setup-node@v5` across `build.yml` and `codeql.yml`; `softprops/action-gh-release@v3` in the
tag-gated release step; `DavidAnson/markdownlint-cli2-action@v23` in `markdown-lint`; and
`github/codeql-action/init@v3` / `analyze@v3` in `codeql.yml`. A major-version tag is mutable — the tag
can be re-pointed at a new commit — so a tag pin trusts the action publisher's tag rather than a fixed
commit. The risk is sharply concentrated: the only job that holds write scope is the `build` job, which
elevates to `contents: write` for the tag-gated release steps (`build.yml` — the job-level
`permissions: contents: write` block scoping the elevation off the read-only top-level default), and the
only action that runs under that scope is `softprops/action-gh-release@v3`. Every other action runs
read-only. That makes `action-gh-release` the single highest-value SHA-pin target. The baseline already
suggested SHA-pinning for release workflows; this addendum records it as a concrete finding because the
write-scoped release action is now identifiable by name. Dependabot's `github-actions` ecosystem keeps
the pinned SHAs fresh either way, so SHA-pinning does not freeze the action at a stale version. Recorded
as F-22.

### Setup-script safety for outside contributors

There is no npm `postinstall` or other lifecycle script: `package.json` `scripts` contains only
`build`, `typecheck`, `check-classes`, `lint`, `lint:fix`, `format`, `format:check`, and `verify` —
all explicit, none run automatically by `npm ci`. `npm ci` runs no arbitrary install hook beyond
resolving the vendored tarball against the lockfile hash, so a fresh clone executes no code on install.
The PowerShell scripts run only when a contributor invokes them and target fixed, validated paths:
`deploy-dev.ps1` uses a hard-coded extension identity (`com.alextomas955.rename`), never accepts an
arbitrary destination argument (the `-CoveRepo` parameter selects the build SOURCE only, while the
deploy TARGET is always `COVE_HOME`-resolved), cleans only that one id subdirectory and never the
sibling host-managed `.load-cache`, and refuses to kill a process it cannot cleanly identify as the
Cove backend (`scripts/deploy-dev.ps1` — the fixed-identity block, the clean-only-id-subdir step, and
the best-effort restart). Graded Confirmed-safe.

### Build-artifact contents

The deployable contains only expected files, proven two ways. The `deploy-dev.ps1` strip-verify gate
blocks before copy on any host-assembly leak and asserts both `Rename.dll` and `System.IO.Hashing.dll`
are present; the baseline's publish dry run independently confirmed a clean root-level archive layout
with no host-DLL leak. On a tag the release job publishes the extension, copies `extension.json` and
the committed `index.mjs` into `artifacts/extension`, and zips that directory at root level
(`build.yml` — the Publish, Copy manifest, Copy frontend bundle, Package, and Create GitHub Release
steps). The strip-verify discipline that guarantees this, however, currently lives only in the local
`deploy-dev.ps1`; CI publishes down the same NuGet path (which `Cove.Sdk.targets` strips) but does not
itself assert the published set is clean of host assemblies and absolute paths. The audit-time dry run
is the strongest existing evidence, and a CI artifact-contents assertion would move that discipline
from a local script into the always-on gate. Recorded as F-23.

### Where the surface stands

Most of this surface is already hardened, and the grades reflect it. The reproducible CI and
vendored-SDK build (F-03), the least-privilege workflow permissions and four-ecosystem Dependabot and
CodeQL (F-05, F-08), and the no-host-leak strip-verify discipline are confirmed-strong and are
cross-linked to their resolved baseline findings rather than re-argued. The dependency tree itself is
small and pinned: one first-party BCL runtime dependency on the deployable, one hash-pinned vendored
npm runtime dependency, an exact-pinned lockfile, and AGPL-compatible licenses throughout. Two genuine
new recommendations emerged — SHA-pinning the write-scoped release action (F-22) and adding a CI
artifact-contents assertion to match the local strip-verify gate (F-23) — and an SBOM is noted as a
defensible-but-low-value option rather than a finding. Nothing in the dependency or supply-chain
surface blocks a public release; the two findings are hardening, not corrections.

## Contributor and release policy

This section extends the baseline audit's "Contributor-readiness review" and "Registry publication
review" rather than repeating them; those sections established the contributor on-ramp and the registry
mechanics, and this one reads the repository-operations and release-gate policy on top of them.

One caveat governs everything below and is stated once here: **the repository has no GitHub remote
configured** — `git remote -v` is empty, because the branch rename to a hosted default was deferred to
first push. Every branch-protection, tag-protection, required-status-check, and CODEOWNERS
recommendation that follows is therefore a setting to apply WHEN the repository is pushed. None of them
can be verified against a live remote here; they are forward-looking by necessity, not by choice.

### Fork versus branch

The conventional public-OSS posture fits this repository: outside contributors fork and open a PR from
their fork, while the maintainer works on topic branches with push rights. `CONTRIBUTING.md`'s
pull-request process currently reads "Create a topic branch off `main`" and "Open the PR against
`main`" — which assumes a contributor who can push branches, i.e. the maintainer. That is correct for
the single-author present. When the repository goes public, `CONTRIBUTING.md` should gain a one-line
note that outside contributors fork-and-PR rather than branch directly; this is a documentation
addition, not a workflow change, and is not applied here.

### Default branch and reconciliation

This is already reconciled and need not be re-derived. `main` is canonical (`docs/BRANCHING.md`),
`master` is accepted transitionally, and both `build.yml` and `codeql.yml` trigger on `[main, master]`
so PR CI runs regardless of which branch the repository is first pushed with (cross-links F-06). The
only remaining step is setting the hosted default branch to `main` on first push — a one-time remote
setting, not a repo change.

### Branch protection

Once pushed, protect `main`: require a pull request before merge, require the status checks named below
to pass, and disallow force-pushes. Forward-looking — there is no remote on which to set this yet.

### Required status checks

This is the high-value, repo-specific recommendation. The exact CI jobs a maintainer should mark
required, by their real names in `.github/workflows/build.yml`, are:

- **build** — runs the unit-tier `dotnet test --filter "Tier!=Integration"` and the NuGet-path
  publish.
- **csharp-format** — `dotnet format --verify-no-changes` plus the zero-warning analyzer build.
- **frontend** — `npm ci` (offline vendored SDK), `npm run verify`, `npm run build`, and the
  stale-bundle gate (`git diff --exit-code -- dist/index.mjs`).
- **markdown-lint** — `markdownlint-cli2` over `README.md CHANGELOG.md docs/**/*.md`.
- **version-parity** — `node scripts/check-version-parity.cjs`.

Plus the **CodeQL** `analyze` job from `.github/workflows/codeql.yml`. Marking these six required is the
concrete branch-protection content; each is a job that already exists and runs on PR.

### Merge strategy

Squash-merge is the recommended default, tied to the committed-bundle workflow: the repository commits
the built `dist/index.mjs`, and the stale-bundle gate means a feature branch often carries a separate
"rebuild bundle" commit. Squash-merging collapses those into one commit per change, keeping the bundle
churn out of `main`'s linear history. This is a recommendation, not a mandate — a maintainer who
prefers merge commits loses only history tidiness, not safety.

### Tag protection

The release is gated on `v*` tags: `build.yml`'s `push: tags: ['v*']` trigger fires the Package and
`softprops/action-gh-release@v3` steps under the job's elevated `contents: write` scope. Once pushed,
protect the `v*` tag pattern so only a maintainer can create a release tag, which is what actually
triggers a publish. Forward-looking; cites the tag-gated release job and its write scope.

### CODEOWNERS

There is no CODEOWNERS file, and for a single-author repository one adds little today. It is the cheap
forward step that, once outside contributors arrive, auto-requests the maintainer's review on every PR
and can guard the safety-critical paths — the movers (`DiskMover`, `CrossVolumeMover`,
`CanonicalPathGuard`), the executor (`RenameExecutor`), the revert log (`RevertLog`), `extension.json`,
and the publish/strip configuration (`Rename.csproj`, `Directory.Build.props`, `deploy-dev.ps1`). A
minimal CODEOWNERS scoped to those paths is the recommendation when the repository opens to outside
contributors; one does not exist now and none is claimed to.

### Release-workflow manual approval

The release fires automatically on a `v*` tag push. Because the tag push is itself a maintainer-gated
action, the marginal value of adding a GitHub Environment with a required reviewer is a second
confirmation between tag and publish, not a new gate where none existed. The trade-off: it catches the
case where a tag is pushed by mistake or by automation, at the cost of a manual approval step on every
release. For a single-author repository the tag push is already the human gate, so this is a
take-it-or-leave-it hardening rather than a needed control; it becomes more valuable if tag-push rights
are ever widened.

### CI-only release assets

The release ZIP is built and uploaded ONLY by CI on a tag: the Package step zips `artifacts/extension`
and `softprops/action-gh-release@v3` uploads it (`build.yml` — the Package and Create GitHub Release
steps), with no manual asset-upload path anywhere. This is Confirmed and is the right posture; the
recommendation is simply to keep it CI-only so no hand-built ZIP can ever be attached to a release.

### Release checksum and contents verification

The release does not currently display the ZIP's SHA-256 or prove the asset URL resolves before the
registry PR is opened — that is baseline F-12, still queued, and is cross-linked rather than re-argued.
Cove's registry CI computes the SHA-256 from `downloadUrl` on its side, so the local gap is the
pre-publish proof: building the exact ZIP, confirming its asset URL resolves, and recording its hash
before opening the registry PR. F-23 above adds the complementary artifact-contents assertion in CI.

### Recommended PR-template additions

The current `.github/PULL_REQUEST_TEMPLATE.md` already asks for What-changed, a How-verified checklist
(`dotnet test src/Rename.Tests`, `npm run verify`, "Built and checked in a running Cove"), a Safety
check (DB-and-disk update together, never-overwrite / never-force-unlock, no host-provided assemblies
bundled), and Notes-for-reviewers. Against the fuller ask a public release wants, three specific lines
are missing and should be ADDED to the existing template — not a rewrite of it:

- A **manifest / permission change** checkbox under Safety: "If this changes `extension.json`
  (`permissions`, `minCoveVersion`, `entryDll`, `jsBundle`) or the declared capability surface, the
  change is intentional and documented." The current template covers the publish set but not a manifest
  or permission delta.
- A **frontend-bundle** checkbox under How-verified: "If this touches `src/Rename.Ui`,
  `dist/index.mjs` was rebuilt and committed (the stale-bundle gate passes)." This ties directly to the
  `frontend` job's stale-bundle gate and reminds a contributor before CI catches it.
- An **AI-generated-code disclosure and human-review** line under Notes-for-reviewers: "If any of this
  change was generated with AI assistance, say so; the author has reviewed the safety-critical logic
  (movers, executor, revert log) by hand." This ties to the repository's standing posture that
  safety logic must be human-reviewed and that AI/author narrative is kept out of shipped artifacts.

These are recommended additions to the existing template, presented as concrete lines to add; they are
not applied in this read-only pass. The gaps are graded as a recommendation rather than a finding — the
current template is functional and safe, and the additions are public-release polish, not a defect.

### Where the policy stands

The hard ground is already done: the default-branch reconciliation (F-06) and the least-privilege
workflow permissions (F-08) are shipped and cross-linked, and the CI-only release-asset path is
Confirmed. The new value here is the forward-looking remote-settings list — protect `main`, require the
six named CI jobs, protect the `v*` tag pattern, add a scoped CODEOWNERS when the repo opens up — all of
which apply once the repository is pushed, and the three specific PR-template additions tied to this
repository's manifest, bundle, and safety-review posture. The checksum gap stays tracked as F-12; no
new finding is filed by this section, because each remaining item is either a forward-looking remote
setting or a documentation addition rather than a present defect.

## Staged testing policy: per-PR, nightly, and before-release gates

This section folds into the addendum's CI/CD and testing guidance and extends the baseline audit's
"Testing strategy" and "CI/CD recommendations" rather than repeating their lists. Its new value is the
STAGED SPLIT: which checks belong on every PR, which belong on a heavier nightly or manual run, and
which run once per release. Each tier is tied to this repository's real CI jobs and its devcontainer,
and each gate is graded as already-in-CI, recommended-add, or a cross-link to a tracked finding.

### Tier 1 — every-PR gates (fast, on every pull request)

This tier is largely DONE: it is what `build.yml` and `codeql.yml` already run on each PR. The only
addition worth recommending is a publish-artifact-contents assertion, which is filed as F-23 above.

| Gate | What it runs | Status |
| --- | --- | --- |
| Unit-tier tests | `dotnet test --filter "Tier!=Integration"` (`build` job) | Already in CI (cross-links F-03) |
| C# style + analyzer | `dotnet format --verify-no-changes` + the zero-warning analyzer build (`csharp-format` job) | Already in CI |
| Frontend verify + build | `npm ci`, `npm run verify`, `npm run build` (`frontend` job) | Already in CI |
| Stale-bundle gate | `git diff --exit-code -- dist/index.mjs` (`frontend` job) | Already in CI (cross-links F-03) |
| Markdown lint | `markdownlint-cli2` over README/CHANGELOG/docs (`markdown-lint` job) | Already in CI |
| Version parity | `node scripts/check-version-parity.cjs` (`version-parity` job) | Already in CI (cross-links F-05) |
| CodeQL | `csharp` + `javascript-typescript` analysis on PR (`codeql.yml`) | Already in CI (cross-links F-08) |
| Artifact-contents assertion | Enumerate the published `*.dll` set, fail on any host-provided assembly, assert `Rename.dll` + `System.IO.Hashing.dll` present, grep for absolute paths | Recommended add (F-23) — ports the local `deploy-dev.ps1` strip-verify gate into CI |

### Tier 2 — nightly or manual gates (heavier, not on every PR)

This tier is mostly recommended-add. It covers the checks that need an environment a bare PR runner does
not have — a Cove checkout for the integration tier, a clean container, and non-Windows hosts.

| Gate | What it runs | Status |
| --- | --- | --- |
| Full test suite incl. integration tier | The integration tier needs a sibling Cove checkout, so `build.yml`'s strategy comment makes the full suite the LOCAL gate, not a PR gate | Already local-only by design; recommend a scheduled/dispatch workflow that provisions a Cove checkout |
| Devcontainer / cross-platform smoke | `.devcontainer/verify.sh` on a clean `mcr.microsoft.com/devcontainers/dotnet:1-10.0` container plus the Node 24 feature; a Linux/macOS matrix leg | Recommended add — the devcontainer is the existing vehicle; the Linux leg is already proven on `ubuntu-latest` per PR, macOS is not |
| Case-only rename | Reproduce `movie.mkv` → `Movie.mkv` on a case-insensitive and a case-sensitive volume | Recommended add (cross-links F-16) |
| Unicode NFC/NFD comparison | Pair an NFC and an NFD spelling of the same accented name and assert the intended behavior | Recommended add (cross-links F-17) |
| Cross-volume hardlink severing | Hardlink a source, move it cross-volume, assert the documented behavior | Recommended add (cross-links F-18) |
| Bind-mount / UNC classification | Exercise `VolumeClassifier` against bind-mount and UNC paths in a Linux/Docker and a network environment | Recommended add (the matrix bind-mount/UNC rows above) |

A scheduled or `workflow_dispatch` workflow is the recommended home for this tier — it runs the slow,
environment-dependent checks off the PR critical path while still running them regularly.

### Tier 3 — before-release gates (run once per release, the most thorough)

This tier is the still-queued registry/backup release work plus the manual browser validation the
project requires. It is where the "proven by driving the Cove UI in a browser" owner requirement lives,
because that is the one step automation cannot stand in for.

| Gate | What it runs | Status |
| --- | --- | --- |
| Build the exact release ZIP | Build the `com.alextomas955.rename-<version>.zip` the tag-gated CI produces | Already CI-built on a `v*` tag; the before-release step is to verify it, not rebuild by hand |
| Clean-Cove install | Install that exact ZIP into a CLEAN Cove instance | Recommended add (cross-links F-13) |
| Full lifecycle walk | Extension discovery, settings-tab load, settings save/load, preview, rename, undo, restart/reload persistence, disable / re-enable | Recommended add — the manual browser validation (cross-links F-13); restart-persistence and disable/re-enable are the queued live checks the compatibility matrix flagged |
| Log-exposure check | Confirm the full-path logging is the documented, expected behavior | Already documented (cross-links F-11) |
| Backup guidance present | Confirm the database/media backup guidance for first large runs is in the user docs | Recommended add (cross-links F-13, and F-19's undo-history-on-clear note) |
| Release-asset URL resolves | Confirm the GitHub release ZIP's download URL resolves before opening the registry PR | Recommended add (cross-links F-12) |
| ZIP SHA-256 verified | Record the ZIP's SHA-256 and confirm it matches what Cove's registry CI computes from `downloadUrl` | Recommended add (cross-links F-12) |
| Registry metadata draft | Prepare `extensions/com.alextomas955.rename.json` with `versions[]` (version, changelog, `downloadUrl`, `minCoveVersion`) | Recommended add (cross-links F-12 and the baseline's Registry publication review) |

### Where the staged policy stands

The per-PR tier is largely done — six CI jobs plus CodeQL already gate every pull request, and the one
addition is the artifact-contents assertion filed as F-23. The nightly tier is mostly recommended-add:
the integration tier is local-only by design and the cross-platform reproduction items (F-16, F-17,
F-18, and the bind-mount/UNC matrix rows) need an environment a single Windows box does not have, so a
scheduled workflow is the recommended vehicle. The before-release tier is the still-queued
registry-publication and backup work (F-12, F-13) plus the manual browser validation the project
requires — the codification of a release gate that does not yet exist as a checklist. This section files
no new finding: the per-PR addition is already F-23, and every nightly and before-release item
cross-links an existing finding (F-11, F-12, F-13, F-16, F-17, F-18) rather than manufacturing a new
one.

No source, CI, test, manifest, or behavior change was made while producing these three sections; the
only file written was this addendum. The dependency and supply-chain surface, the contributor and
release policy, and the staged testing policy are recorded as evidence-cited grades — most of the
ground already hardened and cross-linked to its resolved baseline finding, with two new hardening
recommendations (F-22, F-23) and a set of forward-looking remote-settings and release-gate
recommendations tied to this repository's real current state.

## Over-engineering and simplification

This section grades the backend source for the smells that signal over-building — abstractions with
one real implementation, layers named for a pattern rather than a job, forwarding shims, duplicated
data shapes, speculative extension points, and defensive scaffolding that no real input reaches —
and classifies each against the code that is actually there. The verdicts are simplify-now,
simplify-later, leave-as-is, and investigate-further.

The frame matters because the temptation in a mature, tidy codebase is to manufacture work. This
extension is at v1.8 with roughly thirty source files in clean domain folders, and most of its
abstractions are load-bearing: the rename surface is the one part of a media library where a wrong
move loses a file, so a seam that exists to keep the disk logic testable, or a guard that fails
closed, earns its place. A clean abstraction that earns its place is leave-as-is, not a finding. A
rename that would only churn the code — moving a class for tidiness, collapsing a well-named helper,
renaming for taste — is explicitly out of scope here and is not recommended anywhere in this section.
The bar for a simplify verdict is a concrete, evidence-cited cost the abstraction imposes without a
matching benefit, not an aesthetic preference.

| Item | What it is (evidence) | Classification | Rationale |
| --- | --- | --- | --- |
| `IRenameDataPort` single production implementation | One interface (`src/Rename/Planner/IRenameDataPort.cs`) with a single production implementation, `CoveRenameDataPort` (`src/Rename/Execution/CoveRenameDataPort.cs`), plus the test fake `FakeRenameDataPort` and the narrower `CollisionBlindDataPort`. | leave-as-is | A single-impl interface is the classic over-engineering smell, but this one is the seam that lets the planner, collision, gating, and suffix logic be unit-tested with zero database, and it is the type boundary that keeps the production `Rename.csproj` from taking a runtime dependency on `Cove.Core` entities (the port maps live `Video`/`VideoFile` graphs into Rename-owned DTOs). The interface doc states both reasons; the test fakes consume it. Removing it would couple the planner to EF and `Cove.Core`. The seam earns its place. |
| The Rename-owned DTO records (`RenameEntity`, `RenameFile`, `RenamePerformer`, `RenameCaption`, `RenameFileMutation`) | Plain records in `IRenameDataPort.cs` that mirror Cove entity fields the port reads. | leave-as-is | This looks like DTO duplication of `Cove.Core` entities, but it is the deliberate consequence of the type boundary above: the production assembly does not reference `Cove.Core`, so it cannot speak in `Video`/`Image`/`Audio` types. The records carry only the fields the projector and mover need, with nullability used to drive entity-type-aware token degradation. They are the boundary's vocabulary, not redundant mirrors. |
| The mover trio (`DiskMover`, `CrossVolumeMover`) plus `VolumeClassifier` | Two movers selected by a pure same-vs-cross volume decision (`src/Rename/Execution/VolumeClassifier.cs`, `DiskMover.cs`, `CrossVolumeMover.cs`). | leave-as-is | Same-volume and cross-volume moves are genuinely different operations: `File.Move` is atomic and preserves hardlinks within a volume; a cross-volume move must be copy → verify → fsync → promote → delete-source-last to survive an interruption. The split reflects a real filesystem distinction, not an invented one, and the classifier is pure string math shared by both the executor and (later) the planner so they agree on "same volume". |
| `CanonicalPathGuard` and `FreeSpaceGuard` | Two guard classes invoked on the destructive path (`src/Rename/Execution/CanonicalPathGuard.cs`, `FreeSpaceGuard.cs`). | leave-as-is | Neither is defensive scaffolding for an impossible case: the canonical-path guard resolves symlinks/junctions and fails closed against the configured allowlist (a real routing safety boundary), and the free-space guard sums projected bytes per destination volume to refuse rather than fill a disk. `FreeSpaceGuard` injects its only disk touch as a `Func` probe so it is fully unit-testable with no second drive, and it reuses `VolumeClassifier` rather than re-rolling a root compare. Both protect a real, reachable failure. |
| `RevertLog` single-writer serialization | An append-only newline-delimited blob in the extension store guarded by a process-wide `SemaphoreSlim` keyed on the store key (`src/Rename/Execution/RevertLog.cs`). | leave-as-is | The serialization is not speculative concurrency defense: the blob is a read-modify-write KV value, two intra-batch workers or two jobs over the same key would silently drop rows without it, and Cove disables EF's thread-safety checks so the corruption would be silent. The concurrency tests reproduce the dropped-row failure with the gate removed. The mechanism is matched to a demonstrated hazard. |
| `RenameFileKind` includes `Gallery`; entity-type-aware token degradation | The kind enum lists `Video`/`Image`/`Audio`/`Gallery`, and the projector omits tokens a kind does not carry (`IRenameDataPort.cs`, `src/Rename/Engine/MetadataProjector.cs`). | leave-as-is | `Gallery` is the one speculative entry — it is listed "for completeness" and is not renamed today — but it is a single enum member with a documented not-yet status, not a built-out code path: `Rename.TryParseKind` returns false for everything but video/image/audio, so no dead gallery branch ships. The per-kind degradation itself is not premature future-proofing; image and audio are live, supported kinds (their permission paths are tested), and the nullable-field omission is how a heightless audio file cleanly drops `$resolution`. The cost of the lone unused enum name is a nit, not worth a churn edit. |
| The `Rename` partial-class split (`Rename.cs`, `Rename.Api.cs`, `Rename.Events.cs`, `Rename.Logging.cs`) | One `FullExtensionBase` subclass split across four files by concern (`src/Rename/Rename*.cs`). | leave-as-is | A partial class is not an abstraction layer; it is a file-organization choice for one class that contributes API, events, jobs, state, and logging. The split is by capability surface, the largest part (`Rename.Api.cs`, the endpoint handlers) is the one a contributor edits most, and the source-generated `LoggerMessage` methods live with the field they bind. This is readability, not over-building. |
| `RenameJob` static encode/decode helper | A static class holding the job id constant and the parameter-map (de)serialization (`src/Rename/Jobs/RenameJob.cs`, 61 lines). | leave-as-is | This is not a `Manager`/`Service`/`Provider` god-object — the name `Job` reflects that it owns the single `rename-batch` job's id and the pure round-trip of its parameters through the host's string-only map. It holds no logic beyond JSON encode/decode and is tolerant of bad input (a malformed parameter map decodes to a clean no-op). It is the minimal seam between the host's job contract and the batch core. |
| Naming/layer smells (`Manager` / `Service` / `Provider` / `Helper` / `Utility` / `Processor`) and forwarding shims | A scan of the source tree for these layer names. | leave-as-is | The tree carries none of these generic layer names on its own types: classes are named for what they do (`Sanitizer`, `LengthReducer`, `DestinationResolver`, `RenameExecutor`, `UndoReplayer`). The only `Service`/`Provider` identifiers are host interfaces the extension consumes (`IServiceScopeFactory`, `IJobService`, `ICurrentPrincipalAccessor`), not invented wrappers. No pass-through forwarding class was found — each class does real work. |
| Builders / factories / fixtures | Production code carries no builder or factory classes; the test tree has shared seeds and a `LongTemplateFixture` (`src/Rename.Tests/ExecutorTestSeed.cs`, `LongTemplateFixture.cs`). | leave-as-is | There is no over-built factory layer in production. The test-side seeds and the one fixture are graded in the test-quality section below; they are earning their place there, not adding indirection here. |

The grading resolves to leave-as-is for every reviewed item: the abstractions that look like classic
over-engineering smells — the single-implementation port, the Rename-owned DTOs, the two movers, the
two guards, the serialized revert log — are each matched to a concrete, evidence-cited reason rooted
in the one thing this extension cannot get wrong, which is losing track of a file. The only
candidate that even approaches a finding is the unused `Gallery` enum member, and it is a documented
single-line placeholder with no dead code path behind it, so a churn edit to remove it would cost
more than it saves. No item is graded simplify-now or simplify-later, and no investigate-further item
arises here (the investigate-further items in this addendum are the cross-platform filesystem
questions already filed as F-16 through F-18, which are correctness questions, not over-engineering
ones). This section therefore raises no new finding. Consistent with the repository's standing
policy, no aesthetic-only or churn-only refactor is recommended: a clean abstraction that earns its
place is left exactly as it is.

## Naming, file, and folder structure

This section reviews the names across the source tree for the patterns worth flagging — misleading
names, too-generic names, names that read as machine-generated, names inconsistent with Cove or with
the extension's own manifest identity, names that leak how a thing is built rather than what it does,
and stale scaffold names left over from an extension template. It has two parts: a keep-list of the
names that read clearly and should not change, and a high-value-only proposed rename/move map. The
map is a proposal, not an applied change, and it is deliberately not a broad reorganization.

The manifest identity is coherent and is the anchor for the naming review. `src/Rename/extension.json`
declares `id` `com.alextomas955.rename`, `entryDll` `Rename.dll`, and `name` `Rename`; the C# entry
class `Rename` (`src/Rename/Rename.cs`) overrides `Id` to the same `com.alextomas955.rename` and the
endpoints mount under `/api/extensions/com.alextomas955.rename`. The built assembly is `Rename.dll`,
matching `entryDll`. The id, the assembly name, the namespace root (`Rename`), and the endpoint base
all agree; there is no manifest-id / assembly-name drift to flag. (The version-prose drift that does
exist is a separate concern already filed as F-21; it is about which Cove version the prose cites,
not about the extension's own name.)

### Names that should not change

The domain-folder structure is good and is kept. The six folders — `Engine/`, `Execution/`,
`Planner/`, `Options/`, `Api/`, `Jobs/` — split the code along the real seams of the work: pure
naming logic, disk/database mutation, dry-run planning, persisted settings, the HTTP surface, and the
host job contract. A contributor can predict which folder a change belongs in from the folder name
alone, and the test tree mirrors it. This is the structure to preserve, not reorganize.

- `Engine/` (`Sanitizer`, `LengthReducer`, `Tokenizer`, `TemplateEngine`, `FieldRewriter`,
  `MultiValue`, `ResolutionLabel`, `RenameResult`) — each name states the pure transform it performs.
  `Sanitizer` cleans a segment, `LengthReducer` enforces a path-length budget, `TemplateEngine`
  renders a template, `FieldRewriter` does per-field value rewrites. They read as what they do.
- `Execution/` (`DiskMover`, `CrossVolumeMover`, `VolumeClassifier`, `CanonicalPathGuard`,
  `FreeSpaceGuard`, `RenameExecutor`, `UndoReplayer`, `RevertLog`, `CoveRenameDataPort`) — the mover
  pair names the same-vs-cross distinction plainly; the two `*Guard` names state what they protect;
  `UndoReplayer` and `RevertLog` name the undo mechanism in domain terms. `CoveRenameDataPort` is the
  one name that carries its host (`Cove`) and its role (`DataPort`), which is correct here because it
  is precisely the Cove-backed implementation of the data-port seam.
- `Planner/` (`RenamePlanner`, `DestinationResolver`, `MetadataProjector`, `BatchPreview`,
  `RouteResult`, `RenamePlan`, `IRenameDataPort`) — `MetadataProjector` (projects entity metadata to
  tokens) and `DestinationResolver` (resolves an entity to a destination route) both name a real
  transform; `BatchPreview` names the blast-radius summary the dry-run produces.
- The `Api/` request/result records (`RenameRequest`, `PreviewResponse`, `PreviewSampleRequest`,
  `PreviewSampleResult`, `UndoResult`, `SampleTokenSets`) — each name pairs an endpoint with its wire
  shape. `PreviewSampleRequest`/`PreviewSampleResult` clearly belong to the live-preview sample
  endpoint, distinct from the selection-based `PreviewResponse`.
- The manifest id `com.alextomas955.rename`, the assembly `Rename.dll`, and the `rename-batch` job id
  — coherent with each other and with the repository, as established above.

No template-scaffold residue was found in the type names: there is no leftover `Class1`,
`MyExtension`, `Plugin1`, or `TODO`-named type. The names are domain names, not generated ones.

### Proposed rename/move map (high-value only)

No name in the source tree clears the high-value bar for a rename or a move. Every type name read
clearly against its job during this review, the manifest identity is coherent, the folder structure
is sound, and there is no misleading, stale-scaffold, or implementation-leaking name whose correction
would repay the churn. The map is therefore intentionally empty.

The one naming-adjacent defect this review did surface is not a type or file rename at all — it is
planning-process vocabulary still embedded in five test-source doc comments (`research §Architectural
Responsibility Map`, `Phase 24 / VER-01`, `WR-03 EXECUTOR-SEAM`). That is a comment-wording
coherence gap, filed as F-24 in the findings table, and its fix is a reword in place, not a rename or
a move. No structural rename/move is proposed.

## Test quality and brittleness

This section grades the test suite — roughly eighty-six test files mirroring the source folders —
against the quality axes that separate durable tests from brittle ones: whether tests assert
behavior or internal calls, whether faked collaborators are checked by their arguments and resulting
state rather than a bare "was called", whether test names read as behavior statements, whether the
shared fakes and fixtures earn their indirection, whether any test pins a past bug, and whether the
coverage reaches the surfaces that matter most — destructive edge cases, the permission matrix,
concurrency boundaries, preview purity, and a clean package. It also asks the practical question of
whether a contributor would know where to add a new test.

This section recommends only. No test was added, edited, deleted, or run-to-modify while producing
it; the only file written by this work is this addendum. Where a recommended test was already implied
by an earlier threat-model finding, this section cross-links that finding rather than inventing a
duplicate.

| Axis / area | Assessment (evidence) | Verdict |
| --- | --- | --- |
| Behavior vs. implementation focus | Tests assert observable outcomes, not internal call sequences. `RollbackTests.SaveFailsAfterMove_FileRestoredToOldPath_DbRowUnchanged` (`src/Rename.Tests/Execution/RollbackTests.cs`) forces a save throw after a real disk move and asserts the file is back at its old path, the DB row still holds the old basename, and no revert-log row was written — the disk/DB end state, not the calls that produced it. `PreviewPurityTests` asserts the planned status and path AND that no folder was created and nothing was saved. | leave-as-is |
| Mock-assertion vs. outcome | The suite uses no mocking framework (no Moq / NSubstitute; a scan for `.Verify(`, `Times.Once`, `Mock<`, `.Received(` finds nothing). Collaborators are hand-rolled fakes that record arguments and resulting state, and tests assert those. `EndpointPermissionTests` uses a `RecordingJobService` and asserts the enqueued job's type AND its `exclusive` flag (`Assert.True(exclusive)`), and on a deny path asserts `Assert.Empty(jobs.Enqueued)` — the call arguments and the not-enqueued outcome, never a bare "was called". `FakeRenameDataPort` records `SaveCalls` and `CreatedFolderPaths` so a purity test can assert they stay empty. | leave-as-is |
| Test-name readability | Names read as behavior statements: `RenameEnqueue_WithoutVideosWrite_Returns403_AndDoesNotEnqueue`, `Preview_MoveToMissingFolder_CreatesNoFolderRow`, `InterleavedAppends_OneInstance_BlobHasExactlyNWellFormedRows`, `SaveFailsAfterMove_SidecarAlsoRestored`. The condition-and-expected-outcome shape is consistent across the files sampled. | leave-as-is |
| Fixture / fake utility | The shared fakes earn their place rather than adding indirection: `FakeRenameDataPort` is the DB-free seam fake the purity/collision/gating tests need; `CollisionBlindDataPort` deliberately bypasses the pre-check so the rollback test can force the unique-index throw; `ConcurrentFakeStore` is the thread-safe store the concurrency tests need to isolate the `RevertLog` gate from the store. `ExecutorTestSeed`, `CoveContextFactory` (SQLite, not EF-InMemory, chosen so the unique index and rollback actually fire), `TempDir`, and `SubstDrive` each back a specific need stated in their own doc. None is a speculative builder. | leave-as-is |
| Bug-encoding tests | Several tests pin a fixed bug so it cannot regress: `PreviewPurityTests` is the F-01 preview-mutation regression; the F-02 entity-permission split is pinned by `EndpointPermissionTests` (`PreviewAsync_ImageRequest_RequiresImagesRead_NotVideosRead`, the audio-write case); F-04 exclusivity is pinned by the `Assert.True(exclusive)` assertions. These are valuable regression anchors, not brittle over-specification. | leave-as-is |
| Coverage — destructive edge cases | The destructive surface is well covered: `RollbackTests`, `LockedFileTests`, `CollisionTests`, `CrossVolumeMoverTests`, `CrossVolumeVerifyFailTests`, the `CanonicalGuard*Tests` set (symlink, junction, 8.3, prefix), and `SidecarTests`. The known gaps are the cross-platform ones the threat model already filed: a Windows reserved-device-name case (F-14), an untracked same-stem sidecar case (F-15), a case-only rename on a case-insensitive volume (F-16), an NFC/NFD comparison (F-17), and a cross-volume hardlink case (F-18). | Covered, with the gaps cross-linked to F-14 through F-18 |
| Coverage — permission matrix | Strong and explicit: `EndpointPermissionTests` proves the deny path returns 403 AND does not enqueue for video, image, and audio, that an image request needs images.read/write (not videos), that audio needs audios.write, and that undo returns 403 before any scope or disk/DB touch. `EntityIdsCapTests` covers the id-count bound. | leave-as-is (covered) |
| Coverage — concurrency boundaries | The `Concurrency/` folder covers the parallel batch (`ParallelBatchTests`), parallel folder creation (`ParallelFolderCreationTests`), the per-worker scope (`PerWorkerScopeTests`), and the single-writer revert-log invariant under two-instance contention (`RevertLogConcurrencyTests`, which asserts the parsed-blob row count and per-row well-formedness, the right invariant for a silent-corruption bug). | leave-as-is (covered) |
| Coverage — preview purity | `PreviewPurityTests` plus `Planner/PreviewWholeBatchTests`, `Preview/BlastRadiusTests`, and the `Api/PreviewEndpointTests` / `PreviewRoutingTests` / `PreviewSampleEndpointTests` set assert the planner mutates nothing and the routed/sample previews stay read-only. | leave-as-is (covered) |
| Coverage — clean-package behavior | The published-package cleanliness (no host-provided assembly leak, expected DLLs present) is NOT a unit test and is not in this suite — by design it lives in `scripts/deploy-dev.ps1`'s strip-verify, and the absence of an always-on CI assertion for it is already filed as F-23. `ExtensionManifestFileTests` does cover the manifest file's own shape. | Gap cross-linked to F-23 (CI assertion), manifest shape covered |
| Where to add a new test | The test tree mirrors the source folders (`Api/`, `Execution/`, `Jobs/`, `Options/`, `Planner/`) and adds dimension folders (`Concurrency/`, `Events/`, `Preview/`, `TestSupport/`), so a contributor can predict the location of a new test from the code under test. The one asymmetry is the `Engine/` source folder: its tests (`SanitizerTests`, `TemplateEngineTests`, `TokenizerTests`, `MultiValueTests`, `LengthReducerTests`, `FieldRewriterTests`) sit at the test-project root rather than an `Engine/` subfolder. This is a minor, non-blocking inconsistency, not a defect. | leave-as-is (a churn-only move; not recommended) |

The test suite grades as healthy on every axis: behavior-focused assertions, hand-rolled fakes that
check arguments and end state instead of a bare "was called", behavior-statement names, fixtures that
each back a stated need, and a set of bug-encoding regression tests for the shipped baseline fixes.
Coverage reaches the surfaces that matter — the destructive moves, the permission matrix, the
concurrency boundaries, and preview purity are all exercised — and the remaining coverage gaps are
exactly the ones earlier sections already filed: the cross-platform filesystem cases (F-14 through
F-18) and the always-on clean-package CI assertion (F-23). The single test-specific issue this
review adds is not a coverage or brittleness gap but the planning-process vocabulary still present in
five test-source doc comments, filed as F-24. No churn-only reorganization is recommended — in
particular the `Engine/` test-folder asymmetry is left as-is — and, to restate the contract, this
section adds no tests and changes no test code; it records recommendations only.

## UI safety and accessibility

This section grades the frontend as the safety layer of a destructive tool. The backend keeps a file
from ever being lost; the frontend is what lets a user understand what is about to happen, confirm it
in proportion to its blast radius, see what actually happened, and reverse it. It reviews each safety
and accessibility dimension against the actual component code, citing a concrete `component:line` for
every assessment, and marks a behavior `investigate-further` only where confirming it would require
driving the running UI in a browser — which this read-only pass does not do. Where the behavior is
correct it is marked confirmed-good and left as-is; only a genuine, evidence-anchored gap becomes a
new finding.

One fact frames the whole review. The reality check that the dialog primitive
(`src/Rename.Ui/src/dialog.tsx`) is already a proper modal — `role="dialog"`, `aria-modal="true"`,
`aria-labelledby`/`aria-describedby`, a Tab focus trap, focus-on-open, return-focus-on-close, and
Esc-to-cancel suppressed while an operation is in flight — holds. The "obvious" accessibility gaps a
React component review usually finds (a div pretending to be a modal, no focus management, no keyboard
escape) are not present here. The two findings this section raises are not accessibility gaps; they
are a confirmation-proportionality inconsistency between the two rename entry points and an
undo-durability message the UI does not state.

There are two rename entry points, and the distinction matters for several rows below. The in-list
**"Rename selected" bulk action** (`src/Rename.Ui/src/renameSelected.ts`) runs through the host's
action dispatch, which exposes no React-dialog API, so it confirms through the native blocking
`window.confirm` whose text is built by the pure `buildConfirmSummary` (`src/Rename.Ui/src/preview.ts`
lines 103–195). The settings-panel **"Review & Rename"** box (`ReviewSection` in
`RenameSettingsPanel.tsx`) opens the React `ReviewDialog` (`src/Rename.Ui/src/ReviewDialog.tsx`). Both
call the same `/preview` and `/rename`, so the planned names are one source of truth — but they render
the confirmation differently, which is the subject of F-25.

| Dimension | Assessment (component:line evidence) | Verdict |
| --- | --- | --- |
| Preview clarity | The live preview renders each sample as a struck-through old name above the new name with a `Renamed →` lead-in, plus per-sample advisory flags (empty, sanitized, length-reduced with the dropped fields named, gating-skip) — `src/Rename.Ui/src/PreviewCard.tsx` lines 38–68, flag copy lines 21–36. The panel's preview pane is debounced and labelled "Old → new for sample items, before anything touches disk" (`RenameSettingsPanel.tsx` lines 917–919). | confirmed-good |
| Old ↔ new comparison | The review table shows `Original` and `New name` columns side by side, the new cell reads "— will be skipped" for a skip status, and each row carries its warning badges (`ReviewDialog.tsx` lines 134–164). The bulk-action confirm lists up to five `old → new` basename examples with "… and R more." (`preview.ts` lines 167–173). The diff is unambiguous in both paths. | confirmed-good |
| Destructive-action confirmation | The rename is gated behind an explicit confirm in both paths: the React `ReviewDialog` with a `Confirm & rename {n}` button (`ReviewDialog.tsx` lines 191–199), and the bulk action's `window.confirm` that cancels the `/rename` on a No (`renameSelected.ts` lines 42–48). Nothing reaches `/rename` without a confirm. | confirmed-good |
| Confirmation proportional to blast radius | The bulk-action confirm scales its call-to-action with the backend `ConfirmLevel` and adds a per-volume "N items (X GB) move from A to B" line for a cross-drive batch (`preview.ts` lines 145–185). The panel's `ReviewDialog` consumes only the per-item array from the same `/preview` and shows a flat count-and-table regardless of the move's size or cross-drive span (`ReviewDialog.tsx` lines 65–83, 198) — it never reads `summary`/`confirmLevel`/`volumePairs`. | gap (F-25) |
| Large-batch confirmation threshold | The bulk-action wording escalates by `ConfirmLevel` rather than a hard item-count threshold, and a Heavy cross-drive batch gets the strongest "LARGE cross-drive move … files will be COPIED" copy (`preview.ts` lines 178–185). The panel path does not escalate at all (the F-25 gap). Whether the `Light`/`Standard`/`Heavy` thresholds the backend assigns feel right to a user is a runtime judgement not settled by reading the wording. | confirmed-good on the bulk path; investigate-further on the threshold tuning |
| Undo visibility and comprehensibility | The Undo section is always rendered in the panel; it reads `/last-batch` on mount and after each rename, shows "Last rename: N items renamed · {relative time}" when a batch is undoable, and states plainly "This moves every file in that batch back to its original name. It can't be undone again." (`UndoSection.tsx` lines 162–199, 24–30). When nothing is undoable it says "No rename to undo." (lines 202–204). | confirmed-good |
| Partial-failure display | Undo reports three honest outcomes — full success, partial ("k file(s) couldn't be moved back ({reason}). The rest were restored."), and total failure ("Nothing was changed.") — derived from the response's `failed`/`skipped` arrays (`UndoSection.tsx` lines 126–141). The review table marks every skip/failed status per row with a reason badge (`ReviewDialog.tsx` lines 148–161; `WarningBadge.tsx` lines 35–60), and the bulk confirm breaks skips into gated/collision/locked sub-counts (`preview.ts` lines 122–135). Outcomes are shown per item, not as a bare success toast. | confirmed-good |
| Large-preview usability | The review table is height-capped and scrolls (`max-h-96 overflow-y-auto`) with a sticky header (`ReviewDialog.tsx` lines 134–146); the live preview renders a fixed small sample set, not the whole library (`RenameSettingsPanel.tsx` lines 930–934). There is no virtualization, so a several-thousand-item review renders every row into the scroll container. Whether that is a usability problem at real library sizes is a runtime-performance question this static read cannot settle. | confirmed-good for the capped/scrolled layout; investigate-further on very large lists |
| Loading / disabled / cancel / empty states | Loading: spinners with text ("Loading preview…", "Loading settings…", "Rendering preview…") at `ReviewDialog.tsx` lines 121–125, `RenameSettingsPanel.tsx` lines 509–516 and 924–928. Disabled: the confirm button is disabled while renaming, while the preview is still loading, and when nothing will rename (`ReviewDialog.tsx` lines 191–195); Save is disabled when there is nothing to save or a save is in flight (`RenameSettingsPanel.tsx` line 589). Cancel: both dialogs have a Cancel button disabled while the operation is pending, plus Esc and scrim-click (`ReviewDialog.tsx` lines 183–189; `dialog.tsx` lines 35–37, 50–55, 82). Empty: "Nothing to rename — adjust your settings or selection." and "Enter at least one item ID to preview." (`ReviewDialog.tsx` lines 166–170; `RenameSettingsPanel.tsx` lines 287–289). | confirmed-good |
| Actionable errors | Error copy tells the user what state they are in and what to do: "Couldn't load the preview — {detail}. Close and try again.", "Couldn't rename — {detail}. Nothing was changed; you can try again.", "Couldn't save settings — {detail}. Your changes are still here; try Save again.", and a Retry button on a failed settings/last-batch load (`ReviewDialog.tsx` lines 117–119, 174–176; `RenameSettingsPanel.tsx` lines 596–599; `UndoSection.tsx` lines 175–182). Each names whether anything changed. | confirmed-good |
| Keyboard accessibility | Controls are native `button`/`input`/`select`/`checkbox`/`label` elements, so they are reachable and operable by keyboard by default (`primitives.tsx` throughout; the token list-input adds tokens on Enter at lines 489–493). The icon-only reorder/remove controls are real buttons (`primitives.tsx` lines 447–479). The dialog traps Tab within the panel (`dialog.tsx` lines 56–70). No custom-div control bypasses the keyboard. | confirmed-good |
| Focus behavior | The dialog focuses the first focusable element on open and restores focus to the opener on close, and traps Tab/Shift-Tab at the panel edges (`dialog.tsx` lines 39–46, 56–70). Esc cancels unless an operation is pending (lines 35–37, 50–55). This is the focus management a modal owes a keyboard user. | confirmed-good |
| Screen-reader / ARIA labeling | The dialog carries `role="dialog"`, `aria-modal`, and `aria-labelledby`/`aria-describedby` wired to the title and the summary line (`dialog.tsx` lines 83–90; `ReviewDialog.tsx` lines 113, 128; `UndoSection.tsx` lines 223, 226). Icon-only buttons carry `aria-label` ("Move {v} up/down", "Remove {v}") (`primitives.tsx` lines 449, 458, 472). The inline template/token advisories are live regions (`role="status" aria-live="polite"`, `RenameSettingsPanel.tsx` lines 172, 208). Warning badges never rely on color alone — amber/red pills lead with an `AlertTriangle` glyph and always carry text (`WarningBadge.tsx` lines 62–71, documented lines 4–6). | confirmed-good |
| Dangerous-action copy | The confirm buttons name the consequence and the count: "Confirm & rename {n}" (`ReviewDialog.tsx` line 198), "Undo {count} rename(s)" on a red destructive button (`UndoSection.tsx` lines 241–249), and the bulk confirm's escalating "files will be COPIED across drives … Click OK only if you are sure" for a Heavy batch (`preview.ts` lines 181–182). The copy states what will happen, not a generic "OK?". | confirmed-good |
| Human-vs-AI tone of UI strings | The strings read as a person wrote them — "Changed your mind?" is absent but the register is plain and direct ("Nothing was changed; you can try again.", "skips un-curated items so they don't get junk names.", "No rename to undo."). No "comprehensive", "seamless", "robust", or list-of-three filler; no AI-assistant register. | confirmed-good |
| Honest backup / undo-limitation messaging | The UI promises reversibility — "You can undo this afterwards." (`preview.ts` lines 181–185) and "It can't be undone again." (`UndoSection.tsx` lines 165–168, 226–229) — but states nowhere that the undo record lives in the extension's stored data and is lost if that data is cleared (the F-19 durability limit), nor recommends a backup before a first large run (the still-queued F-13). The message offers more recoverability than the storage model guarantees. | gap (F-26, cross-links F-19 and F-13) |

The frontend grades as a strong safety layer on almost every axis: the preview makes the old → new
change unambiguous, both rename paths gate behind an explicit confirm, the dialog primitive is a
properly accessible modal with focus management and keyboard escape, errors tell the user whether
anything changed, and undo shows honest per-item partial-failure outcomes. The accessibility floor
that a component review usually has to raise is already met. Two genuine gaps remain, and neither is
an accessibility defect. The settings-panel Review dialog does not apply the blast-radius
`ConfirmLevel` escalation that the in-list bulk action does, so the same large cross-drive batch is
confirmed with different weight depending on which entry point the user takes (F-25). And the
interface promises an undo it cannot always honor — the undo record is lost if the extension's data is
cleared — without saying so (F-26, the UI/docs face of F-19). Both are recorded as findings with
concrete `component:line` anchors; both are copy/wiring changes this audit records rather than applies.
Every other dimension is confirmed-good and left as-is, with the large-list rendering and the
`ConfirmLevel` threshold tuning marked investigate-further because each is a runtime judgement a static
read cannot settle.

## Documentation quality and human tone

This section reviews the public-facing prose — the README, the contributor and release docs, the
changelog, the pull request template and the issue templates under `.github/`, the script and workflow
comments, and the agent context files — against the standard the repository sets for itself: durable maintainer prose, free of
internal planning vocabulary, local-machine paths, requirement or milestone identifiers, AI-assistant
register, and over-formal generated filler, and honest about what the tool does and does not
guarantee. It extends, and does not repeat, the baseline audit's AI/process-residue review (baseline
[F-10](./2026-06-29-read-only-repo-audit.md), recorded Resolved in v1.7): that finding swept the bulk
of the stale residue out of the public docs and scripts, so the expectation going in is that the docs
are largely clean with at most a few residual items. The review confirms that expectation. It records
a keep / rewrite / move / delete verdict per file with a cited location, and — consistent with the
repository's own comment policy — it flags only residue, never the load-bearing rationale (the
Cove-constraint, safety, rollback, concurrency, permission, and compatibility notes that explain
*why* the code is the way it is are exactly the prose to keep).

This section grades documents for AI-sounding tone, so it holds itself to the same bar: the prose
below is plain and direct, names a real location for every claim, and carries no planning vocabulary
or assistant register of its own.

| File | Issue (location evidence) | Verdict |
| --- | --- | --- |
| `README.md` | Reads as durable, human prose — a one-line value statement, a feature list, install/usage steps, and an honest "never orphans a file / previewed before a byte moves" framing. Two issues, both already tracked: the Requirements line still cites the old Cove pin ("Cove 0.6.x — built against `minCoveVersion` 0.6.2", line 110) — the F-21 version-prose drift — and the undo description ("reverse the most recent batch", line 93; "every change is reversible through the undo log", line 95) promises reversibility without the data-clear caveat or a backup recommendation (F-26, cross-links F-19/F-13). No AI filler, no local paths, no planning IDs. | rewrite (the two cited lines only; keep the rest) |
| `CONTRIBUTING.md` | Clear, durable contributor prose. The only residue is the same version drift already filed as F-21: "matching 0.6.2" (line 29) and "`Cove.Plugins` / `Cove.Sdk` `0.6.2` from NuGet" (line 36), where the live pin is 0.7.1. The substantive build-path rationale (the two build paths, the local-source vs. NuGet selector, the strip discipline) is load-bearing and kept. | rewrite (the two version mentions only — folded into the F-21 fix) |
| `CHANGELOG.md` | Human-toned, newest-first, user-facing. The header's explanation that the `v1.x` headings are "development milestones, not published package versions" and that the artifact ships as `0.1.0` (lines 3–7) reads like a deliberate maintainer note, not residue — it is the honest reconciliation of an internal narrative with the shipped version, exactly the kind of rationale to keep. The "milestone" wording here is a release-narrative term, not a planning-process leak. Undo is described as reversing "the most recent batch", which is honest and does not over-promise permanence. | keep |
| `SECURITY.md`, `CODE_OF_CONDUCT.md` | Standard, durable open-source operations prose (the F-08 hardening, recorded Resolved). No AI register, no local paths, no planning IDs found. | keep |
| `docs/ARCHITECTURE.md`, `docs/BRANCHING.md` | Durable architecture and branching prose; no planning-vocabulary or local-path residue surfaced by the residue scan. The architecture doc carries the safety-invariant rationale the README points at, which is load-bearing. | keep |
| `.github/PULL_REQUEST_TEMPLATE.md` | Human-toned and, notably, carries a "Safety check" section that names this tool's real invariants for a reviewer to confirm — DB and disk update together, never overwrites a target, never force-unlocks, no host assemblies bundled (lines 13–19). This is durable, on-point prose. | keep |
| `.github/ISSUE_TEMPLATE/bug_report.md`, `feature_request.md` | Plain, friendly issue prose with concrete reproduce/scope sections ("Something renamed wrong, failed, or behaved unexpectedly"; "What are you trying to do that's hard or impossible today?"). No filler, no IDs, no local paths. | keep |
| `scripts/deploy-dev.ps1` (comments) | The comment block is durable technical rationale — the build → strip-verify → deploy → restart contract, why publish (not build) is needed for the strip, the fixed-identity and clean-only-id-subdir safety notes. This is the residue that F-10 already swept; the remaining comments are load-bearing why-notes, not process narrative. | keep |
| `scripts/check-version-parity.cjs` (comments) | The header explains the multi-source version-drift the gate guards. Its `0.6.2` mention (the "extension.json bumped to 0.7.1 but the `Rename.cs` override stayed 0.6.2" example) is an intentional illustration of the drift the gate exists to catch, not a stale claim — F-21 already noted this and explicitly leaves it untouched. | keep |
| `scripts/update-cove-sdk.ps1` (comments) | Durable rationale for the vendored-SDK design (why the SDK is vendored, that `npm ci` resolves it offline by hash, that it is AGPL-compatible). Load-bearing, no residue. | keep |
| `.github/workflows/build.yml`, `codeql.yml` (comments) | The build-workflow comments are technical rationale, with one residual version mention — the publish-step comment "Cove.Plugins/Cove.Sdk 0.6.2" (build.yml line 84) — which is the same F-21 drift (the live pin is 0.7.1). The rest of the workflow comments are durable. | rewrite (the one version comment only — folded into the F-21 fix) |
| `CLAUDE.md`, `.claude/CLAUDE.md` (agent files) | These are agent-context files, deliberately untracked from version control and gitignored (so they are not contributor-facing public docs and are out of scope for the public-tone bar). They encode the repository's standing no-jargon and C# comment policies that this very review grades the public docs against. No public-doc change is implied; they are correctly kept as private local context. | keep (private, out of the public-doc scope) |

The documentation is clean, and the review reflects that: the only residue the public docs carry is
the single version-prose drift already filed as F-21 (the four `0.6.2` mentions in the README,
CONTRIBUTING, and the build workflow comment, while the live pin is 0.7.1), and the one honest-message
gap filed as F-26 (the README's undo prose promising reversibility without the data-clear caveat or a
backup recommendation, the docs face of F-19). Every other file — the changelog, the security and
conduct files, the architecture and branching docs, the issue and pull-request templates, and the
three scripts' comment blocks — reads as durable, human-toned maintainer prose with no AI register, no
local-machine paths, and no planning or requirement identifiers, which is the expected outcome after
the v1.7 residue sweep (baseline F-10). No new finding is raised here: F-21 and F-26 are cross-linked
rather than re-argued, the F-19 durability limit is the substance behind F-26, and the load-bearing
rationale in every kept file is preserved. As with every section of this addendum, this review records
the keep/rewrite verdicts; it does not edit any of the documents it reviews.

## Cove example and template comparison

The baseline audit consulted six Cove ecosystem repositories under the `yourcove` organization and
listed them in its [External Cove sources consulted](./2026-06-29-read-only-repo-audit.md) section: the
single-extension repository template, the multi-extension repository template, `communitydownloaders`,
`communityscrapers`, `recommendations`, and the `officialextensionregistry`. The baseline used them to
confirm that this repository follows Cove's single-extension shape and manifest model; it did not work
through each one as a head-to-head comparison. This appendix does. For each repository it asks the same
five questions — what pattern that repository demonstrates, what this Rename repository already follows,
what Rename should copy, what it should avoid copying, and where Rename intentionally differs because it
is a *destructive* single-extension tool that carries a UI, a backend API, background jobs, and stored
state — and it states up front whether the source was actually reachable, so no claim rests on an
assumption about what a "typical template" contains.

All six repositories were reachable at the time of this pass, and each was read directly (the rendered
GitHub repository page and the raw `README.md`, plus the catalog, manifest, and workflow files named in
each entry below). Where a comparison turns on the shape of the Cove SDK rather than on an example
repository — how `CoveExtensionBase` surfaces manifest metadata, what the host injects at load — it is
grounded in the local sibling Cove checkout at `../../cove` (`src/Cove.Plugins/IExtension.cs`,
`src/Cove.Sdk/CoveExtensionBase.cs`, `sdk/frontend/package.json` for the `@cove/extension-sdk` shape),
which is a real, citable reference. One repository, `recommendations`, was reachable but had not yet
been populated with its intended content; that is called out plainly in its entry rather than papered
over. The comparison surfaced no new gap in *this* repository that the earlier sections had not already
filed, so no finding is added here; the one divergence it did surface is a property of an external
repository, recorded as an investigate-further note in that repository's entry, not as a Rename finding.

### Single-extension repository template

Reachability: fetched directly (`README.md`).

Pattern it demonstrates. One repository owns exactly one extension. The README frames it as a GitHub
**"Use this template"** starting point: build the single project (`dotnet build .\SingleExtensionTemplate.slnx
-c Release`), and let CI publish the project, copy `extension.json` to the package root, create
`<extension-id>-<version>.zip`, and attach it to a `v<version>` tag. It carries the same local-Cove
contract selector this repository uses — if checked out beside `cove` it references the local
`src/Cove.Plugins`, and `-p:UseLocalCovePlugins=false` forces package mode. It also ships a
`scraper-examples` folder with a pure-YAML scraper for extensions that need no compiled C#.

What Rename already follows. This is the template Rename is an instance of. The single-extension shape,
the `extension.json`-copied-to-package-root packaging, the `<extension-id>-<version>.zip` asset on a
`v*` tag, and the `UseLocalCovePlugins` local-source-vs-NuGet selector are all present here —
`.github/workflows/build.yml` (the Publish → Copy manifest → Package → release-on-`v*` chain, asset
name `com.alextomas955.rename-<version>.zip`) and `Directory.Build.props` (the selector) match the
template's contract closely. The baseline already recorded this alignment.

What Rename should copy. The template's `v<version>` tag is a bare `vX.Y.Z`, which is exactly what this
repository uses; nothing to copy that is not already here. The one habit worth borrowing is the
template's explicit "replace the example ID, namespaces, manifest fields, and release workflow
`EXTENSION_ID`" checklist — a single-extension repo that started from this template should make sure no
example placeholder survives, which for Rename it does not (the live `EXTENSION_ID` is
`com.alextomas955.rename`).

What Rename should avoid copying. The `scraper-examples` YAML-scraper folder is irrelevant to a
destructive renamer and should not be imported — Rename declares empty `scraperRuntime` and
`downloaderRuntime` permissions in `extension.json` precisely because it scrapes and downloads nothing.

Where Rename intentionally differs. The template assumes a single compiled project whose whole CI story
is build → publish → zip. Rename is heavier by necessity: it adds a frontend job that rebuilds and
source-verifies the panel bundle (`src/Rename.Ui/dist/index.mjs`) with a stale-bundle gate, a version-
parity gate across the manifest/assembly/UI sources, and an analyzers-as-errors build — machinery the
minimal template does not have because a one-file example does not need a verified UI bundle or a
multi-source version contract. That extra CI is deliberate, not drift.

### Multi-extension repository template

Reachability: fetched directly (`README.md`, `extensions/catalog.json`).

Pattern it demonstrates. One repository owns several related extensions. Its load-bearing conventions:
`extension.json` is the single source of truth for identity and metadata (`id`, `name`, `version`,
`description`, `author`, `url`, `categories`, `minCoveVersion`, `entryDll`, `dependencies`), and the
README states plainly **"Do not redeclare any of these in C#"** — the example extensions extend
`CoveExtensionBase`, into which the host injects the parsed manifest at load (which the local checkout
confirms: `src/Cove.Sdk/CoveExtensionBase.cs` surfaces those values from the injected manifest). An
`extensions/catalog.json` lists every extension (`id`, `path`, `tagPrefix`), a
`scripts/validate-extension-repo.mjs` checks the catalog and manifests stay consistent — including that
each manifest's `minCoveVersion` is at least the repo's `CoveMinVersion` — and each extension releases
under its own tag prefix (`example-ui/v0.1.0`), with CI packaging only the extension matching the
pushed tag. Central `Directory.Build.props`/`.targets` reference the Cove host contracts compile-only.

What Rename already follows. The most important convention — manifest as the single source of truth,
no metadata redeclared in C# — is the exact subject of baseline finding F-05 and addendum F-21. The
live version sources in Rename now agree (`0.7.1`/`0.1.0` across `extension.json`, the `Rename.cs`
override, and `Directory.Build.props`), and a dedicated `version-parity` CI job
(`scripts/check-version-parity.cjs`) enforces that agreement — Rename has effectively adopted the
multi-extension template's "manifest is truth" discipline and enforces it in CI, even though it is a
single-extension repo. The compile-only host-contract reference (host-provided assemblies stripped, not
shipped) is also followed via `Cove.Sdk.targets`.

What Rename should copy. The template's `minCoveVersion >= CoveMinVersion` consistency check is a clean
idea Rename does not currently express: Rename pins `minCoveVersion` to `0.7.1` but has no gate
asserting that the value it ships is coherent with the Cove contracts it builds against. This is
adjacent to F-21 (prose drift) and the registry-readiness work; a small parity assertion in the spirit
of `validate-extension-repo.mjs` would be a reasonable borrow.

What Rename should avoid copying. The whole multi-extension apparatus — `catalog.json`, per-extension
`tagPrefix` release tags, the per-extension CI matrix, the "package only the matching catalog entry"
workflow — is overhead for a repository that ships exactly one extension. Adopting it would be
over-engineering of precisely the kind the over-engineering review in this addendum warns against.
Rename's single bare-`v*` tag is correct for one extension.

Where Rename intentionally differs. The template's example extensions are deliberately thin
(`IScraperProvider` / `IDownloaderProvider` stubs that "drop all metadata properties"). Rename is the
opposite: a `FullExtensionBase` extension contributing API, UI, jobs, events, and state, whose value is
in the destructive execution path the templates never exercise. The template teaches repository shape;
Rename's complexity lives below that line, in the copy-verify-delete and revert-log machinery the
templates have no analog for.

### communitydownloaders

Reachability: fetched directly (`README.md`; release/tag conventions confirmed from it).

Pattern it demonstrates. A real, populated multi-extension repository: a manifest-only bundle
(`cove.community.downloaders`) plus capability extensions (`...common-audio`, `...common-text`,
`...reddit`, `...ytdlp`). It validates with `npm run validate:extensions`, builds each extension's own
`.csproj`, and releases with the lowercase `tagPrefix` from `extensions/catalog.json` (for example
`common/v1.0.0`, `ytdlp/v1.0.0`); the workflow accepts any `<tagPrefix>v<semver>` tag, packages only the
matching catalog entry, and uploads `<extension-id>-<version>.zip` for the registry. It is the
multi-extension template's conventions applied at scale.

What Rename already follows. Only the shared denominators: the registry-bound ZIP asset name
`<extension-id>-<version>.zip` and the "clone beside `cove` to build against local contracts, or
`UseLocalCovePlugins=false` for packages" development story. Rename matches both.

What Rename should copy. Little that is specific to this repo. The one transferable habit is the
explicit `npm run validate:extensions` pre-release manifest check; Rename's equivalent is the
`version-parity` job, which covers the same ground for a single extension.

What Rename should avoid copying. The per-capability split, the manifest-only "bundle" extension, and
the `tagPrefix`-namespaced release tags are all multi-extension constructs with no place in a single
destructive tool. A downloader's permission surface (`network`, `downloaderRuntime`) is also the exact
opposite of Rename's empty-permission posture and must not be imported.

Where Rename intentionally differs. A downloader fetches bytes from the network into the library; it is
additive and its failure mode is "nothing downloaded." Rename mutates bytes already in the library and
its failure mode is "a file in the wrong place or lost." That is why Rename carries machinery a
downloader repository has no reason to: a disk-and-DB-together transaction model, a preview that must
not mutate, and a revert log. The comparison is mostly a study in contrast.

### communityscrapers

Reachability: fetched directly (`README.md`).

Pattern it demonstrates. The largest populated multi-extension repository (a manifest-only
`cove.community.scrapers` bundle plus ~15 site-specific scrapers), and notably a *mixed-runtime* one:
alongside compiled C# scrapers it ships generated YAML scraper packs under `extensions/yaml/`, each with
`kind: "scraper-pack"`, no DLL, installed from source through the registry. Same catalog/`tagPrefix`/
`<tagPrefix>v<semver>` release model as `communitydownloaders`.

What Rename already follows. Again only the shared denominators (registry ZIP asset naming, the
local-vs-package build selector). Nothing scraper-specific applies.

What Rename should copy. Nothing material. The `kind` manifest field is worth being *aware* of — the
registry's validator treats a `kind: "scraper-pack"` entry specially — but Rename is a normal DLL
extension and correctly omits `kind`.

What Rename should avoid copying. The YAML/no-DLL packaging path, the scraper runtime permissions, and
the per-site extension explosion are all inapplicable and would be actively wrong to adopt. A scraper
reads remote metadata and never touches the user's files; importing any of its surface into a renamer
would expand the attack and blast surface of a tool whose entire safety argument depends on doing
*less*, not more.

Where Rename intentionally differs. Scrapers and downloaders are read-mostly, network-facing, and
horizontally numerous; Rename is write-heavy, network-free, and singular. The registry README itself
states the boundary the other way around — "Scrapers and downloaders are ordinary extensions in this
registry" — which is the useful reframing: from the registry's point of view Rename is just as ordinary
an extension to publish as any scraper, even though its internals could hardly be more different.

### recommendations

Reachability: fetched directly, but **investigate further** — the repository is reachable yet, at the
time of this pass, not populated with its intended content. Its GitHub description advertises "a
recommendation library and some official recommender extensions to serve as examples," but its
`README.md` is the multi-extension template README verbatim ("# Cove Multi-Extension Template … Use
this template when one repository owns multiple related Cove extensions"), and its
`extensions/catalog.json` still lists only the three template placeholders (`com.example.ui`,
`com.example.downloader`, `com.example.scraper`), with `extensions/ExampleUi/extension.json` present and
no real recommender extension directory found. So as a comparison source it is effectively a second
copy of the multi-extension template, not yet a populated example. This is recorded honestly rather than
fabricated into a confident "recommender" comparison; the divergence between its stated purpose and its
current contents is a property of that external repository, not a gap in Rename, so it is noted here and
not raised as a finding against this repo.

Pattern it demonstrates (as it actually stands). The multi-extension template pattern, already covered
above: manifest-as-truth, `catalog.json`, per-extension `tagPrefix` releases, central
`Directory.Build.props`/`.targets`.

What Rename already follows / should copy / should avoid. Identical to the multi-extension template
entry above, for the same reasons — the manifest-as-truth discipline is already adopted and enforced in
CI; the multi-extension catalog/tag-prefix apparatus should not be. Nothing recommender-specific can be
compared until the repository is populated.

Where Rename intentionally differs. A recommender extension is a read-only, inference-style contribution
to the library's presentation; it changes what a user *sees*, never what is *on disk*. Rename changes
what is on disk and leaves presentation alone. If the `recommendations` repository is later populated
with real recommender extensions, a follow-up pass should re-fetch it and compare against any
recommender-specific manifest or job conventions it then demonstrates; today there is nothing concrete
there to compare.

### Official extension registry

Reachability: fetched directly (`README.md`, `index.json`, `.github/workflows/validate.yml`, and a real
entry `extensions/cove.community.downloaders.reddit.json`).

Pattern it demonstrates. The registry is a metadata-only repository, not a binary host. Cove reads
`index.json` (a generated, ID-only list: `{"schemaVersion": "2.0", "extensions": [{"id": ...}]}`),
resolves each `extensions/{extension-id}.json` as the canonical metadata, downloads the extension's own
GitHub release ZIP, verifies SHA-256, and extracts it locally. The schema is strict and CI-enforced by
`validate.yml`: an entry needs `id` (matching the filename stem), `repositoryUrl`, summary fields (or a
`sourceManifestUrl`), and a `versions[]` array; each version needs `version`, `downloadUrl`,
`minCoveVersion`, and a CI-generated `checksum` in `sha256:<64-hex>` form. Top-level release fields
(`version`, `minCoveVersion`, `releasedAt`, `checksum`, `downloadUrl`, `updatedAt`, `url`) are
*forbidden* and must live on `versions[]`; categories must be lowercase kebab-case; a `sourceManifestUrl`
must be a `raw.githubusercontent.com` URL, never a `github.com/.../blob/` URL. CI computes each
`versions[].checksum` from its `downloadUrl` (failing the PR if the asset 404s), stamps missing
`releasedAt` on merge to `main`, and regenerates the ID-only `index.json` — none of which a submitter
hand-writes. A real entry such as `cove.community.downloaders.reddit.json` shows the shape exactly:
`sourceManifestUrl` pointing at the source repo's raw `extension.json`, `repositoryUrl`, lowercase
`categories`, and a single `versions[]` entry with `downloadUrl`/`minCoveVersion`/`checksum`.

What Rename already follows. Rename is registry-shaped before it has ever submitted. It uses the
required reverse-domain ID (`com.alextomas955.rename`), ships the conventional release asset name
(`com.alextomas955.rename-<version>.zip`) on `v*` tags, keeps `minCoveVersion` in its source
`extension.json` (so a `sourceManifestUrl` can sync it), and uses lowercase categories
(`["tools", "automation"]`). The baseline's registry-publication review already drafted a conforming
`extensions/com.alextomas955.rename.json` with `sourceManifestUrl`, `repositoryUrl`, and a `versions[]`
entry — which the live `validate.yml` confirms is the correct shape.

What Rename should copy. The registry's discipline of **not hand-writing generated fields** is the habit
to internalize: `checksum`, `releasedAt`, and `index.json` are CI's to compute, and a Rename release
process should be built to *let* registry CI compute the checksum from a reachable `downloadUrl` rather
than to pre-fill it. This is the substance behind the still-queued F-12 (codify registry publication as
a local release gate) and the baseline's F-13 (publish a real release asset and verify the URL resolves
before opening the registry PR). The single most copyable, evidence-backed rule is the one
`validate.yml` enforces in code: the `downloadUrl` must resolve to a real release asset at PR time, or
the checksum computation fails the validation — so Rename's release CI must publish the GitHub release
asset *before* the registry PR, exactly as F-12/F-13 already recommend.

What Rename should avoid copying. Nothing to avoid in the schema itself — but Rename must avoid the
anti-pattern the registry explicitly forbids: putting release data (`version`, `downloadUrl`,
`checksum`, `minCoveVersion`) at the top level of its registry entry instead of inside `versions[]`. The
baseline's drafted entry already nests them correctly; the rule to hold is to keep them there and never
duplicate summary/version fields into `index.json` by hand.

Where Rename intentionally differs. The registry treats every extension uniformly — "scrapers and
downloaders are ordinary extensions in this registry," and so is a destructive renamer. The registry
neither knows nor cares that Rename mutates files; its contract is purely about discoverable, checksum-
verified, version-pinned metadata. So this is the one comparison where Rename should *not* differ at all:
the safest move for a destructive tool is to be an entirely ordinary, schema-conformant registry citizen,
and the only honest gap is process, not shape — the still-queued F-12/F-13 release-gate work that makes
the conforming entry the baseline already drafted actually publishable. No new finding is added here:
the registry comparison reinforces F-12 and F-13 rather than surfacing anything they do not already
cover.

This appendix raised no new finding. Every angle it examined either confirmed an alignment the baseline
already recorded (single-extension shape, manifest-as-truth, registry-conformant ID and asset naming) or
reinforced a gap already filed — the registry-publication release gate (F-12) and the backup plus
clean-install and URL-resolves verification (F-13). The one external divergence it surfaced, the
`recommendations` repository standing as an unpopulated copy of the multi-extension template, is a
property of that repository and is recorded as an investigate-further note in its entry, not as a finding
against this repo. The numbering therefore stays at F-26; no F-27 is added, because the comparison
produced no evidence-backed gap in this repository that was not already on the books.

## Updated contributor-ready and release checklist

This checklist merges the baseline audit's fourteen-item contributor-ready target list with the new
items this addendum surfaced, and drops none of the baseline lines. Each baseline item now carries its
honest current state rather than an empty box: the items the shipped releases v1.5 through v1.8 closed
are checked and annotated with the release that closed them, and the items still open — the registry
metadata draft, the exact-ZIP clean-Cove smoke, and the backup guidance — stay unchecked. The items
this addendum adds follow in a clearly labelled subsection so the no-baseline-line-dropped property is
visible at a glance.

### Baseline contributor-ready items

- [x] Preview is read-only. — shipped in v1.6 (the resolution of F-01; the planner mutates nothing and
  resolves destination folders read-only via `RenamePlanner` / `CoveRenameDataPort.TryGetFolderIdAsync`,
  pinned by `PreviewPurityTests`).
- [x] Permissions are entity-specific. — shipped in v1.6 (the resolution of F-02; per-kind in-endpoint
  checks via `Rename.cs` `PermissionsFor` and the `ICurrentPrincipalAccessor` checks, pinned by
  `EndpointPermissionTests`).
- [x] Backend build/test works from a clean clone. — the NuGet-path restore/build/unit-tier runs clean
  (`dotnet restore`/`build`/`test -p:UseLocalCovePlugins=false`), and the bare-runner Linux path is proven by CI on every PR;
  a fresh-Windows-and-macOS clone with no sibling Cove is still pending an environment (the
  clean-room reproducibility section's "Not yet demonstrated" table). Checked for the proven Linux/CI
  and author-machine paths; the cross-platform clone remains a recorded gap, not a re-opened blocker.
- [x] Frontend install/verify/build works from a clean clone or documented bootstrap. — shipped: `npm ci`
  resolves the vendored `@cove/extension-sdk` tarball offline against the lockfile hash, and
  `npm run verify` / `npm run build` pass on a clean checkout (the `frontend` CI job and the
  author-machine reproducibility evidence).
- [x] CI rebuilds and verifies the shipped frontend bundle. — shipped in v1.7 (the resolution of F-03;
  the `frontend` job rebuilds `dist/index.mjs` and the stale-bundle gate `git diff --exit-code -- dist/index.mjs`
  fails the build on drift).
- [x] Package artifact is validated before release. — the local `deploy-dev.ps1` strip-verify gate
  enumerates the publish set, blocks on any host-provided-assembly leak, and asserts `Rename.dll` and
  `System.IO.Hashing.dll` are present; the baseline's publish dry run confirmed a clean root-level ZIP.
  The always-on CI artifact-contents assertion that would move this discipline off the local script is
  still open and recorded as F-23, so this item is checked for the local gate with that CI gap noted.
- [x] Tool versions are pinned and documented. — shipped: the .NET SDK is pinned by `global.json`, the
  Node toolchain by the `volta.node 24.11.0` field that CI mirrors, the npm tree by the exact-pinned
  `lockfileVersion 3` lockfile, and the Cove SDK by `CovePluginsVersion` in `Directory.Build.props`.
- [x] Cove source and Cove installation paths are configurable. — shipped in v1.7 (the resolution of
  F-07; the build source is selected by `UseLocalCovePlugins` / `COVE_REPO` / the sibling auto-detect,
  and the deploy target is `COVE_HOME`-resolved — no `I:\cove-dev` layout is assumed).
- [x] Docker/devcontainer path exists and runs the standard verification commands. — shipped in v1.7
  (the `.devcontainer` on `mcr.microsoft.com/devcontainers/dotnet:1-10.0` plus the Node 24 feature runs
  `.devcontainer/verify.sh` on create); a clean container run is still pending exercise (the
  reproducibility section's "Pending a container run"), so the path exists and is checked while the live
  container smoke stays a recorded follow-up.
- [x] Public docs no longer assume the author's local machine layout. — shipped in v1.7 (the resolution
  of F-10; the residue sweep removed local paths and process narrative from the public docs and scripts).
  One residual remains, but it is a version-prose drift, not a local-layout leak: see F-21 in the added
  items below.
- [x] Security and dependency maintenance files are present. — shipped in v1.8 (the resolution of F-08;
  `SECURITY.md`, `CODE_OF_CONDUCT.md`, `.github/dependabot.yml` across four ecosystems, the CodeQL
  analysis workflow, and least-privilege workflow permissions).
- [ ] Registry metadata draft exists and matches the final release. — still open. The baseline drafted a
  conforming `extensions/com.alextomas955.rename.json` and the registry comparison confirmed its shape,
  but codifying it as a local release gate against the actual published asset is the still-queued F-12.
- [ ] Exact release ZIP has passed clean-Cove smoke testing. — still open. Installing the exact tag-built
  ZIP into a fresh Cove instance and walking discovery, settings, preview, rename, undo, restart, and
  disable/re-enable is the queued F-13 work; it needs a clean-Cove environment this pass did not have.
- [ ] User-facing docs include backup guidance for first large batches. — still open (F-13). The undo
  durability limit (F-19) and the undo-messaging gap (F-26) cross-link this: the README and the UI should
  recommend a database/media backup before a first large run, which is not yet written.

### Items added by this addendum

These are the new checklist-worthy items the addendum's threat-model, reproducibility, supply-chain,
test, UI, and documentation sections surfaced. Each is keyed to its finding and carries its current
state.

- [ ] Reserved-device-name guard in the sanitizer (F-14). — open. `CleanSegment` has no `CON`/`PRN`/
  `AUX`/`NUL`/`COM1`–`9`/`LPT1`–`9` branch, so a rendered reserved stem fails the move on Windows as a
  skip; add a disambiguation branch and a `SanitizerTests` case.
- [ ] Untracked same-stem sidecar handling documented (F-15). — open. Only Cove-tracked captions move
  with a rename; document the tracked-caption-only scope as a known limitation, and consider an opt-in
  same-stem sweep later.
- [ ] Undo-durability limit documented and backed by a backup recommendation (F-19, cross-links F-13). —
  open. The revert log lives only in the extension's store; document that clearing the extension's data
  loses undo history and recommend a backup before large first runs.
- [x] SHA-pin the write-scoped release action (F-22). — open as a hardening item, not a blocker. The
  release-write scope is held only by `softprops/action-gh-release@v3`; SHA-pinning it (with Dependabot
  keeping the SHA fresh) is recommended. Left unchecked as a concrete still-open action.
- [ ] CI artifact-contents assertion ported from the local strip-verify gate (F-23). — open. Add a CI
  step after Publish that fails on any host-provided assembly, asserts `Rename.dll` and
  `System.IO.Hashing.dll` are present, and greps the publish set for absolute paths.
- [ ] Version-prose coherence (F-21). — open. Four contributor-facing locations still cite the old Cove
  `0.6.2` pin while the live pin is `0.7.1` (`README.md`, two lines in `CONTRIBUTING.md`, and the
  `build.yml` publish-step comment); update the prose to match the live pin.
- [ ] Test-source comment coherence (F-24). — open. Five test-source doc comments still carry internal
  planning vocabulary even though the production tree is clean of it; reword them to durable terms.
- [ ] Confirmation-proportionality parity between the two rename entry points (F-25). — open. The
  settings-panel Review dialog does not apply the blast-radius `ConfirmLevel` escalation the in-list bulk
  action does; have `ReviewDialog` read the `/preview` summary it already fetches.
- [ ] Undo-limitation messaging in the UI and README (F-26, cross-links F-19/F-13). — open. Add a
  one-line caveat where undo is offered and in the README that undo history is lost if the extension's
  data is cleared, with a backup recommendation.

The reading this checklist gives is consistent with the rest of the addendum: the baseline's release
blockers are closed and checked with the release that closed them, the still-queued registry and backup
work (F-12, F-13) stays unchecked, and the new findings this addendum raised are added as open items
rather than folded silently into the baseline lines.

## Updated follow-up plan

This plan updates the baseline audit's seven proposed milestones (M1 preview-purity hardening, M2 entity
permission matrix, M3 reproducible frontend/release CI, M4 cross-platform contributor setup, M5 release
and metadata consistency, M6 OSS hardening, M7 registry-publication readiness) against what the shipped
releases closed. The baseline's top three — M1, M2, M3 — and the metadata and OSS-hardening work shipped
across v1.6 through v1.8, so they are recorded here as done and not re-proposed. What remains is the
still-queued registry and backup work the baseline carried forward as F-12 and F-13, plus the new
findings this addendum surfaced (F-14 through F-26). Each remaining milestone below carries six fields:
goal, scope, files likely affected, acceptance criteria, validation commands, and risk level. The
validation commands are this repository's real safe, read-only checks — no command here mutates state.

### What the baseline already shipped

- **M1 preview-purity hardening** — shipped in v1.6 (F-01). The planner mutates nothing; routed preview
  resolves the destination folder id read-only. Not re-proposed.
- **M2 entity permission matrix** — shipped in v1.6 (F-02). Per-kind in-endpoint permission checks gate
  video, image, and audio with their matching Cove permissions. Not re-proposed.
- **M3 reproducible frontend and release CI** — shipped in v1.7 (F-03). CI rebuilds and source-verifies
  the bundle with a stale-bundle gate. Not re-proposed.
- **M4 cross-platform contributor setup** — largely shipped in v1.7 (F-07): `global.json`, the
  configurable `COVE_REPO` build source and `COVE_HOME` deploy target, and the devcontainer. The residual
  cross-platform verification (a fresh Windows/macOS clone, a clean container run) is folded into the
  cross-platform verification milestone below, since it is an environment-gated check rather than new
  build work.
- **M5 release and metadata consistency** — shipped in v1.8 (F-05): the live version sources agree at
  `0.7.1`/`0.1.0` and a `version-parity` CI job enforces the agreement. The one residual is prose drift,
  carried into the documentation-coherence milestone below as F-21.
- **M6 OSS hardening and contributor polish** — shipped in v1.8 (F-08): `SECURITY.md`,
  `CODE_OF_CONDUCT.md`, Dependabot, CodeQL, and least-privilege permissions. The PR-template additions the
  baseline asked for are the small remaining polish, carried into the registry-readiness milestone below.
- **M7 registry-publication readiness** — partially carried forward. The registry metadata draft exists
  and the registry comparison confirmed its shape, but codifying the release gate (F-12) and the backup
  plus clean-install smoke (F-13) is the still-queued work that heads the active list below.

### Priority order

The baseline ordered its work preview-purity → permissions → reproducible CI → cross-platform → metadata
→ OSS hardening → registry. Because the first six shipped, the active head of the list is the baseline's
last milestone — the still-queued registry/backup readiness work — together with the new
correctness/safety findings this addendum raised. The remaining milestones keep that relative ordering:
the correctness-and-safety hardening (a reserved-name guard that prevents a whole class of titles from
renaming on Windows) leads, because a correctness gap outranks a release-gate or a doc gap; the
registry/release-gate readiness follows as the baseline's own next-in-line registry work; then the
backup/clean-install milestone (the baseline's F-13, now reinforced by F-19 and F-26); then the
documentation-and-UI-coherence polish (the lowest-severity Nit/Low findings); and the cross-platform
verification pass last, because every item in it is environment-gated rather than a code change and is
explicitly investigate-further. No still-open baseline item is reordered relative to another; the new
findings are slotted by severity, which is the only reordering and is stated here as its reason.

### Milestone A — correctness and sanitizer hardening

Goal: close the confirmed correctness gap where a metadata value that renders to a Windows reserved
device name cannot be renamed, and settle the cross-platform name/path-comparison questions before any
behavior change.

Scope:

- Add a reserved-device-name branch to the sanitizer so a cleaned stem matching `CON`/`PRN`/`AUX`/`NUL`/
  `COM1`–`COM9`/`LPT1`–`LPT9` (with or without an extension) is disambiguated case-insensitively (F-14).
- Hold the case-only-rename, Unicode NFC/NFD, and cross-volume hardlink questions (F-16, F-17, F-18) as
  investigate-further inputs to the cross-platform milestone rather than changing comparison behavior
  here speculatively.

Files likely affected: `src/Rename/Engine/Sanitizer.cs`, `src/Rename.Tests/SanitizerTests.cs`.

Acceptance criteria:

- A rendered reserved stem (with and without an extension) is disambiguated to a valid name and renames
  successfully on Windows.
- A `SanitizerTests` case asserts the disambiguation; existing sanitizer tests still pass.
- No comparison-path behavior is changed without first reproducing F-16/F-17/F-18 on a real volume.

Validation commands:

- `dotnet build Rename.slnx -c Release -p:UseLocalCovePlugins=false`
- `dotnet test src/Rename.Tests/Rename.Tests.csproj -c Release --filter "Tier!=Integration"`

Risk level: Low (a localized sanitizer addition with a dedicated test; no change to the destructive
spine or the comparison paths).

### Milestone B — registry-readiness and release-gate

Goal: codify registry publication as a local release gate and prove the release artifact is clean before
the registry PR, so a release can be published without rediscovering the registry schema or shipping an
unverified asset.

Scope:

- Codify the registry metadata draft (`extensions/com.alextomas955.rename.json` with a nested
  `versions[]` entry carrying version, changelog, `downloadUrl`, `minCoveVersion`) and let registry CI
  compute the checksum from a reachable `downloadUrl` rather than hand-writing it (F-12).
- Add the CI artifact-contents assertion after Publish that fails on any host-provided assembly, asserts
  `Rename.dll` and `System.IO.Hashing.dll` are present, and greps the publish set for absolute paths,
  porting the local `deploy-dev.ps1` strip-verify gate into the always-on release path (F-23).
- SHA-pin the write-scoped release action `softprops/action-gh-release` (and optionally the CodeQL
  actions) to a verified commit with the major tag retained in a trailing comment (F-22).
- Add the three PR-template lines the contributor-policy section named (manifest/permission change,
  frontend-bundle rebuild, AI-disclosure/human-review).

Files likely affected: `.github/workflows/build.yml`, `.github/workflows/codeql.yml`,
`.github/PULL_REQUEST_TEMPLATE.md`, a new `extensions/com.alextomas955.rename.json` draft, and the
release/registry docs.

Acceptance criteria:

- A maintainer can publish a GitHub release, confirm the asset URL resolves, and prepare the registry PR
  without re-deriving the schema; the nested `versions[]` shape passes the official registry validation.
- The CI artifact-contents assertion fails the build if a host-provided assembly leaks into the publish
  set or an absolute path appears.
- The release-write action is pinned by SHA with Dependabot keeping it fresh.

Validation commands:

- `dotnet publish src/Rename/Rename.csproj -c Release -o artifacts/extension -p:UseLocalCovePlugins=false`
  then enumerate `artifacts/extension/*.dll` and grep the publish set for `I:\`, `/home/`, `/Users/`
- `node scripts/check-version-parity.cjs`
- `pwsh scripts/deploy-dev.ps1` strip-verify path as the local reference for the CI assertion

Risk level: Medium (touches the release workflow and the write-scoped action; bounded because the changes
are additive gates and a SHA pin, not a change to what is built or published).

### Milestone C — backup and clean-install verification

Goal: give a user the backup guidance and the clean-Cove smoke the baseline F-13 still owes, and document
the undo-durability limit the storage model carries.

Scope:

- Add user-facing backup guidance: recommend a database/media backup before a first large rename batch in
  the README and where undo is offered (F-13, F-26).
- Document that undo history lives in the extension's stored data and is lost if that data is cleared
  (F-19), in the README and the UI undo copy (F-26).
- Document the tracked-caption-only sidecar scope as a known limitation (F-15).
- Install the exact tag-built ZIP into a clean Cove instance and walk discovery, settings load,
  settings save/load, preview, rename, undo, restart/reload persistence, and disable/re-enable (F-13;
  this is the manual browser validation the project requires).

Files likely affected: `README.md`, `src/Rename.Ui/src/UndoSection.tsx`, `src/Rename.Ui/src/preview.ts`,
the user-facing rename docs; no backend behavior change.

Acceptance criteria:

- The README and the undo UI state the data-clear caveat and recommend a backup before large first runs.
- The tracked-caption-only sidecar limitation is documented.
- The exact release ZIP has been installed into a clean Cove instance and walked through the full
  lifecycle, with restart-persistence and disable/re-enable confirmed live.

Validation commands:

- Build the exact release ZIP on a `v*` tag (CI-built), then the manual clean-Cove install and the
  browser lifecycle walk — the one step automation cannot stand in for.
- `npm run verify` and `npm run build` in `src/Rename.Ui` for the undo-copy change.

Risk level: Low for the docs/copy changes; the clean-Cove smoke is environment-gated (needs a fresh Cove
instance) rather than risky.

### Milestone D — documentation and UI coherence

Goal: close the low-severity prose, comment, and UI-coherence findings so the published repository reads
consistently.

Scope:

- Update the four version-prose locations that still cite `0.6.2` to the live `0.7.1` pin — the README
  requirements line, two `CONTRIBUTING.md` lines, and the `build.yml` publish-step comment — leaving the
  parity gate's example comment and the baseline snapshot untouched (F-21).
- Reword the five test-source doc comments that still carry internal planning vocabulary to durable terms
  without touching test behavior (F-24).
- Have `ReviewDialog` read the `/preview` response `summary` it already fetches and surface the same
  `ConfirmLevel`-scaled wording and per-volume blast lines the bulk action uses, so both rename entry
  points confirm a cross-drive batch with equal weight; no backend change is needed (F-25).

Files likely affected: `README.md`, `CONTRIBUTING.md`, `.github/workflows/build.yml`,
`src/Rename.Tests/TestSupport/FakeRenameDataPort.cs`, `src/Rename.Tests/TestSupport/SubstDrive.cs`,
`src/Rename.Tests/Execution/ExecutorAllowlistGuardTests.cs`,
`src/Rename.Tests/Execution/CrossVolumeVerifyFailTests.cs`,
`src/Rename.Tests/Execution/CrossVolumeUndoTests.cs`, `src/Rename.Ui/src/ReviewDialog.tsx`.

Acceptance criteria:

- The four prose/comment version mentions read `0.7.1`/`0.7.x`; the parity gate still passes.
- The five test comments carry no requirement IDs or phase numbers; the tests still pass unchanged.
- The panel Review dialog applies the same blast-radius confirmation escalation as the bulk action for a
  large cross-drive batch.

Validation commands:

- `node scripts/check-version-parity.cjs`
- `dotnet test src/Rename.Tests/Rename.Tests.csproj -c Release --filter "Tier!=Integration"`
- `npm run verify` and `npm run build` in `src/Rename.Ui`

Risk level: Low (prose, comment, and UI-copy/wiring changes; the UI change reads a summary already on the
wire, so no backend or contract change).

### Milestone E — cross-platform and environment verification

Goal: settle the environment-gated investigate-further items by reproducing them on real hosts before
deciding whether any behavior change is warranted — explicitly not a speculative-change milestone.

Scope:

- Reproduce a case-only rename on a case-insensitive and a case-sensitive volume (F-16), an NFC-vs-NFD
  comparison of the same accented name (F-17), and a cross-volume hardlink sever (F-18), recording the
  observed behavior before any comparison-path change.
- Exercise `VolumeClassifier` against Docker bind-mount and UNC paths in a Linux/Docker and a network
  environment (the bind-mount/UNC matrix rows).
- Run a fresh Windows clone with no sibling Cove, a clean container run of `.devcontainer/verify.sh`, and
  a macOS clone, to close the reproducibility section's "Not yet demonstrated" rows.

Files likely affected: primarily test files
(`src/Rename.Tests/Execution/*`, `src/Rename.Tests/Concurrency/*`) once an environment confirms a real
behavior; no production change until reproduction justifies one.

Acceptance criteria:

- Each investigate-further item is reproduced on a real host and its observed behavior recorded; a
  behavior change is made only where reproduction shows a defect, not speculatively.
- The fresh Windows/macOS clone and the clean container run pass the standard verification pipeline.

Validation commands:

- `dotnet restore`/`build`/`test -p:UseLocalCovePlugins=false` and `npm ci`/`verify`/`build` on a fresh
  clone outside `i:\cove-dev`
- `bash .devcontainer/verify.sh` in a clean container
- the new `CollisionTests`/comparison cases for F-16/F-17/F-18 once an environment exists

Risk level: Low (investigate-first by design; no behavior change is made until a real host reproduces the
hazard).

## Read-only and evidence quality gate

This closing section audits the whole addendum against the discipline it set for itself and records the
observed state of the working tree, so the read-only contract is proven rather than asserted. It is the
last section by design: it grades the complete document — the three threat-model sections, the
reproducibility and compatibility sections, the dependency, contributor-policy, and staged-testing
sections, the over-engineering, naming, test-quality, UI, and documentation reviews, the ecosystem
comparison appendix, and the two synthesis sections above.

### Every confirmed finding carries a concrete anchor

The findings table (F-14 through F-26) and the prose around it cite a concrete anchor for every
Confirmed row — a file path, a class or function, command output, a documentation link, or an observed
pattern — never a bare assertion. Spot-checking the Confirmed rows: F-14 cites `src/Rename/Engine/Sanitizer.cs`
`CleanSegment` and the exact illegal-character set it filters; F-15 cites `RenameExecutor.RetargetCaption`,
`CoveRenameDataPort.MapVideoFile`, and the `if (file is VideoFile vf)` save branch; F-19 cites
`src/Rename/Execution/RevertLog.cs` and its single `revertlog` store key; F-21 cites the specific lines in
`README.md`, `CONTRIBUTING.md`, and `.github/workflows/build.yml` against the live pin in
`Directory.Build.props`; F-22 cites the `build` job's `permissions: contents: write` block and the
`softprops/action-gh-release@v3` step; F-23 cites the Publish/Package gap against the local
`deploy-dev.ps1` strip-verify; F-24 cites five test files by line; F-25 and F-26 cite `component:line`
anchors in `preview.ts`, `ReviewDialog.tsx`, `UndoSection.tsx`, and the README. The reproducibility
section's "Proven on this author machine" figures are each tied to the command that produced them (the
358-test unit tier, the byte-identical bundle diff, the offline `npm ci`), and the consistency and
asset-map grades each cite the executor, mover, port, and revert-log classes that back them. The
evidence-citation discipline holds across the document.

### Confirmed is separated from Investigate-further throughout

The findings table carries an explicit Status column whose only values are Confirmed and Investigate
further, and the dedicated "Confirmed mitigations, items to investigate further, and behavior to leave
as-is" section restates that split in prose: the Confirmed list names the destructive, consistency, and
undo spines with their class anchors; the Investigate-further list names the cross-platform items (F-16,
F-17, F-18), the bind-mount/UNC classification questions, the reindex-staleness question, and the
log-redaction enhancement (F-20), each marked as needing an environment or a host behavior confirmed
before any change. The threat-model matrices use the same three-value status (Confirmed, Investigate
further, leave as-is) per row, and the reproducibility section keeps "Proven by CI", "Proven on the
author machine", and "Not yet demonstrated" as distinct tiers rather than blurring them. Nothing
unproven is presented as Confirmed.

### Explicit leave-as-is calls appear where a change would be churn

The document repeatedly records an explicit leave-as-is verdict where a change would be churn rather than
a fix, instead of manufacturing work. The consistency section grades six of its seven scenarios
leave-as-is because the disk-first ordering, the rollback-through-the-matching-mover, the runtime
path assertion, the append-on-success revert log, and the path-driven undo are correct as they stand. The
over-engineering section resolves every reviewed item to leave-as-is — the single-implementation port,
the Rename-owned DTOs, the two movers, the two guards, the serialized revert log — each tied to a
concrete reason rooted in not losing a file, and it explicitly declines to remove the lone unused
`Gallery` enum member because the churn would cost more than it saves. The naming section's proposed
rename/move map is intentionally empty, and the test-quality section leaves the `Engine/` test-folder
asymmetry as-is as a churn-only move. The "leave as-is" column in the destructive-filesystem matrix marks
the OS-illegal-character, locked-file, permission-denied, symlink, junction, intra-batch-collision, and
existing-destination rows as correct and unchanged. The leave-as-is discipline is applied consistently,
not selectively.

### Observed working-tree state

The working-tree assertions below were run as part of writing this section, and the results are the
observed output of those commands, not a claim:

- `git rev-parse --short=7 HEAD` reported `9693317` — HEAD is unchanged at the commit this milestone
  started from. No source, CI, test, or rename commit was made.
- `git status --short --untracked-files=no` reported no output — the tracked working tree is clean. No
  `.cs`, `.ts`, workflow, test, manifest, or any other tracked file was modified, and no file was renamed.
- `git status --short` reported only `docs/audits/` as untracked. The `.planning/` directory is
  gitignored, so the only change on disk attributable to this milestone is this addendum document plus the
  gitignored planning artifacts.
- `git ls-files --error-unmatch docs/audits/2026-06-30-audit-addendum.md` failed (pathspec did not match a
  tracked file) — the addendum is deliberately left untracked. Keeping the audit document out of version
  control is this milestone's read-only convention: the document records the audit without itself
  becoming a tracked change to the repository it audits.

The observed result confirms the contract the addendum opened with: no source, CI, test, manifest, or
behavior change was made while producing it, and the only artifact on disk is this document and the
gitignored planning files. The read-only audit held end to end.
