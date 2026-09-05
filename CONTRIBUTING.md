# Contributing

## Branching and CI

See [Branching](https://alextomas955.github.io/cove-extensions/contributing/branching) for the
branch model and what CI runs on a pull request. Open PRs against `main`.

## Building and testing

From the repo root:

```sh
dotnet build CoveExtensions.slnx
```

An extension's frontend needs its wire types generated first — they are derived from the committed
OpenAPI document and gitignored, so a fresh clone has none and `npm run verify` fails on a missing
module. From the repo root, before any per-extension frontend command:

```sh
npm ci --no-workspaces
npm run generate:wire
```

To check the C# formatting without writing, and to fix it:

```sh
npm run format:cs:check
npm run format:cs
```

Run it through the script rather than calling `dotnet format` directly. Two reasons, both of which
have bitten here: with a `../cove` sibling checked out, `dotnet format` follows the ProjectReference
graph into Cove's own source and reports hundreds of issues that are not yours — the script excludes
it, and that exclude does the same work in CI, which checks Cove out beside this repo. And a folder
path passed to `--include`/`--exclude` **must end in a separator**: `--include ./src` matches nothing
and exits 0, while `--include ./src/` works. A scoping typo there does not fail; it silently passes.

Each extension has its own build/test/verify commands — see that extension's own README
([`extensions/Renamer/README.md`](extensions/Renamer/README.md)) and
[`.github/PULL_REQUEST_TEMPLATE.md`](.github/PULL_REQUEST_TEMPLATE.md) for what a PR is expected to
verify before it's opened.

## Adding or extending an extension

Every extension in this monorepo is a dynamically-loaded `Cove.Sdk` plugin. To add a new one (or
extend an existing one), you work against the same contract:

- Implement `IExtension` from `Cove.Plugins`, typically by subclassing `FullExtensionBase`.
- Ship an `extension.json` load manifest (`id`, `name`, `entryDll`, `jsBundle`, `minCoveVersion`).
  Its `entryDll` must match the built assembly name.
- Do not add your own Cove reference or a per-project `Directory.Build.props`. The `Cove.Sdk`
  reference and the source-selection math are wired once at the repo root
  (`Directory.Build.props`/`Directory.Build.targets`); your extension inherits it.
- Never bundle host-provided assemblies (`Cove.Core`/`Cove.Plugins`/`Cove.Sdk`, EF Core, Npgsql,
  Pgvector) — `Cove.Sdk.targets` strips them for two reasons the SDK states itself. The host's copy is
  normally the one that loads, so a leak usually costs only package weight and a warning naming the
  assembly. But if a bundled copy ever loads into the extension's own load context it creates a second
  identity for types that cross the host boundary, and casts and dependency injection then break
  silently. Verify the published output rather than trusting either outcome.
- Never write to Cove's database directly; go through `CoveContext` and `SaveChangesAsync`.

Register the extension in [`extensions/catalog.json`](extensions/catalog.json) so CI can build and
release it. Read the field set off `extensions/catalog.json` itself and off
`scripts/validate-extension-repo.mjs`, which is what enforces it — an enumeration copied into this file
goes stale silently, and one here already had. Some fields do more than name a location: one of them
marks an entry as manifest-only, and `.github/workflows/build.yml` then skips assembling, packaging and
releasing it. Every path you declare must exist: the validator fails the build on one that does not,
rather than letting the typo surface later as an `npm ci` in a missing directory partway through a
matrix leg. Adding an extension's build and release capability is a catalog edit, not a workflow-logic
change.

For the full authoring rules and a real layout to copy (`src/<Name>/`, `src/<Name>.Tests/`,
`src/<Name>.Ui/`), read the existing extension READMEs above and the Contributing guides on the
[docs site](https://alextomas955.github.io/cove-extensions/).

## Documentation

Docs are part of "done," not a follow-up. If a change alters an extension's settings,
configuration options, public API, or user-facing behavior, update that extension's docs in the
same PR — `extensions/<Name>/docs/`, its `README.md`, and `CHANGELOG.md` as applicable, plus the
matching page under `website/docs/` (the docs site).

Nothing enforces this automatically. The pull request template carries a docs checkbox, and that
checkbox is the whole reminder — no hook and no CI job compares changed source against changed docs.
If a change genuinely needs no docs update, say so in the PR and check the box anyway.

## Releasing

Releases are cut per extension via tags, not from this file's process — see
[Releasing](https://alextomas955.github.io/cove-extensions/contributing/releasing).

## Reporting a bug or requesting a feature

Open an issue using the templates under `.github/ISSUE_TEMPLATE/`.

## Reporting a security issue

Do not open a public issue for a security vulnerability — see [`SECURITY.md`](SECURITY.md).

## License

By contributing, you agree that your contributions will be licensed under this repository's
[AGPL-3.0 license](LICENSE).
