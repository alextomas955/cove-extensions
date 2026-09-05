---
sidebar_position: 7
---

# Configuration reference

This page lists every configuration knob you may need to set or read while working in this repo,
grouped by the file that owns it, in roughly the order you meet them. It states what each knob does
and where its value lives; it does not repeat the values, because a copied value goes stale without
telling anyone. Read the value off the file named in each entry.

Related pages, which this one does not restate:

- How the build wiring works, and why each fact is declared where it is - [Monorepo
  architecture](./architecture).
- Module shape, the wire contract, and the correctness rules - [Extension authoring
  patterns](./authoring-patterns).
- The branch model and what CI runs on a pull request - [Branching](./branching).
- How a release is built and published - [Releasing](./releasing).
- The end-to-end harness and how to add a suite - [Adding an extension's E2E
  suite](./authoring-e2e).
- An extension's user-facing settings - that extension's own docs, for example the [Renamer settings
  reference](/extensions/renamer/settings).

This page describes the levers, not the design.

## The .NET SDK pin

`global.json` at the repo root pins the SDK and selects the test runner.

| Key               | What it does                                                                     | Valid values                                                                                                   |
| ----------------- | -------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------- |
| `sdk.version`     | The .NET SDK version every `dotnet` command in this repo resolves to.            | Any installed SDK version. The pinned one is in `global.json`.                                                 |
| `sdk.rollForward` | How far the installed SDK may exceed the pinned version before `dotnet` refuses. | `disable`, `patch`, `feature`, `minor`, `major`, `latestPatch`, `latestFeature`, `latestMinor`, `latestMajor`. |
| `test.runner`     | Which test host `dotnet test` drives.                                            | `VSTest` or `Microsoft.Testing.Platform`.                                                                      |

Under `Microsoft.Testing.Platform`, `dotnet test` names a project with `--project <project path>`
as the workflows in `.github/workflows/` do. A bare project path also works, so treat `--project` as
the form this repo standardises on rather than the only accepted one.

## Cove source selection

The extensions build either against a local Cove source checkout or against published NuGet
packages. `Directory.Build.props` computes which, and `Directory.Build.targets` refuses a selection
it cannot deliver. Both files sit at the repo root and apply to every project in the monorepo, so an
extension's own `.csproj` never restates any of this.

| Knob                 | How you supply it                                                    | What it does                                                                                                                                                                                                                                                      |
| -------------------- | -------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `CoveSourceMode`     | `-p:CoveSourceMode=…` or the `COVE_SOURCE_MODE` environment variable | Pins the selection outright. Valid values are `source` and `none`; any other value is a build error.                                                                                                                                                              |
| `CoveRepoRoot`       | `-p:CoveRepoRoot=…` or the `COVE_REPO` environment variable          | Names the root of a Cove source checkout - the directory holding `src/Cove.Data/Cove.Data.csproj`. Supplying it selects `source`. A relative value resolves against the monorepo root, so it means the same thing whatever directory you launched the build from. |
| `UseLocalCoveSource` | `-p:UseLocalCoveSource=true\|false`                                  | The older spelling of the same intent as `CoveSourceMode`, still honoured as an input. It is also the property that reports the result - see below.                                                                                                               |

### Precedence

Highest first:

1. An explicit `-p:CoveSourceMode` (or `COVE_SOURCE_MODE`) wins outright. An explicit
   `-p:UseLocalCoveSource` is honoured as the older spelling of the same intent.
2. An explicitly supplied `CoveRepoRoot` (or `COVE_REPO`) selects `source`.
3. A conventional `../cove` sibling checkout selects `source`. This is the zero-config default.
4. Otherwise `none`: the published NuGet packages, which is the clean-clone path.

The selection turns on how a value _arrived_, not on whether a path happens to exist. Absence is a
legitimate state for the sibling auto-detect and an error for an explicit configuration, so
`Directory.Build.targets` fails the build when `source` was selected and no `Cove.Data.csproj` is at
the resolved location. It refuses to fall back, because the fallback would not build the projects
that mode was chosen for.

### Check which source was selected

Do not infer the selection from your working directory. The sibling auto-detect resolves `../cove`
relative to the monorepo root, so a checkout kept anywhere else takes the `none` branch unless you
name it. Set `COVE_REPO` to that location, or pass `-p:CoveRepoRoot`; either wins over the sibling.
Under `none`, a project that requires a checkout stops the build by name.

Two things report the answer:

- `dotnet build` prints one line naming the resolved mode and absolute repo root. The absolute form
  is deliberate: it is what makes a shell's path rewriting of a POSIX `COVE_REPO` into a
  drive-lettered path visible. `dotnet test` does not show that line, so do not go looking for it on
  a test run.
- Query the properties without building:

  ```sh
  dotnet msbuild extensions/Renamer/src/Renamer/Renamer.csproj \
    -getProperty:UseLocalCoveSource \
    -getProperty:CoveSourceMode \
    -getProperty:CoveRepoRootResolved
  ```

The property that reports the result is **`UseLocalCoveSource`**, not the mode switch you set. It is
derived from `CoveSourceMode` - `true` when the mode is `source`, `false` otherwise - so reading it
back tells you what the build actually wired up. `CoveRepoRootResolved` holds the absolute root, and
is empty when none was found.

### The host version floor

| Property         | What it does                                                                                                                                                                    |
| ---------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `CoveMinVersion` | The single declared floor for the host Cove version the extensions require. `scripts/validate-extension-repo.mjs` compares each `extension.json`'s `minCoveVersion` against it. |
| `CoveSdkVersion` | The version used for the `Cove.Sdk` and `Cove.Plugins` package references. It defaults to `CoveMinVersion`.                                                                     |

Both live in `Directory.Build.props`, and their declaration order there is load-bearing: MSBuild
evaluates properties top to bottom, so `CoveSdkVersion` must be declared after `CoveMinVersion` or it
expands to an empty version and every Cove package reference silently takes it.

Raise the floor only when the extensions depend on a host capability that requires it. It is what
users see as the minimum Cove version, so never edit it to make a version comparison pass.

### The Cove test image

| Property                  | What it does                                                                                                                                                                |
| ------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `CoveTestImageRepository` | The registry and repository of the released Cove container image. Read by `scripts/fetch-cove-assemblies.mjs` and by the end-to-end harness in `tests/e2e/lib/harness.mjs`. |
| `CoveTestImageTag`        | The tag `scripts/fetch-cove-assemblies.mjs` extracts when you pass no explicit `--tag`.                                                                                     |

Both live in `Directory.Build.props`, and both are deliberately separate from `CoveMinVersion`: that
one is the floor the extensions advertise to users, while these name a moving upstream build.

Read the tag knob narrowly. The end-to-end harness takes only the registry and repository from
`CoveTestImageRepository` and derives its own tag - from `COVE_E2E_TAG` when set, otherwise from the
highest `minCoveVersion` declared by a catalog entry that has an e2e suite. So `CoveTestImageTag`
does not decide which host version the e2e suite boots.

### Compiler and analyzer settings

`Directory.Build.props` also sets the compiler posture for every project. The ones a contributor
meets:

| Property                    | What it does                                                                                                            |
| --------------------------- | ----------------------------------------------------------------------------------------------------------------------- |
| `TargetFramework`           | The single target framework every project in the monorepo compiles for.                                                 |
| `TreatWarningsAsErrors`     | Zero-warning policy: any compiler or analyzer warning fails the build, locally and in CI.                               |
| `AnalysisLevel`             | Which .NET analyzer rule set applies. The analyzers ship in the SDK, so do not also add the NetAnalyzers NuGet package. |
| `EnforceCodeStyleInBuild`   | Makes the IDE-prefixed style rules run in a command-line build, not only in an editor.                                  |
| `GenerateDocumentationFile` | Enabled so `IDE0005` (dead `using`) is reported on build.                                                               |
| `NoWarn`                    | Silences `CS1591` (missing XML doc on a public member). Doc comments are earned by judgment here, never mandated.       |
| `EnableDynamicLoading`      | Required: every extension is loaded dynamically by the host.                                                            |

`Directory.Build.targets` adds the Sonar C# analyzer to every project, unconditionally on how Cove is
located. Two things are worth knowing before you read a green result as a clean tree. The pre-commit
hook runs per file, so it reports on the files you touched and not on their neighbours. And the CI
legs do not all build the same way: the analyzer gate (`csharp-format` in `lint.yml`) builds the whole
solution in `source` mode against a checked-out Cove, so it sees what a local source build sees. The
Windows leg checks Cove out and builds in `source` mode too. The packaging leg builds in `none` mode,
which is what proves the shipped assembly needs no checkout.

## Package versions

`Directory.Packages.props` sets `ManagePackageVersionsCentrally`, so every NuGet version for the
whole monorepo is declared there as a `<PackageVersion>` and every `.csproj` carries a version-less
`<PackageReference>`. If you add a package, add its version to that file and leave the project
reference bare.

There is one deliberate exception. `Cove.Sdk` and `Cove.Plugins` reference the `$(CoveSdkVersion)`
property from `Directory.Build.props` instead of a literal, because that property is the single
source of truth `scripts/validate-extension-repo.mjs` reads as the host-SDK floor. The indirection
also keeps the host SDK hand-bumped in lockstep with the local Cove host rather than bumped
automatically, which a literal would invite.

Several entries in that file carry a comment stating why they are held where they are. Read the
comment before changing a version.

## Node packages and scripts

### More than one `package.json`

This repo holds several, each owning its own scripts and dependencies:

- the repo root, which holds the monorepo-wide tooling;
- `website/`, the docs site;
- `tests/e2e/`, the shared end-to-end harness;
- `extensions/*/e2e/`, each extension's suite;
- each extension's UI project, for example `extensions/Renamer/src/Renamer.Ui/`;
- `shared/ui-shared/`, which declares no scripts and no dependencies.

`npm run <script>` resolves against the nearest `package.json`, so **the same script name can mean
different things depending on the directory you run it from.** Some worked examples, all read off the
files:

| Script          | At the repo root                                   | In `extensions/Renamer/src/Renamer.Ui/`                         | Elsewhere                                                                                                                        |
| --------------- | -------------------------------------------------- | --------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------- |
| `test`          | Runs the Node tests for the scripts in `scripts/`. | Generates the wire types, then runs the unit tests with Vitest. | In `tests/e2e/` it runs Playwright; in `extensions/Renamer/e2e/` it delegates to `tests/e2e` scoped to that extension's project. |
| `generate:wire` | Generates wire types for every catalog entry.      | Generates them for that one extension only.                     | -                                                                                                                                |
| `build`         | Not defined.                                       | Builds the UI bundle with Vite.                                 | In `website/` it builds the docs site.                                                                                           |
| `format`        | Formats the whole monorepo with Prettier.          | Formats that package only.                                      | -                                                                                                                                |

When a command in another document or a workflow specifies a working directory, run it there. The
same words in the wrong directory do something else, or nothing.

### The root `package.json`

| Field             | What it does                                                                                                                                          |
| ----------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------- |
| `engines.node`    | The minimum Node version the root tooling supports. `website/package.json` declares its own, independently.                                           |
| `volta.node`      | The exact Node version Volta switches to in this directory. `extensions/Renamer/src/Renamer.Ui/package.json` carries its own pin as well.             |
| `workspaces`      | Which directories npm treats as workspaces. Only the end-to-end packages are listed - the UI projects and `website/` are not.                         |
| `overrides`       | Forces a transitive dependency onto the version the root declares.                                                                                    |
| `devDependencies` | The monorepo-wide tooling: the formatter, the linter and its plugins, the merge-gate tools, the hook runner, TypeScript, and the wire-type generator. |

Because the UI projects and `website/` are not workspaces, each carries its own lockfile and needs
its own `npm ci` run in its own directory. Use `cd <dir> && npm ci` rather than `npm ci --prefix
<dir>`. A root `npm ci --no-workspaces` installs only the root tooling, which is what the lint
workflow and the root instructions in `CONTRIBUTING.md` use.

The root scripts a contributor runs are declared in the root `package.json`. `prepare` installs the
local hook runner; see [Local hooks](#local-hooks) below.

## Style and gate configuration

Each file below owns one concern, and no extension carries a per-extension copy of any of them -
adding an extension needs no edit to any of these.

| File                       | What it governs                                                                                                          |
| -------------------------- | ------------------------------------------------------------------------------------------------------------------------ |
| `.editorconfig`            | Encoding, line endings, indentation per file type, and the C# style and analyzer severities. `dotnet format` reads it.   |
| `.prettierrc.json`         | Prettier's formatting options for the whole repo.                                                                        |
| `.prettierignore`          | What Prettier skips.                                                                                                     |
| `eslint.config.mjs`        | The single ESLint config for every extension's TypeScript and React UI and every first-party `.mjs`/`.cjs` script.       |
| `.knip.json`               | Dead files, unused exports, and unused dependencies on the TypeScript side, declared per workspace.                      |
| `.jscpd.json`              | The copy-paste check: the languages measured, what is excluded, and the duplication threshold above which the run fails. |
| `.syncpackrc.json`         | Dependency-version drift across every `package.json` in the repo.                                                        |
| `.markdownlint-cli2.jsonc` | The Markdown rules and the file set they apply to.                                                                       |
| `.sonarcloud.properties`   | The analysis scope for SonarQube Cloud automatic analysis.                                                               |
| `CoveExtensions.slnx`      | The explicit project list the C# formatting and analyzer gates take as their subject.                                    |

A few of these carry a non-obvious mechanic or a deliberate exclusion. The reasons are in the files;
the ones worth knowing before you edit are below.

### Dead-code severities are build errors

`.editorconfig` raises `IDE0051` (unused private member), `IDE0052` (unread private member) and
`IDE0060` (unused parameter) to `error`. They run in a command-line build because
`EnforceCodeStyleInBuild` is on, so dead C# code fails the build rather than appearing as a
suggestion in an editor. This is the C# counterpart of what `.knip.json` gates on the TypeScript
side; dropping either would gate dead code on one tier and not the other.

### Prettier ignores what nested ignore files cannot reach

Prettier reads only the `.gitignore` at its working directory, never a nested one, so a
root-run pass reaches subjects that a nested ignore file excludes. `.prettierignore` therefore
repeats each nested entry the root `.gitignore` does not already carry, anchored with `**/` so a
second package is covered without an edit.

Three exclusions there are about content that must not be rewritten rather than about build output:
the tracked lockfiles, whose reformatting produces a diff nobody reads; the committed wire document,
which is written verbatim by its emitter and diffed by CI; and the wire snapshots, which are compared
byte for byte, so reformatting one rewrites the expectation instead of tidying it.

### ESLint has no per-extension config

There is intentionally no per-extension ESLint config. A new extension's `src/` and scripts are
linted by path from `eslint.config.mjs`, so the rule set cannot drift between extensions. The
architectural boundary rules derive their list of UI bundles from `extensions/catalog.json`, so an
extension that gains a UI is classified with no config edit.

Two exclusions matter. Generated wire types are ignored, because they are a program input rather than
lint's subject. `website/` is ignored, because the docs site has its own toolchain and its own
dependency tree. Formatting rules are off across the board - that is Prettier's job.

### Knip's ignored dependencies are host-provided

`.knip.json` declares an entry set and a project set per workspace. Its `ignoreDependencies` lists
exist because those packages are genuinely used but not resolvable the way knip looks for them: some
are host import-map externals that the bundle never carries, and some are consumed only by build or
test configuration. Read the list in the file before adding to it - an entry added to silence a
finding hides the class of finding the tool exists for.

### Duplication is measured on product code only

`.jscpd.json` is thresholded rather than absolute: it fails above a percentage of duplicated lines
declared in that file, so the point is to stop new accidental duplication from entering silently
rather than to demand zero. It honours `.gitignore` and additionally excludes generated and vendored
code, the docs site, and the end-to-end tier. Its exclusions are narrower than Sonar's on one axis:
Sonar excludes the C# and TypeScript test files by name, and this one does not, so duplication inside
a test file still counts here. Read both files rather than assuming they share a list.

`.sonarcloud.properties` excludes the same test-file class from its own duplication metric, and says
why: a test that legitimately replays the same recorded fixture as its neighbour is not a
copy-paste defect, and counting it as one produces a number nobody can act on.

### Syncpack's three ignored version groups

`.syncpackrc.json` decides its subject set from its own `source` globs, not from npm workspace
discovery - discovery would read the root and the two end-to-end workspaces and silently skip every
UI bundle and the docs site.

Three version groups are deliberately ignored, and each carries its reason as its `label`:

- The end-to-end projects depend on the shared harness package by wildcard, because npm resolves that
  to the local copy and a fixed range would break workspace resolution the moment the local version
  bumps.
- The docs site's React is Docusaurus's own and is independent of an extension UI's, which must match
  whatever React the Cove host page already loaded. The group is scoped to `website`, so React drift
  between the root and any extension UI still fails.
- The Renamer UI package is private and nothing declares a dependency on it, so it carries no version
  for syncpack to compare dependents against. The group is scoped to the local dependency type, so a
  dependent declaring a wrong range would still fail.

### Markdownlint's file list lives in its config

`markdownlint-cli2` **appends** its config's globs to the command line, so a glob passed on the
command line can only widen the set while reading as a narrowing. The file list therefore lives in
`.markdownlint-cli2.jsonc` and only there, and the CI job passes an explicitly empty `globs` input -
the one spelling that leaves the config as the single source, since the action's own default is
non-empty and root-only.

### Sonar's filename and branch traps

Two things about `.sonarcloud.properties` are easy to get wrong, and both fail silently:

- Automatic analysis reads `.sonarcloud.properties` and ignores `sonar-project.properties`, which
  belongs to the CI-based scanner. The wrong filename gets you silence, not an error.
- Only the default branch's copy is read. Editing it on a branch changes nothing until the branch
  merges, so a scope fix cannot be verified on the pull request that makes it.

The supported key set is small - sources, tests, their inclusions and exclusions, duplication
exclusions, and encoding. Coverage import is not among them.

## Local hooks

`lefthook.yml` at the repo root declares the pre-commit checks: Prettier and ESLint on staged files,
the Tailwind class check for each UI bundle, the host-import check, and `dotnet format` on staged C#
files. The formatting and lint entries fix rather than verify and restage what they changed, because the check
costs the same either way. It is deliberately lightweight - no full build and no test run
on commit.

The hook runner is installed by the root `prepare` script. Before you rely on it, know that **a local
hook is advice a contributor can skip, and only a check a CI workflow runs can block a merge.** The
runner is also silently absent whenever npm withholds its install script, in which case a commit
passes with no hook having run and git reports nothing. If you need a hook's guarantee on a
particular commit, run its checks by hand.

That is why the gate tools above are wired into `.github/workflows/lint.yml` as well, and why a check
you need enforced belongs in a workflow rather than only here.

One hook entry cannot be a gate anywhere: the host-import check reads the Cove host's own generated
shim out of a local Cove checkout, so with no checkout present it skips loudly rather than passing
quietly. It honours `COVE_REPO`.

## The extension registry

`extensions/catalog.json` is the extension registry and the source of truth CI reads to compute its
build matrix. Each entry declares an extension's identity, the paths CI needs, and the artifact set a
release ships.

Read the authoritative field set off two places rather than off any prose, including this page:

- the file itself, [`extensions/catalog.json`](https://github.com/alextomas955/cove-extensions/blob/main/extensions/catalog.json);
- `scripts/validate-extension-repo.mjs`, which is what enforces it.

Adding a new extension's build and release capability is a `catalog.json` edit, not a change to
workflow logic. The linter, the boundary rules, and the wire-type generator all derive their
per-extension lists from this file too. For which file reads which field, and what a green result
does and does not prove, see [Monorepo architecture](./architecture).

## Environment variables

| Variable                  | What it does                                                                                                                                                                                                        | Read by                                                                  |
| ------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------ |
| `COVE_REPO`               | The root of a local Cove source checkout. Supplying it selects `source` mode and overrides the `../cove` sibling auto-detect.                                                                                       | `Directory.Build.props`, `scripts/check-host-imports.mjs`                |
| `COVE_SOURCE_MODE`        | Pins the Cove source selection to `source` or `none`, outranking every other input.                                                                                                                                 | `Directory.Build.props`                                                  |
| `COVE_E2E_IMAGE`          | A complete container image reference for the end-to-end host, which wins over the repository and tag resolution.                                                                                                    | `tests/e2e/lib/harness.mjs`                                              |
| `COVE_E2E_TAG`            | The tag for the end-to-end host image. When unset, the harness uses the highest `minCoveVersion` among catalog entries with a suite.                                                                                | `tests/e2e/lib/harness.mjs`                                              |
| `COVE_WIRE_DOC_UPDATE`    | Set to `1` to make the wire-document test write the committed OpenAPI document instead of failing on drift.                                                                                                         | `shared/Cove.Extensions.Shared.Testing/ExtensionOpenApiDocumentTests.cs` |
| `COVE_TEST_SECOND_VOLUME` | Names an existing directory on a different filesystem volume, for the tests that need a cross-volume move. The tests fail with a clear message when it names a path that does not exist or sits on the same volume. | `extensions/Renamer/src/Renamer.Tests/TestSupport/SecondVolume.cs`       |
| `CI`                      | Tightens the end-to-end run: longer startup timeout, fewer workers, retries enabled, and `test.only` forbidden.                                                                                                     | `tests/e2e/playwright.config.mjs`, `tests/e2e/lib/harness.mjs`           |
