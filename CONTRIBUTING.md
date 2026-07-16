# Contributing

## Branching and CI

See [Branching](https://alextomas955.github.io/cove-extensions/contributing/branching) for the
branch model and what CI runs on a pull request. Open PRs against `main`.

## Building and testing

From the repo root:

```sh
dotnet build CoveExtensions.slnx
```

Each extension has its own build/test/verify commands — see that extension's own README
([`extensions/Renamer/README.md`](extensions/Renamer/README.md),
[`extensions/WhisparrSync/README.md`](extensions/WhisparrSync/README.md)) and
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
  Pgvector) — the host provides them, and shipping them causes runtime type-identity mismatches.
- Never write to Cove's database directly; go through `CoveContext` and `SaveChangesAsync`.

Register the extension in [`extensions/catalog.json`](extensions/catalog.json) so CI can build and
release it. Each entry declares `name`, `id`, `path`, `tagPrefix`, `projectPath`, `manifestPath`,
`versionSourcePath`, and the optional `testProjectPath`, `uiPath`, and `e2ePath`/`e2eProject`.
Adding an extension's build and release capability is a catalog edit, not a workflow-logic change.

For the full authoring rules and a real layout to copy (`src/<Name>/`, `src/<Name>.Tests/`,
`src/<Name>.Ui/`), read the existing extension READMEs above and the Contributing guides on the
[docs site](https://alextomas955.github.io/cove-extensions/).

## Documentation

Docs are part of "done," not a follow-up. If a change alters an extension's settings,
configuration options, public API, or user-facing behavior, update that extension's docs in the
same PR — `extensions/<Name>/docs/`, its `README.md`, and `CHANGELOG.md` as applicable, plus the
matching page under `website/docs/` (the docs site).

A pre-commit check warns when an extension's source changed but its docs didn't; it's a reminder,
not a gate. If a change genuinely needs no docs update, say so in the PR and check the box anyway.

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
