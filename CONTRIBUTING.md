# Contributing

## Branching and CI

See [Branching](https://alextomas955.github.io/cove-extensions/contributing/branching) for the
branch model and what CI runs on a pull request. Open PRs against `main`.

## Building and testing

From the repo root:

```sh
dotnet build CoveExtensions.slnx
```

Each extension has its own build/test/verify commands — see that extension's own README (e.g.
[`extensions/Renamer/README.md`](extensions/Renamer/README.md)) and
[`.github/PULL_REQUEST_TEMPLATE.md`](.github/PULL_REQUEST_TEMPLATE.md) for what a PR is expected to
verify before it's opened.

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
