# Changelog

User-facing changes, newest first. The `v1.x` headings are development milestones, not published
package versions: every milestone to date ships under a single artifact/release version, `0.1.0`. The
artifact `version` field (in `extension.json`, the runtime, and the UI package) — not the milestone
label — is what a registry or release carries, and the published milestone labels are kept as the
development narrative rather than renumbered to the release version.

## v1.7 — Reproducible build and cross-platform clean-clone

- The frontend bundle is now built and source-verified in CI from a vendored copy of the Cove
  extension SDK, so the project builds from a clean clone on any OS without a sibling Cove checkout;
  a stale committed bundle fails the build.
- The .NET SDK is pinned, the optional local-Cove source and the deploy target are explicit
  configuration, and a dev-container runs the standard build/verify steps.

## v1.6 — Product correctness

- Preview is strictly read-only — generating a preview no longer creates any database rows, even
  when it routes a file to a folder that doesn't exist yet.
- Each action now requires the permission for the entity kind it touches (videos, images, or audios)
  instead of the video permission for all of them.
- Rename jobs run one at a time, with bounded parallelism within a batch.

## v1.5 — Cove 0.7.1 alignment

- Rebuilt and re-verified against Cove 0.7.1; a rename followed by undo restores files byte-for-byte.
  No behavior change.

## v1.4 — Cross-drive routing and real-config parity

- Files can be routed to per-studio, per-tag, per-source-path, default, and unorganized destinations
  — including across drives — using a copy → verify → delete move that never loses a file, with a
  whole-batch summary to confirm before anything touches disk.
- Field rewriting (squeeze studio names, per-field find/replace, article stripping, duplicate-segment
  collapse), new tokens (`$parent_studio`, `$director`, `$bitrate`), and a pre-routing exclude system
  (by tag, studio, or path) — all opt-in, so the default naming stays unchanged.
- Undo is volume-aware, restoring cross-drive moves by copy-back-and-verify.
- Every rename, move, undo, and auto-rename is written to Cove's log: a line per file
  (`old -> new`), the reason for any skipped item, and a per-batch summary, so you can audit exactly
  what the extension did.

## v1.3 — Hardening and contributor-readiness

- Documentation for outside contributors: a rewritten README, a CONTRIBUTING guide, an architecture
  overview, and repository issue/PR templates.
- Tooling and consistency pass: lint/format gates, a pre-commit hook, and a zero-warning build
  policy across the C# and frontend trees.

## v1.2 — Friendlier settings and a single source of truth

- The rename home moved to a dedicated **Settings → Extensions → Rename** tab.
- Friendlier controls (dropdowns, toggles, inline token hints with "did you mean" suggestions) so a
  template can be built without memorizing syntax.
- A sticky live preview that renders representative old→new diffs as you change options.
- The panel is organized by how it's actually used: the filename template, where files go, and what
  gets renamed are surfaced first; formatting and safety-net options are grouped below.
- One source of truth for previews so the in-list action and the panel agree on what will happen.

## v1.1 — Preview, undo, and the bulk action

- A "Rename selected" bulk action on video and image lists, with a confirm-before-disk dialog and a
  progress-reporting background job.
- A live dry-run preview of the planned old→new changes.
- One-click undo of the most recent batch.

## v1.0 — Core rename engine

- Token templates for the filename and an optional folder path, with optional groups that drop out
  when empty.
- Multi-value controls for performers and tags, character and length safety (including Windows
  MAX_PATH handling), case transforms, and ASCII transliteration.
- DB-authoritative rename/move that never orphans a file: collision suffixing, sidecar handling, and
  a revert log.
- Optional, off-by-default auto-rename on metadata update for video and image items.
- All options configured and persisted in Cove's per-extension store.
