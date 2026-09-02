# Cove Extensions Monorepo

One git repo holding several Cove extensions plus the first-party code they share.
`extensions/catalog.json` lists the extensions. `shared/` holds the cross-extension C# and UI
packages. `README.md` and the docs site under `website/docs/contributing/` explain the repo for
people.

This file holds only what Claude cannot read from the code. Keep it under 200 lines. Path-specific
guidance (comment policy, docs style) lives in `.claude/rules/` and loads only when matching files
are touched. When a change makes a rule here false, rewrite or delete the rule in the same change.

## Writing style

This applies to replies, documentation, code comments, commit messages, and these instruction
files.

Mannered prose substitutes metaphor and flourish for direct statement: "a dial worth turning"
instead of "a parameter worth varying", "earns its keep" instead of "still matters". The phrases
display the writer instead of conveying the idea, and they are imprecise. Say what you mean. When a
literal phrase is available, use it.

- One idea per sentence. One rule per bullet. Break a paragraph after three or four sentences.
- Plain dash, never an em dash.
- A heading says what the section contains. No slogans.
- In a reply, lead with the outcome and keep supporting detail short. Match a document's length to
  what the task needs. No filler sections and no repeated summaries.

## Commands

Run from the repo root unless stated. The same script name means different things in different
`package.json` files, so check the directory before running one.

```sh
dotnet build CoveExtensions.slnx                   # every project; warnings are errors
npm ci --no-workspaces && npm run generate:wire    # before any UI typecheck, UI test, root lint, or knip
npm run format:cs                                  # not raw `dotnet format`, which also formats ../cove
npm run lint && npm run knip && npm run jscpd && npm run syncpack && npm run format:check
node scripts/validate-extension-repo.mjs           # catalog paths, solution membership, host floor
npm test                                           # tests for scripts/
```

- Per UI bundle, from `extensions/<Name>/src/<Name>.Ui/`: `npm ci` (each UI has its own lockfile),
  then `npm run verify`. Use `cd <dir> && npm ci`, never `npm ci --prefix`.
- C# tests: `dotnet test --project <path to .Tests.csproj>`. Do not pass `--nologo`. The testing
  platform rejects it and reports zero tests.
- Deploy into the running dev Cove: `pwsh extensions/Renamer/scripts/deploy-dev.ps1`. Always `pwsh`,
  never Windows PowerShell 5.1.
- Test tiers and the e2e suite: `website/docs/contributing/testing.md`.

## Registry and CI

- `extensions/catalog.json` is the registry. CI (`.github/workflows/build.yml`) builds every entry
  on every PR. Pushing a tag `<tagPrefix>v<semver>`, for example `renamer/v1.0.0`, releases that one
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
  version is `CoveSdkVersion`, derived from `CoveMinVersion` in `Directory.Build.props`.
- The validator reads `CoveMinVersion` as the host floor. Never edit the floor to make a version
  check pass.
- Never bundle host-provided assemblies (`Cove.*`, EF Core, Npgsql, Pgvector). A bundled copy in
  the extension's load context gives host types a second identity, and casts and DI then fail with
  no error. `Cove.Sdk.targets` strips them. On the local ProjectReference path the root targets file
  imports it explicitly. Verify the published file set against the catalog's `artifacts` list.

## Extension contract

- Implement `IExtension` from `Cove.Plugins`, normally by subclassing `FullExtensionBase`.
  `extension.json` is the load manifest. Its `entryDll` must equal the built assembly name.
- Never write to Cove's database directly. Go through `CoveContext` and `SaveChangesAsync`.
- Run background database reads as System through `RunAsSystemAsync` in
  `shared/Cove.Extensions.Shared`. An anonymous principal returns zero rows with no error.
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

- Classify every module as one of: feature slice, pure domain logic, data or wire model,
  infrastructure (I/O), UI primitive, tooling. Dependencies point toward models and to shared code,
  never the other way and never to a sibling feature. `eslint.config.mjs` enforces the sibling rule
  for UI code.
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

## UI conventions

- Named exports only. The one default export is `defineExtension` in `index.ts`.
- Data access goes through a `use*` hook beside its `*Store.ts`, never a raw request in
  `useEffect`.
- Overlays use the focus and keyboard hook in `shared/ui-shared`, which has a menu mode and a
  dialog mode. No overlay library and no native `<dialog>`.
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

- Tests mirror source folders. Only `TestSupport/`, `TransportSmoke/`, and e2e sit outside the
  mirror.
- The cove-absent CI leg is a compile-and-pure-logic smoke test. Anything that references a Cove
  type is Compile-Removed there. The containerized e2e job is the safety gate.
- A red e2e is usually the Cove container dying, not the UI. Search the job log for "is not
  running" before debugging the test.

## Update docs in the same change

- A change to settings, options, API, or behavior updates `extensions/<Name>/docs/`, its
  `README.md`, its `CHANGELOG.md`, and the matching docs-site page in the same change.
- Head a changelog entry with the version it ships as, never "Unreleased". List user impact only.
  Full rule: `website/docs/contributing/releasing.md`.
- Verify a documentation sentence against the code before writing it. A documented setting the
  code ignores is a defect. Where the code is wrong, describe what the code does and report the
  defect.

## Adding an extension

Register it in `catalog.json`. Ship a manifest and a `FullExtensionBase` subclass. Follow the shape
rules above. Add docs: README, site page, CHANGELOG, and a short `CLAUDE.md` holding only what is
specific to it. `README.md` has the human-facing steps.
