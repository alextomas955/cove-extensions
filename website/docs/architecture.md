---
title: Repository architecture
sidebar_position: 1
description: How the cove-extensions monorepo is shaped — the catalog, the centralized build wiring, the host-assembly boundary, and the shared modules.
---

This page explains the shape of the `cove-extensions` **repository** — the parts that exist because
several extensions live in one git repo, and the reasoning behind them. It is deliberately not a
guide to any one extension's internals. Each extension documents its own layers, endpoints, and
safety model on its own page:

- [Renamer architecture](/extensions/renamer/architecture)
- [Whisparr Sync architecture](/extensions/whisparr-sync/architecture)

Everything below is the layer underneath both of those: how an extension is registered, how it is
built against the Cove SDK, what it is allowed to ship, and what it may share with its siblings.

## The multi-extension model

Each extension is a self-contained Cove plugin — a .NET class library implementing `IExtension`
(from `Cove.Plugins`, normally via `FullExtensionBase`), an `extension.json` load manifest, and, if
it has a UI, a React/TypeScript bundle built to `dist/index.mjs`. Extensions ship and version
independently: Renamer's manifest is at version `0.3.0` with `minCoveVersion` `1.0.0`, Whisparr
Sync's at `0.1.0` with `minCoveVersion` `1.1.0`.

What makes them one repository rather than several is a single registry file,
[`extensions/catalog.json`](https://github.com/alextomas955/cove-extensions/blob/main/extensions/catalog.json).
Every entry declares the paths CI needs for that extension — `name`, `id`, `path`, `tagPrefix`,
`projectPath`, `testProjectPath`, `manifestPath`, `uiPath`, `versionSourcePath`,
`requiredBundledDlls`, and its e2e paths.

The catalog is the source of truth the build workflow reads to compute its job matrix. On a pull
request the matrix is *every* catalog entry — there is no `paths:` filtering anywhere, so a change
to shared code cannot green a PR while silently breaking a sibling extension. On a tag push the
matrix is filtered to the single entry whose `tagPrefix` the tag starts with, so pushing
`renamer/v1.0.0` builds, strip-verifies, and packages only Renamer.

The consequence is the point: adding a new extension's release capability is a `catalog.json` edit,
not a workflow-logic change. The workflow never names an extension.

## Repository layout

```text
cove-extensions/
├── Directory.Build.props      # target framework, analyzer posture, Cove source selection
├── Directory.Build.targets    # the Cove.Sdk reference + host-assembly strip import
├── Directory.Packages.props   # Central Package Management — every NuGet version
├── CoveExtensions.slnx        # the solution: 4 extension projects + 2 shared projects
├── BannedSymbols.txt          # repo-wide banned-symbol list (a compiler AdditionalFiles input)
├── extensions/
│   ├── catalog.json           # the registry CI reads
│   ├── Renamer/               # docs/ · e2e/ · scripts/ · src/{Renamer, Renamer.Tests, Renamer.Ui}
│   └── WhisparrSync/          # docs/ · e2e/ · src/{WhisparrSync, WhisparrSync.Tests, WhisparrSync.Ui}
├── shared/                    # cross-extension first-party modules (see below)
├── scripts/                   # CI/gate helpers, including the one gate registry
├── tests/e2e/                 # the shared end-to-end harness
└── website/                   # the Docusaurus docs site
```

Two conventions hold across that tree. Each extension owns everything about itself — its source,
tests, e2e suite, docs, changelog, and README all live under `extensions/<Name>/`, so an extension
is readable and reviewable as a unit. And anything at the root exists because it must be stated
exactly once for every extension; if a root file could be per-extension without risk of drift, it
would be.

The npm side follows the same split. The root `package.json` declares workspaces for
`tests/e2e` and `extensions/*/e2e` only. Each UI bundle and the docs site keep their own
dependency roots and lockfiles, because a UI bundle resolves a vendored Cove frontend SDK tarball
that must install offline.

## The build wiring at the root

`Directory.Build.props` and `Directory.Build.targets` wire `Cove.Sdk` — which transitively carries
`Cove.Plugins` and `Cove.Core` — into every non-test project in the repository. An extension's
`.csproj` therefore declares no direct Cove reference and no relative-path math back to a local Cove
checkout. Restating either per project is how the two would drift.

The wiring is scoped to projects whose name does not end in `.Tests`, and that exclusion is
load-bearing rather than cosmetic. A test project already reaches `Cove.Sdk` transitively through
its `ProjectReference` to the extension it tests. Adding `Cove.Sdk` a *second* time as a direct,
`Private=false` reference makes MSBuild resolve it as non-copy-local, so `Cove.Sdk.dll` drops out of
the test project's own `bin/` and xUnit fails at reflection time. Keeping the transitive path the
only path avoids that.

`Cove.Data` is deliberately outside this wiring. It sits off the `Cove.Sdk → Cove.Plugins →
Cove.Core` chain, is not published to nuget.org, and is consumed test-only, so each test project
keeps its own `Exists()`-guarded reference to it — keyed on the same resolved checkout path the root
uses, so the two can never point at different checkouts.

## Cove source selection

An extension can be built against a local Cove checkout or against the published packages, and the
choice matters: a local build is ABI-identical to the host you are running, while the package build
is what a clean clone and CI get. The precedence is decided once, in `Directory.Build.props`,
highest first:

1. An explicit `-p:UseLocalCoveSource=true|false`, honored as given.
2. A `COVE_REPO` environment variable pointing at a Cove checkout.
3. The conventional `../cove` sibling checkout beside the monorepo root, auto-detected.
4. The published NuGet packages at the pinned `$(CoveSdkVersion)` — the clean-clone default.

The important property is that the boolean and the path are resolved **together**, in that order,
into a single `$(CoveRepoRoot)`, and every consumer reads that property instead of re-deriving a
path. They used to be separate: the boolean honored `COVE_REPO` while the targets file hardcoded the
sibling. A `COVE_REPO` with no `../cove` beside it then selected "local" and resolved nowhere,
producing a project graph with no Cove reference at all and a wall of `CS0234`/`CS0246` rather than
any diagnostic naming the cause.

The reverse mismatch was just as costly. Requiring the resolved boolean before either path arm
matters because the test projects' `Cove.Data`/`Cove.Core` references gate on `Exists(...)`, not on
the boolean — so an explicit `-p:UseLocalCoveSource=false` still pulled a sibling checkout back in
whenever one existed. Every gate that *measures* rather than compiles (`dotnet format`, the analyzer
build, package advisories) then reported the sibling repo's findings as this repo's:
unreproducible against a clean clone, and invisible in CI, which has no sibling.

## Central package management

Every NuGet version lives in the root `Directory.Packages.props` with
`ManagePackageVersionsCentrally=true`; individual `.csproj` files carry version-less
`<PackageReference>` items. One version, one place, for all four projects plus the shared libraries.

`Cove.Sdk` and `Cove.Plugins` are the deliberate exception: their `<PackageVersion>` is a reference
to the `$(CoveSdkVersion)` property in `Directory.Build.props` rather than a literal. That property
is the single source of truth `scripts/validate-extension-repo.mjs` reads as the host-SDK version
floor, and the host SDK must be hand-bumped in lockstep with the Cove host it is built against —
Dependabot cannot bump a property indirection, and here that is the desired behavior rather than a
limitation.

The Entity Framework Core pins follow the same reasoning from the other direction. EF Core is
host-provided, so those versions track the Cove host's own pin: below it is a package downgrade the
moment a test project reaches `Cove.Data`, and above it resolves a different assembly identity than
the host loads.

## The host-assembly boundary

Cove loads each extension into an `AssemblyLoadContext`. If an extension ships its own copy of an
assembly the host already provides, the two copies are different types with the same name, and every
cast across the boundary fails at runtime. That failure is the reason for the whole contract below.

Host-provided assemblies are referenced with `Private=false` so they compile but never copy local.
The denylist of what must never ship is a single shared file,
[`.github/DLL_DENYLIST.json`](https://github.com/alextomas955/cove-extensions/blob/main/.github/DLL_DENYLIST.json):
`Cove.Core`, `Cove.Plugins`, `Cove.Sdk`, the three `Microsoft.EntityFrameworkCore*` assemblies,
`Npgsql` and `Npgsql.EntityFrameworkCore.PostgreSQL`, `Pgvector` and `Pgvector.EntityFrameworkCore`,
and `MediatR.Contracts`.

`Cove.Sdk` ships targets that strip those from the publish set. On the NuGet path they are imported
transitively from the package's `buildTransitive/` folder. On a **local `ProjectReference`** they are
not — package build logic does not flow across a project reference — so the root
`Directory.Build.targets` imports `Cove.Sdk.targets` explicitly. Without that import the local-source
publish set leaks `Cove.Core.dll` and friends, and the two build paths would produce different
packages.

Stripping is not trusted, it is verified. `scripts/strip-verify.mjs` inspects the published output
and fails if a denylisted assembly is present, if the extension's own entry assembly is missing, if
anything the catalog entry names in `requiredBundledDlls` is absent, or if an absolute build path
leaked into the shipped JSON. CI runs it on every build, and the local release-proof harness calls
the same implementation, so what CI enforces and what a local run proves cannot drift.

The inverse case is equally explicit. A first-party dependency that is *not* host-provided must ship,
and the catalog says so per extension: Renamer declares `requiredBundledDlls: ["System.IO.Hashing"]`
for the hash its cross-volume mover verifies copies with, while Whisparr Sync declares `[]` because
its entire outbound HTTP stack is host-provided shared framework.

## Shared first-party modules

`shared/` is the repository's cross-extension code, and its membership rule is strict: a module
belongs here only if it is business-agnostic *and* usable by both extensions unchanged. The level is
decided by reach, never by a directory name. Extension-local code shared between several of that
extension's own features lives in that extension's `common/` folder — both UI bundles have one — and
is never called "shared". A Whisparr-branded logo component is `common/ui/`, not a shared package,
however reusable it looks.

Four modules meet the bar today:

| Module | Kind | How it is consumed |
| --- | --- | --- |
| `shared/Cove.Extensions.Shared` | C# library | A `ProjectReference` from each extension project. Options store, minimal-API permission helper, JSON factory, `RunAsSystem`, `SingleWriterBlobStore<T>`. |
| `shared/cove-extensions-ui` | TypeScript/React | A Vite `resolve.alias` plus a tsconfig path into its raw `src/`. Field primitives, their pure logic, overlay and action helpers, and the shared Vite config factory. |
| `shared/Cove.Extensions.Shared.Testing` | C# test support | A `ProjectReference` from each `*.Tests` project. Fake stores and the `TierTraitGuard`. |
| `shared/Cove.Extensions.Shared.Testing.Cove` | C# source only | Linked `Compile` items, guarded on the Cove checkout existing. Fakes that need host types. |

`Cove.Extensions.Shared` is first-party, so unlike the Cove references it is **not** `Private=false`
and is absent from the denylist — it copies local and ships bundled as `Cove.Extensions.Shared.dll`
inside each extension's package. Being first-party is exactly why it may ship: the host does not
provide it, so there is no second copy to conflict with.

`cove-extensions-ui` is resolved from raw TypeScript source rather than installed from a registry,
so Vite transforms it through the same pipeline as the consuming bundle's own `src/`. Its `src/` is
flat — `index.ts` beside `primitives.tsx`, `primitivesLogic.ts`, `actions.ts`, `postAction.ts`,
`overlay.ts`, `entityPickerLogic.ts` — because at that size the filename suffix already carries the
kind, and a `ui/`/`lib/` split would only restate it.

That package also owns `createExtensionViteConfig`, the single library-mode Vite config both UI
bundles call. It holds the host import-map externals — React and its runtimes, `@tanstack/react-query`,
`lucide-react`, and the host's own `@cove/runtime/components` and `@cove/runtime/api` — which must
stay external for the same type-identity reason the .NET denylist exists: a second React would break
hook identity, and a bundled copy of a host component would fork its identity away from the host's
React tree.

The fourth module has no `.csproj` on purpose. Its two fakes need host types from `Cove.Core` and
`Cove.Data`, so they are pulled in as ordinary per-project `Compile` items guarded on the checkout
existing, rather than as a conditional `ProjectReference` — IDEs do not fully respect reference
conditions. On a CI runner with no Cove checkout the files simply drop out and the pure-unit test
tier still compiles.

## Documentation topology

There are three documentation trees, and one rule connecting them: a page has exactly one source
file, and the site reads that file where it lives.

The Docusaurus site in `website/` runs the classic preset's docs plugin at `routeBasePath: '/'`,
serving `website/docs/` — this page, and the Contributing section — from the site root. Two extra
`plugin-content-docs` instances then source each extension's own folder directly:
`extensions/Renamer/docs` at `/extensions/renamer`, and `extensions/WhisparrSync/docs` at
`/extensions/whisparr-sync`. Nothing is copied into the site, so there is no site copy to drift from
the extension it documents. The offline search theme is given both arrays in parallel for the same
reason.

Only the extra instances carry a custom plugin id. Leaving the default instance's id alone is what
avoids the Docusaurus issue that trips when every docs instance is given one.

The sidebar is autogenerated from the folder tree, so adding a page is adding a file. Internal links
are checked at build time — `onBrokenLinks` is `'throw'` — which is why the cross-links at the top of
this page point at verified routes.

The third tree is the repository's own GitHub-facing files. `README.md`, `CONTRIBUTING.md`,
`SECURITY.md`, and `CODE_OF_CONDUCT.md` stay at the repo root, unduplicated, and the site navbar
links to them on github.com. The division of labor is that the README is a short entry point plus
build and release detail, while the user-facing story — what an extension does, its settings, its
tokens — lives on the site.

## Where to go next

- [Branching](./contributing/branching.md) and [Releasing](./contributing/releasing.md) — the
  process built on top of the catalog and the tag-gated matrix described above.
- [Extension authoring patterns](./contributing/authoring-patterns.md) — the conventions an
  extension's *internals* follow, once the repository-level contract here is satisfied.
- [Adding an extension's e2e suite](./contributing/authoring-e2e.md) — how a new extension joins the
  shared end-to-end harness.
