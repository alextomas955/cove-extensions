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
