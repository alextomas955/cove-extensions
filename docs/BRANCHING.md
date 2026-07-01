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
