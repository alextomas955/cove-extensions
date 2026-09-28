# Cove Extensions Monorepo

One git repo holding several Cove extensions plus the first-party code they share.
`extensions/catalog.json` lists the extensions. `shared/` holds the cross-extension C# and UI
packages. `README.md` and the docs site under `website/docs/contributing/` explain the repo for
people.

Launch Claude Code at this root, never inside an extension's folder. With a sibling `../cove`
checkout, add `--add-dir ../cove` so SDK source is readable.

This file holds only what Claude cannot read from the code. Keep it under 200 lines. Path-specific
guidance (comment policy, docs style) lives in `.claude/rules/` and loads only when matching files
are touched. When a change makes a rule here false, rewrite or delete the rule in the same change.

## Writing style

This applies to replies, documentation, code comments, commit messages, and these instruction
files.

Say what you mean, in the literal phrase. Mannered prose substitutes metaphor for direct statement
("a dial worth turning" for "a parameter worth varying", "earns its keep" for "still matters") and
is imprecise as well as showy.

- One idea per sentence. One rule per bullet. Break a paragraph after three or four sentences.
- Plain dash, never an em dash.
- A heading says what the section contains. No slogans.
- In a reply, lead with the outcome and keep supporting detail short. Match a document's length to
  what the task needs. No filler sections and no repeated summaries.
- Shipped code, docs, changelogs, and commit messages name no planning or workflow tooling. No
  phase, plan, milestone, ticket, or agent references.

## Commands

Run from the repo root unless stated. The same script name means different things in different
`package.json` files, so check the directory before running one.

Toolchain: .NET 10 SDK, Node at the Volta pin in `package.json`, npm only. No yarn, no pnpm. Run
every `.ps1` in this repo with `pwsh`, never Windows PowerShell 5.1.

```sh
dotnet build CoveExtensions.slnx                   # every project; warnings are errors
npm ci --no-workspaces && npm run generate:wire    # before any UI typecheck, UI test or root lint
npm run format:cs                                  # not raw `dotnet format`, which also formats ../cove
npm run lint && npm run knip && npm run jscpd && npm run syncpack && npm run format:check  # knip first needs a plain `npm ci`: it reads the e2e workspaces' configs
node scripts/validate-extension-repo.mjs           # catalog paths, solution membership, host floor
npm test                                           # tests for scripts/
```

- Per UI bundle, from `extensions/<Name>/src/<Name>.Ui/`: `npm ci` (each UI has its own lockfile),
  then `npm run verify`. Use `cd <dir> && npm ci`, never `npm ci --prefix`.
- C# tests: `dotnet test --project <path to .Tests.csproj>`. Do not pass `--nologo`. The testing
  platform rejects it and reports zero tests.
- Deploy into the running dev Cove: `pwsh extensions/Renamer/scripts/deploy-dev.ps1`.
- Test tiers and the e2e suite: `website/docs/contributing/testing.md`.

## Registry and CI

- `extensions/catalog.json` is the registry. CI (`.github/workflows/ci.yml`) builds every entry
  on every PR and every push to `main`. Its `required-checks` job gates on every other job, so a new
  job must be added to that job's `needs`. Pushing a tag `<tagPrefix>v<semver>`, for example `renamer/v1.0.0`, releases that one
  extension. Adding an extension's release is a catalog edit, not a workflow change.
- Read the catalog's field set from the file and from `scripts/validate-extension-repo.mjs`. Do not
  copy the field list into prose. A copy goes stale.
- Only a check that a CI workflow runs can block a merge. A lefthook entry is advice. lefthook is
  absent, with no warning, when npm withholds install scripts.

## Build wiring

- `Directory.Build.props` and `Directory.Build.targets` at the root add `Cove.Sdk` to every
  project. An extension `.csproj` adds no Cove reference and no `Directory.Build.props` of its own.
- Cove source precedence: `-p:CoveSourceMode=source|none` (or `COVE_SOURCE_MODE`), then
  `-p:CoveRepoRoot` (or `COVE_REPO`), then the `../cove` sibling, then NuGet at `CoveSdkVersion`.
  Read the result from `UseLocalCoveSource` and `CoveRepoRootResolved`, not from the switch you set.
  A build that fell back to NuGet compiles fewer tests and still reports success.
- Every package version lives in `Directory.Packages.props`. `Cove.Sdk` is the exception. Its
  version is `CoveSdkVersion`, read from the `minCoveVersion` in the `extension.json` beside the
  project, falling back to `CoveMinVersion` in `Directory.Build.props`. So an extension compiles
  against the host it advertises, and raising one extension's floor leaves the others alone.
- The validator reads `CoveMinVersion` as the host floor. Never edit the floor to make a version
  check pass.
- Never bundle host-provided assemblies (`Cove.*`, EF Core, Npgsql, Pgvector). The host loads its
  own copy of anything in its dependency closure, so a bundled one is dead weight and logs a warning
  at load. `Cove.Sdk.targets` strips them. On the local ProjectReference path the root targets file
  imports it explicitly. Verify the published file set against the catalog's `artifacts` list.

## Extension contract

- Implement `IExtension` from `Cove.Plugins`, normally by subclassing `FullExtensionBase`.
  `extension.json` is the load manifest. Its `entryDll` must equal the built assembly name.
- Never write to Cove's database directly. Go through the host-provided `DbContext` and
  `SaveChangesAsync`. `CoveContext` and the rest of `Cove.Data` are host internals, not contract.
- Run work the extension starts by itself as System through `RunAsSystemAsync` in
  `shared/Cove.Extensions.Shared`. An anonymous principal returns zero rows with no error.
- A job serving a user's request keeps that elevation and authorizes each entity against its caller.
- A swallowed exception emits exactly one `[LoggerMessage]` line.
- Cancellation on shutdown classifies as `Cancelled`, never `Failed`.
- A capability a backend cannot honor is a role interface it does not implement. No `Supports*`
  probe, no version-mismatch throw.

## Library size is unbounded

Libraries reach millions of files. Nothing may grow with the library.

- Never persist a per-file collection to `IExtensionStore`. Cove's bulk data route serializes every
  stored value, so one oversized value breaks the extension's whole settings page and survives
  reinstall.
- Never build a per-file list in memory.
- Never return a response whose row count grows with the library. Persist aggregates and page rows
  on demand.
- A row cap is not a fix. It truncates with no error.
- A journal that must persist is rows in a table the extension owns, bounded by a retention window,
  never one value under a store key.

## Code shape

- Dependencies point toward models and to shared code, never the other way and never to a sibling
  feature. `eslint.config.mjs` enforces the sibling rule for UI code.
  `website/docs/contributing/authoring-patterns.md` carries the responsibilities to place a module
  by.
- C#: capability slices at the project root beside foundation folders (`Api/`, `Contracts/`,
  `Options/`). One rich capability may layer by domain instead, as Renamer's `Engine/`, `Planner/`,
  `Execution/` do. Name folders for what the code does, never for an entity.
- UI: feature slices directly under `src/` beside `index.ts`, `wire/`, `common/`. No `features/`
  folder and no `hooks/` folder. A sub-concern used by one slice nests under it.
- The filename suffix tells the kind: `*Logic.ts` pure, `*Store.ts` infrastructure, `use*.ts` data
  hook, `*.tsx` view. In C#: `*Guard` and `*Projector` domain, `*Port` infrastructure, `*Contracts`
  wire. Add a suffix only for a kind the set lacks. No `ui/`, `lib/`, or `model/` folders inside a
  slice.
- Repo-level `shared/` is for code every extension can use unchanged. Code shared inside one
  extension goes in that extension's `common/ui` or `common/lib`, never in `shared/`. Check whether
  the host already provides a module before adding one to `shared/`.
- A `*Logic.ts` module imports only its relative siblings, so it runs with no environment and no
  mocks. ESLint enforces this.
- A partial class file is named for the one thing it holds, and holds only that. A job body lives
  with the work it runs, never with the endpoint that enqueues it.
- An interface member with no production caller is deleted, not kept for symmetry. A seam that only
  a test double implements is not a boundary.
- Renamer's data port exists because production takes no runtime dependency on Cove.Core entities.
  An interface here is a dependency inversion of that kind, not a test seam.

## UI conventions

- Named exports only. The one default export is `defineExtension` in `index.ts`.
- Data access goes through a `use*` hook beside its `*Store.ts`, never a raw request in `useEffect`.
- Overlays use the menu or dialog mode of the focus and keyboard hook in `shared/ui-shared`. No
  overlay library. No native `<dialog>`: `showModal()` makes the host page inert, covers the host's
  own overlays, and can let a repeated Escape close it while the page is blocking a close.
- Host Tailwind token classes only. No `dangerouslySetInnerHTML`.

## Wire contract

- Responses are all camelCase, property names and enum values alike. Declare an enum's wire
  spelling with `[JsonConverter(typeof(CamelCaseStringEnumConverter))]` on the enum type, never on a
  serializer options object. An options-level converter overrides the attribute.
- Every response is a projection DTO in a `Contracts/` unit, never an EF entity.
- Requests bind case-insensitively, so read the casing from the server for each direction. The
  settings blob an extension persists travels in the PascalCase spelling of its C# record and is
  absent from the wire document.
- A test emits `wire/openapi.json` from the shipped endpoint registrations and fails when the
  committed copy differs. Set `COVE_WIRE_DOC_UPDATE=1` for one test run to rewrite it.
- `npm run generate:wire` turns that document into the gitignored `src/wire/api.ts`. Consume it
  with `import type`.
- Never hand-write a TypeScript wire type. A wrong one type-checks and reads `undefined` at runtime.
  Where a type cannot be generated, record the values in a test whose expected value you copied
  from the server, not computed from the module under test.

## Tests

- A test mirrors its source folder. A group that tests no single source unit gets a folder of its
  own instead: test support, the wire document, cross-cutting invariants, concurrency, e2e.
- An extension has one backend test project. It references Cove's own source unconditionally, so the
  suite needs a checkout and refuses to build without one rather than running a smaller set.
- The cove-absent CI leg builds and publishes the extension and runs no tests. It proves the shipped
  assembly compiles against the published Cove packages alone. The containerized e2e job is the
  safety gate.
- A red e2e is usually the Cove container dying, not the UI. Search the job log for "is not
  running" before debugging the test.

## Branches and PRs

- A PR branches from `origin/main`, or from the branch it depends on when that work is unmerged.
- `main` is squash-merged, so a merged branch's commits are never ancestors of `main`. Rebase onto
  `origin/main` and force-push; never merge `main` back in. A PR whose merge base predates a squash
  replays files it did not touch, so check the diff size before asking for review.
- A review comment on moved code is about the code, not the move. Verify it against the source and
  say where the defect lives rather than fixing unrelated behavior inside a refactor.

## Update docs in the same change

A change to settings, options, API, or behavior updates `extensions/<Name>/docs/`, its `README.md`,
its `CHANGELOG.md`, and the matching docs-site page in the same change. A UI change also recaptures
any screenshot of the screen it altered. How to write them, and how to capture and strip images:
`.claude/rules/docs-writing.md`.

User docs are for people who run Cove, not developers. Steps and screenshots come first. Edge cases go on
the extension's troubleshooting page.

## Adding an extension

Register it in `catalog.json`. Ship a manifest and a `FullExtensionBase` subclass. Follow the shape
rules above. Add docs: README, site page, CHANGELOG, and a short `CLAUDE.md` holding only what is
specific to it. `README.md` has the human-facing steps.
