# Cove Extensions Monorepo

## Project

This is the Cove extensions monorepo — a single git repository holding one or more Cove
extensions, following [yourcove](https://github.com/yourcove)'s official
`multi-extension-repo-template` pattern. Today it ships two extensions — **Renamer**
(`extensions/Renamer/`, metadata-driven rename/relocate) and **Whisparr Sync**
(`extensions/WhisparrSync/`, Cove↔Whisparr import/reconcile/push) — plus first-party shared modules
(`shared/Cove.Extensions.Shared` for C#, `shared/cove-extensions-ui` for the UI bundles) both
consume. See `README.md` for the extension list and dev setup.

## Registry and CI

- `extensions/catalog.json` is the extension registry and the source of truth CI reads to compute
  its build matrix. Each entry declares that extension's `name`, `id`, `path`, `tagPrefix`,
  `projectPath`, `manifestPath`, `versionSourcePath`, and (optionally) `uiPath`. Adding a new
  extension's release capability is a `catalog.json` edit, not a workflow-logic change.
- CI (`.github/workflows/build.yml`) is a catalog-driven `validate → build → release` matrix: every
  catalog entry builds on every PR (no `paths:` filtering); a release for one extension is cut by
  pushing a tag of the form `<tagPrefix>v<semver>` (e.g. `renamer/v1.0.0`), which builds, strip-
  verifies, and packages only that extension.
- See `website/docs/contributing/branching.md` and `website/docs/contributing/releasing.md` for the
  full branching and release process.

## Build wiring

The root `Directory.Build.props`/`Directory.Build.targets` auto-wire `Cove.Sdk` (which
transitively carries `Cove.Plugins` + `Cove.Core`) for every project in the monorepo, either
against a local sibling `../cove` checkout (auto-detected, or via `COVE_REPO`) or from NuGet.
Individual extensions' `.csproj` files should not add their own direct Cove reference or restate
the relative-path math to `../cove` — that's centralized here.

Build the whole monorepo from this root:

```sh
dotnet build CoveExtensions.slnx
```

**Central Package Management:** every NuGet package version lives in one root
`Directory.Packages.props` (`ManagePackageVersionsCentrally=true`); individual `.csproj` files carry
version-less `<PackageReference>`s. `Cove.Sdk`/`Cove.Plugins` are the one exception — their
`<PackageVersion>` references the `$(CoveSdkVersion)` property in `Directory.Build.props` (the single
source of truth `scripts/validate-extension-repo.mjs` reads as the host-SDK version floor), so the
host SDK stays hand-bumped in lockstep with the local `../cove` host rather than Dependabot-managed.

**Cove source selection precedence:** an explicit `-p:UseLocalCoveSource=true` > a `COVE_REPO`
checkout > the `../cove` sibling auto-detect (relative to the monorepo root) > the published NuGet
packages (pinned `CoveSdkVersion`). On a local ProjectReference the `Cove.Sdk` host-assembly
stripping rules (which ship in the package's `buildTransitive/`) are not auto-imported, so the root
`Directory.Build.targets` explicitly imports `Cove.Sdk.targets` to strip the transitive
`Cove.Core.dll`; on the NuGet path that import comes transitively from the package.

## Extension authoring

Every extension in this monorepo is a dynamically-loaded `Cove.Sdk` plugin. The rules below apply to
all of them (Renamer and Whisparr Sync today, more later); an extension's own `CLAUDE.md` adds only
what is specific to it. Shared first-party code lives in `shared/` — `Cove.Extensions.Shared` (a
`ProjectReference` that ships bundled, since it is first-party and not host-provided) and
`cove-extensions-ui` (resolved into each UI bundle from raw TS source via a Vite alias).

- **Implement `IExtension` from `Cove.Plugins`** (`using Cove.Plugins;`) — typically by subclassing
  `FullExtensionBase`. `extension.json` is the load manifest (`id`, `name`, `entryDll`, `jsBundle`,
  `minCoveVersion`); its `entryDll` MUST match the built assembly name.
- **Do not add a direct Cove reference or a `Directory.Build.props` in an extension `.csproj`.** The
  `Cove.Sdk` reference and the source-selection math are wired once at the repo root (see *Build
  wiring*).
- **Never bundle host-provided assemblies.** `Cove.Core` / `Cove.Plugins` / `Cove.Sdk`, EF Core,
  Npgsql, and Pgvector are provided by the host and referenced `Private=false`. Shipping them causes
  `AssemblyLoadContext` type-identity mismatches at runtime. `Cove.Sdk.targets` strips them — verify
  the published output rather than trusting it.
- **Never write to Cove's database directly** (the "Stash" anti-pattern) — direct SQLite/Postgres
  writes are schema-fragile and corrupt the DB. Go through `CoveContext` + `SaveChangesAsync`.
- **Register the extension in `extensions/catalog.json`** so CI can build and release it.

## Extension authoring patterns

The rules above are the load/build contract; these are the durable *shape* rules every extension
follows. A new extension or a reshape obeys them; deviate only with a recorded reason. (The
human-facing version lives at `website/docs/contributing/authoring-patterns.md`.)

- **Six-kind taxonomy as a lens, not a folder tree.** Every module is exactly one of **FEAT**
  (capability slice) · **DOM** (pure logic) · **MODEL** (data/wire shape) · **INFRA** (I/O:
  HTTP/DB/disk/store/timers) · **UIP** (business-agnostic UI primitive) · **TOOL** (commit/CI/build
  gate). Classify by what a file *is*, then place it by its tier's convention. The one dependency
  rule: depend **downward** (toward MODEL) and sideways onto shared/UIP, never **upward** and never
  **across sibling features**.
- **Structure per tier — do NOT mirror FE and BE.** The honest full-stack seam is the wire contract,
  not a directory layout. C# backend = capability/vertical slices at the project root (`Ingest/ ·
  Matching/ · Monitor/ · Push/ · SceneStatus/`) alongside foundation folders (`Contracts/ · Adapters/
  · Client/ · State/ · …`); a single rich capability is domain-layered instead (Renamer's `Engine/ ·
  Planner/ · Execution/`). UI = feature slices directly under `src/` (`settings/ · scene/ · monitor/
  · …`) next to `index.ts`, `contracts.ts`, `common/`. Seeing the same capability name on both tiers
  (`Monitor/` + `monitor/`) is intended alignment, **not** duplication to collapse.
- **No `features/` wrapper.** Slices live at the tier root, not under `features/`/`Features/` — that
  is a large-app (FSD/Bulletproof-React) pattern that discriminates nothing in a plugin that is almost
  entirely slices. Sub-concerns reachable from only one slice NEST under it (Renamer's
  `settings/dry-run/`). Add a `features/` grouping later only if a tier grows a real body of
  non-feature code.
- **Capability naming, not entity naming (C#).** Slice by *what the code does*, never by entity —
  **no `Studio/`, `Performer/`, or `Scene/` folder**. Entity ops live in the capability acting on them
  (studio + performer monitoring → `Monitor/`; scene add → `Push/`). Name a projection for its
  capability: `SceneStatus/`, never bare `Scene/`.
- **Legibility is suffix-as-kind, not deep segment folders.** TS `*Logic.ts`=DOM, `*Store.ts`=INFRA,
  `use*.ts`=INFRA hook, `*.tsx`=view, `contracts.ts`=MODEL; C# `*Service`/`*Guard`/`*Projector`/
  `*Detector`=DOM, `*Port`/`*Client`=INFRA, `*Contracts`/`*Models`=MODEL. No `ui/lib/model/` segments
  inside a slice — the suffix carries it. Folder-per-section only when a section holds more than one
  file.
- **Two-level shared — "shared" is repo-level only.** Repo-level `shared/` = cross-extension only:
  exactly `shared/cove-extensions-ui` (FE) + `shared/Cove.Extensions.Shared` (BE). The FE package's
  `src/` is **flat** — `index.ts` beside `primitives.tsx`, `primitivesLogic.ts`, `actions.ts`,
  `postAction.ts`, `overlay.ts`, `entityPickerLogic.ts` — because at seven files the suffix already
  carries the kind and a `ui/`/`lib/` split would only restate it (the suffix-as-kind rule above).
  **The level is decided by reach, never by a directory name:** a module belongs at repo level only if
  it is business-agnostic *and* reusable by both extensions unchanged. Extension-local multi-feature
  code lives in that extension's own `common/`, which **is** split (`common/ui` + `common/lib`, in both
  extensions today), and is **never** called "shared" — e.g. `WhisparrLogo.tsx` is Whisparr-branded, so
  it is `common/ui/`, not `cove-extensions-ui`.
- **Models live with their behavior; only wire contracts get a home.** Do not strip behavior into a
  data-only "models layer" (anemic-domain anti-pattern). C# wire DTOs → a `Contracts/` unit in the
  SAME assembly, cross-cutting enums defined once in a neutral `Vocabulary.cs`. TS wire types → one
  `contracts.ts` per UI `src/` root, consumed via `import type` (erases at runtime, so `*Logic.ts`
  stays offline-gate-clean).
- **Wire is all-camelCase — properties AND enum values, no island.** It is the convention on both
  external boundaries (Cove `JsonSerializerDefaults.Web`, Whisparr's Servarr API). Every UI response is
  a projection DTO, never a live domain/EF type. Codegen stays deferred (~17 stable types; offline
  gates ARE the drift check); re-trigger only past ~30 mirrored types.
- **UI conventions.** Named exports only (the `defineExtension` default in `index.ts` is the one
  default export); no barrels bar `index.ts` + the curated `shared/` public barrel; data access
  through a named `use*` hook beside its `*Store.ts`, never a raw `request()` in `useEffect`; no
  `hooks/` folder (co-locate; a generic cross-feature hook → `common/lib`); multi-root stores stay
  hand-rolled but unify on `resourceEntryLogic` + `useSyncExternalStore`; overlays are the two
  native/hand-rolled primitives (popover + native `<dialog>`), no library; host-token Tailwind classes
  only (no arbitrary values, no `dangerouslySetInnerHTML`).
- **Correctness standards.** Background DB reads run as System via one `RunAsSystemAsync` seam (an
  Anonymous principal returns zero rows with no error). No silent swallow — a best-effort `catch` emits
  exactly one `[LoggerMessage]` line. Cancellation on shutdown classifies `Cancelled`, never `Failed`.
  A version/role a backend can't honor is simply **not registered** as a role interface — no
  `Supports*` probe, no `VersionMismatch` throw. Single-writer `IExtensionStore` journals are bounded
  and compact via the thin, opt-in `SingleWriterBlobStore<T>` (mirror `RevertLog.Compact`); a journal
  nothing renders reduces to a tiny bounded status record.
- **Nothing may be O(library).** Libraries here reach **millions** of files, so treat library size as
  unbounded input in every design — storage, memory, wire payload, and browser state alike. Concretely:
  never persist a per-file collection to `IExtensionStore` (Cove's bulk `GET /api/extensions/{id}/data`
  serialises every value an extension owns, so ONE oversized value 500s that extension's whole settings
  page, survives reinstall, and is only removable with SQL); never accumulate a per-file list on the
  managed heap before writing it; never ship a response whose row count grows with the library. Persist
  **aggregates**, serve **rows paged on demand** — planning and projection here are pure per entity, so a
  slice computes identically to a full pass. A row cap is NOT the remedy: it converts a hard failure into
  a silently truncated answer, which is worse. When a design cannot avoid a full pass (a count, a
  reconcile), the pass may be O(library) in *time* but its output must still be O(1) in size. Renamer's
  `last-scan-result` reaching 595 MB (~1.04M files) is the worked example.
- **Testing.** Every xUnit test class carries exactly one class-level `[Trait("Tier", …)]` — L0
  pure-logic · L1 host-double (real SQLite `CoveContext` / `TempDir` / principal) · L2 in-process
  endpoint (the `NewExtension` harness + permission-gated handlers) · L3 containerized / live-instance
  e2e — so a tier runs in isolation (`dotnet test --filter "Tier=L0"`) as a design fact independent of
  the csproj Compile-Remove build fact. A shared reflection guard (`TierTraitGuard` in
  `Cove.Extensions.Shared.Testing`, driven by a per-project coverage test) fails the suite if any test
  class lacks a Tier trait, so the taxonomy stays exhaustive by mechanism rather than by hand. Tests
  MIRROR their source folders; only test-only groups (`TestSupport/`, `TransportSmoke/`, e2e) sit
  outside the mirror. The bare-CI (cove-absent) leg is a compile/pure SMOKE — any test that references
  a Cove source type is Compile-Removed there and is L1/L2 — and is NOT the safety gate; the
  containerized e2e job is the required safety gate. Keep `*Logic.ts` offline-gated so pinned
  wire-casing enums fail a gate on drift.
- **Tooling as merge gates.** jscpd, knip, syncpack, and import-boundaries run as **blocking** merge
  gates. Rollout: land each tool, get it green on `main`, THEN flip to blocking.
- **Adding a new extension:** register in `catalog.json` → manifest + `FullExtensionBase` → structure
  by tier (no `features/`, capability-not-entity, suffix-as-kind, `common/` for local shared) → wire
  camelCase + `Contracts/`/`contracts.ts` → UI (named exports, `use*` hooks, no `hooks/`) → correctness
  (RunAsSystem, no silent swallow, `Cancelled`, bounded stores) → tests (Tier traits, mirror source,
  safety behind e2e) → green under the merge gates → docs (README + site + CHANGELOG + own CLAUDE.md).

## C# comments and XML docs

**The code explains the what; comments explain the why.** Default to no comment: if the code already
says it plainly, a comment only adds drift risk. The subtle invariants that keep an operation
correct (concurrency assumptions, TOCTOU windows, external-system quirks) are exactly what earns a
comment.

- **Write a comment only for:**
  - Domain / business rules not visible in the code (e.g. a routing-precedence order).
  - Non-obvious edge cases and the reasoning behind them.
  - External-system quirks — the Cove ABI, host API limitations, platform path rules.
  - Security / safety reasoning (e.g. resolving symlinks late to keep a TOCTOU window minimal).
  - Perf / concurrency / data-consistency assumptions (e.g. `CoveContext` is not thread-safe).
  - Temporary workarounds — and only with a removal condition (why it is here, when it can go).
  - Public-API contracts a caller cannot infer from the signature — null behavior, whether a method
    throws, ordering guarantees.
- **Never write:**
  - **Name restatement** — a comment that just repeats a member, variable, or type name.
  - **Tutorial narration of obvious code** — e.g. a comment above a `foreach` saying it loops.
  - **Change-narrative / author voice** — phrasings that describe the edit or speak as the person
    making it rather than describing the code.
  - **Process, workflow, or tooling jargon** — comments must describe the code, not the workflow used
    to produce it. No references to GSD, planning phases, tickets, tasks, milestones, or any other
    development framework or agent-workflow vocabulary. A contributor reading the source should never
    have to know what process wrote it; the shipped code is tool-agnostic.
  - The first two are the primary deletion targets when cleaning up existing comments.
- **XML docs (`///`)** are earned by judgment, not mandated. Earn them on the public / SDK-facing
  surface (the `IExtension` boundary, interfaces, shared-vocabulary contract types) where the tag
  states a contract a caller cannot read from the signature; discouraged-when-redundant on internal
  app code; none on test or generated code. Each tag earns its place: no `<param>` that merely
  restates a parameter name. `<remarks>` is the home for the *why* and the edge cases; `<exception>`
  is genuinely useful because it documents which throws a caller must catch.

```csharp
// BAD — the summary just restates the signature; it adds nothing a reader cannot see.
/// <summary>Gets the user by id.</summary>
User GetUserById(int id);
```

```csharp
// GOOD — the summary states the contract; <remarks> carries the why and the edge case.
/// <summary>Resolves <paramref name="candidate"/> to its canonical on-disk path.</summary>
/// <remarks>
/// Resolves symlinks as late as possible so the gap between the safety check and the move stays
/// small (a smaller TOCTOU window). Returns the canonical path, or throws when the target escapes
/// the allowed roots.
/// </remarks>
string ResolveCanonicalPath(string candidate);
```

- **AI tools (Claude Code / Copilot / Cursor):** when generating code, do **not** add comments or XML
  docs unless they clear the *why* bar above. Match the surrounding comment density. Never narrate
  the edit; never restate a name.
- **Analyzer posture (why there is no forced-doc rule):** `GenerateDocumentationFile` is ON only so
  `IDE0005` (dead `using`) is reported on build; `CS1591` (missing XML doc on a public member) is
  deliberately silenced via `NoWarn`, so doc *presence* is not mandated. There is intentionally **no**
  StyleCop / Sonar / Meziantou doc-enforcement analyzer — those would manufacture exactly the filler
  this policy forbids. Do not add one. See the root `Directory.Build.props` for the actual lever state.

## TypeScript / React comments

The same discipline as the C# section applies to the extensions' TypeScript/React UI bundles: **the
code explains the what; comments explain the why.** Default to no comment; earn each one.

- **Write a comment only for:**
  - Host-contract quirks the code can't show — e.g. a Cove UI slot passes its context as **top-level
    props** (`props.studio`), not `props.context.*`; `OverrideComponent` and `actionType:"context-menu"`
    are silent no-ops; the video detail-rail tab icon is host-drawn and cannot be a custom image.
  - Wire-format contracts — the PascalCase JSON field names that must match the C# options model, or
    a pinned enum casing the server emits.
  - Non-obvious UI reasoning — why a fetch is deduped through a shared store, why a popover renders
    via a portal (to escape action-row overflow clipping), why a control is disabled.
  - The invariant a `*Logic.ts` module exists to hold — it is extracted precisely so it can be
    unit-tested without a DOM.
- **Never write:** name restatement; tutorial narration of obvious JSX/hooks; change-narrative or
  author voice; or process / workflow / tooling jargon — no GSD, planning-phase, ticket, or
  agent-workflow references. The shipped bundle is tool-agnostic; a reader should never need to know
  what process wrote it.
- **JSDoc (`/** */`)** is earned, not mandated. Earn it on the extension's public surface — the
  `defineExtension` entry, exported slot/tab components, and the `*Logic.ts` contracts — where it
  states something the signature cannot (what a component reads from props, what a pure function
  guarantees, an ordering rule). Skip it on obvious internal helpers; none on tests.
- **AI tools (Claude Code / Copilot / Cursor):** match the surrounding comment density; never narrate
  the edit; never restate a name.
- The `check-classes` gate guards host-JIT Tailwind-class validity and XSS (no
  `dangerouslySetInnerHTML`), **not** comment presence — it manufactures no doc filler, and no
  doc-presence lint should be added on the TS side either.

## Documentation upkeep

When a change alters an extension's settings, configuration options, public API, or user-facing
behavior, update that extension's docs in the same change — `extensions/<Name>/docs/`, its
`README.md`, and `CHANGELOG.md` as applicable, plus the matching docs-site page. Docs are part of
done. Do not defer them to a later change.

## How to write documentation

The docs site (`website/docs/`) follows a research-backed playbook (Diátaxis + Google/Microsoft
style guides). When writing or reviewing docs:

- **Keep the four Diátaxis modes separate** — don't blend them on one page:
  - **How-to guide** (task-oriented, for a competent user): a real-world goal, written from the
    *user's* perspective, a sequence of actions; omit teaching. → a "Rename your library" guide.
  - **Reference** (information-oriented, neutral, factual): its structure **mirrors the product**
    (group settings by the UI panel section, in the same order the user sees). → the settings and
    token references.
  - **Explanation** (understanding-oriented): the "why" / design & safety model. → `ARCHITECTURE`.
  - **Tutorial** (a single happy-path lesson): usually unnecessary for one extension.
- **Settings reference — per-setting anatomy:** name/label (as in the UI) · one neutral sentence of
  what it does · default · valid values/type · a short example when it clarifies. Uniform rows →
  a table; a setting needing nuance (routing precedence, templates) → a subsection with an example.
  Note settings that exist but aren't in the UI as an explicit "advanced / not exposed" callout.
- **Template/token systems:** lead with a complete worked example (a full template → the exact
  filename it produces), then a graduated series (name → +year → +resolution …); pair every token
  with its rendered output (`token = example`); group tokens into thematic tables; document syntax
  rules explicitly (Renamer's `{ … }` group collapses when its inner tokens are empty; `$$` is a
  literal `$`; absent tokens are omitted). List the shipped presets. Document tokens *as they are*
  (`$title`, `$resolution`) — do NOT impose an UPPERCASE_UNDERSCORE convention (that's for
  user-replaced CLI placeholders, not a fixed token vocabulary).
- **README vs site:** the GitHub README is a short entry point (what it is + a link to the site) and
  holds dev/build/release detail; the *user* story (what it does, settings, tokens) lives on the site.
- **Style:** second person ("you"), active voice, present tense; sentence-case headings; task
  headings use the bare infinitive ("Add a per-studio destination"), concept headings use noun
  phrases ("Naming templates"), never an -ing gerund; lead with the most important info and put
  conditions before instructions ("If X, do Y"); show an example before a paragraph of prose;
  progressive disclosure (common path first, advanced behind its own heading); screenshots sparingly.
