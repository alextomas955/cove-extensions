---
sidebar_position: 8
---

# Branching

The canonical default branch for this repository is **`main`**.

- Contributors branch off `main` and open pull requests against `main` (see the repo's
  contribution guidelines).
- CI (`.github/workflows/build.yml`) triggers on every pull request, whatever branch it targets. The
  `pull_request` trigger filters on the PR's _base_ branch, so while it named `main` a pull request
  between two feature branches matched no workflow and could merge with no signal at all — which is
  what makes a stacked series reviewable. It runs against every extension registered in
  `extensions/catalog.json` on every PR — there is no path filtering, so a change to any one extension
  (or to shared root tooling) exercises the whole monorepo's build matrix.
- Releasing is a job inside that same workflow, not a workflow of its own. It runs only on a pushed
  tag matching `<extension-tagPrefix>v*` (for example `renamer/v1.0.0`), it gates on the aggregated
  required checks before it publishes anything, and on a tag push the matrix narrows to the single
  extension whose `extensions/catalog.json` entry the tag matches — no other extension builds on that
  run.
- The same workflow also runs on a daily schedule. That leg is the only one that resolves the version
  axis against a moving upstream Cove image, so it is where upstream breakage above the declared floor
  surfaces. A red that recurs at the scheduled hour and passes on re-run is that leg, not your branch.

This branching model applies repo-wide, to the whole `extensions/` monorepo — it is not scoped to
any single extension.
