# Branching

The canonical default branch for this repository is **`main`**.

- Contributors branch off `main` and open pull requests against `main` (see the repo's
  contribution guidelines).
- CI (`.github/workflows/build.yml`) triggers on pull requests targeting `main`. It runs against
  every extension registered in `extensions/catalog.json` on every PR — there is no path filtering, so a
  change to any one extension (or to shared root tooling) exercises the whole monorepo's build
  matrix.
- The release workflow triggers independently, on tags matching `<extension-tagPrefix>v*` (e.g.
  `renamer/v1.0.0`), and only builds/releases the extension whose `extensions/catalog.json` entry the tag
  matches.

This branching model applies repo-wide, to the whole `extensions/` monorepo — it is not scoped to
any single extension.

## One-time default-branch alignment (owner)

The repository's local branch may still be `master` and the hosted default branch is set on first
push. To make `main` canonical:

1. Rename the local branch when ready: `git branch -m master main`.
2. After the first push to GitHub, set the repository's default branch to `main`
   (repo Settings → Branches → default, or `gh repo edit --default-branch main`).
3. Confirm a pull request triggers the **Build and Release Extensions** workflow.

Once the hosted default is `main`, the transitional `master` entry in the CI `pull_request` filter
can be removed.
