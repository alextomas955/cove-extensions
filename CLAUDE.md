# Cove Extensions Monorepo

## Project

This is the Cove extensions monorepo — a single git repository holding one or more Cove extensions,
following [yourcove](https://github.com/yourcove)'s official `multi-extension-repo-template` pattern.
See `README.md` for the extension list and dev setup.

## What belongs in this file

Durable principles that guide judgment — the reasoning behind a rule, so a reader can apply it to a
case this file never anticipated. Not a snapshot of implementation state.

Concretely: no file inventories, no module counts, no incident figures, no configuration values
copied from where they actually live, no keyword lists. Those go stale silently, and a stale rule is
worse than no rule — a reader cannot tell whether the rule or the code is wrong. Prefer a stated
principle with one illustrative example over an enumeration.

**Treat every rule here as a claim, not as truth.** Verify against the live system before relying on
one. When a change invalidates a rule, rewrite or delete it in that same change; leaving both the old
rule and the new reality in place is the failure mode this section exists to prevent. Removing
content from this file is a valid outcome.

## Registry and CI

- `extensions/catalog.json` is the extension registry and the source of truth CI reads to compute its
  build matrix. Adding a new extension's release capability is a `catalog.json` edit, not a change to
  workflow logic.
- CI (`.github/workflows/build.yml`) is a catalog-driven `validate → build → release` matrix: every
  catalog entry builds on every PR, and a release for one extension is cut by pushing a tag of the
  form `<tagPrefix>v<semver>`, which builds, strip-verifies and packages only that extension.
- See `website/docs/contributing/branching.md` and `website/docs/contributing/releasing.md` for the
  full branching and release process.

## Build wiring

The root `Directory.Build.props`/`Directory.Build.targets` auto-wire `Cove.Sdk` (which transitively
carries `Cove.Plugins` + `Cove.Core`) for every project in the monorepo, either against a local
sibling `../cove` checkout or from NuGet. Package versions are pinned centrally there too. An
extension's `.csproj` should not add its own direct Cove reference, restate the relative-path math to
`../cove`, or carry a package version — all of that is centralized at the root.

Build the whole monorepo from this root:

```sh
dotnet build CoveExtensions.slnx
```

The one non-obvious piece: on a local project reference, the `Cove.Sdk` host-assembly stripping rules
ship inside the package and so are not imported, which is why the root targets file imports
`Cove.Sdk.targets` explicitly. That import looks redundant next to the NuGet path, where the same
import arrives transitively — remove it and the transitive `Cove.Core.dll` gets published.

## Extension authoring

Every extension here is a dynamically-loaded `Cove.Sdk` plugin. The rules below apply to all of them;
an extension's own `CLAUDE.md` adds only what is specific to it. First-party code shared across
extensions lives in `shared/` and ships bundled, since the host does not provide it.

- **Implement `IExtension` from `Cove.Plugins`** — typically by subclassing `FullExtensionBase`.
  `extension.json` is the load manifest, and its `entryDll` MUST match the built assembly name.
- **Do not add a direct Cove reference or a `Directory.Build.props` to an extension `.csproj`.** Both
  are wired once at the repo root (see *Build wiring*).
- **Never bundle host-provided assemblies.** `Cove.Core` / `Cove.Plugins` / `Cove.Sdk`, EF Core,
  Npgsql and Pgvector come from the host and are referenced `Private=false`. Shipping them causes
  `AssemblyLoadContext` type-identity mismatches at runtime. `Cove.Sdk.targets` strips them — verify
  the published output rather than trusting it.
- **Never write to Cove's database directly** — direct SQLite/Postgres writes are schema-fragile and
  corrupt the DB. Go through `CoveContext` + `SaveChangesAsync`.
- **Register the extension in `extensions/catalog.json`** so CI can build and release it.

## Extension authoring patterns

The rules above are the load/build contract; these are the durable *shape* rules every extension
follows. The shape rules themselves — the six-kind taxonomy, per-tier structure, no `features/`
wrapper, capability-not-entity naming, suffix-as-kind, two-level shared code, UI conventions,
correctness standards and test tiering — are stated with their reasoning at
`website/docs/contributing/authoring-patterns.md`. Read that page before adding or reshaping an
extension; what stays below is what that page does not carry.

- **Depend downward and sideways, never upward and never across sibling features.** This is the one
  dependency rule of the taxonomy, and the invariant the import-boundary lint enforces as an error.
- **Before adding to a repo-level shared package, check whether the host already provides it.** Cove
  exposes shared runtime modules and a component library to extensions, and reimplementing those is
  the most common way the two-level-shared rule gets violated.
- **Models live with their behavior.** Do not strip behavior out into a data-only "models layer" (the
  anemic-domain anti-pattern); only wire contracts get a home of their own.
- **A hand-declared wire type is an unverified assumption.** The wire is all-camelCase, properties and
  enum values alike, and the host serializer binds incoming properties case-insensitively, so there is
  one casing convention on the wire and not a separate request casing. A response interface that
  declares the wrong casing still type-checks — the compiler trusts the declaration — and then every
  field reads `undefined` at runtime with no error anywhere. That has shipped here. Whether the answer
  is generation, validation at the fetch boundary, or something else is an open question; what is
  settled is that "it type-checks" proves nothing about the wire.
- **Nothing may be O(library).** Libraries here reach millions of files, so treat library size as
  unbounded input in every design — storage, memory, wire payload and browser state alike. Persist
  **aggregates**, and serve **rows paged on demand**; planning and projection are pure per entity, so
  a slice computes identically to a full pass. A row cap is not the remedy — it converts a hard
  failure into a silently truncated answer, which is worse. Where a full pass is unavoidable (a count,
  a reconcile) it may be O(library) in *time*, but its output must still be O(1) in size. This has
  already happened here: a per-file collection persisted to the host's extension store grew large
  enough to fail that extension's entire settings page, survive reinstall, and require SQL to remove.
- **Only a check that a CI workflow runs can block a merge.** An entry in the local hook runner is
  advice a contributor can skip, so wire a check you need enforced into a workflow.
- **A gate must be able to fail, and must prove it ran.** A gate that inspects zero input and exits 0
  is a bug, not a pass — it reads as coverage while providing none, and can stay that way for weeks.
  Every gate reports what it actually examined and treats empty input as a hard failure. A gate that
  cannot run in an environment must say so loudly, never skip silently.
- **Justify a gate by a failure that is still possible.** When a design change makes a bug class
  impossible — the problem removed at its source — delete the gate in that same change; keeping it as
  extra defense costs maintenance and rots unnoticed. Prefer a structural guarantee (an allowlist, a
  single source of truth, a generated artifact) over a scan that looks for the bad outcome afterward.
- **Repo tooling is catalog-driven, never per-extension.** A script that understands one extension's
  layout multiplies with every extension added. Drive it from `catalog.json` so a new extension needs
  no tooling change.
- **Scripts must be portable.** This repo is developed on Windows and runs CI on Linux. Do not derive
  a filesystem path from `import.meta.url` via `.pathname` — on Windows it yields a leading-slash form
  that resolves to a doubled drive prefix; use `import.meta.dirname` or `fileURLToPath`. Do not assume
  POSIX binaries are on PATH. This class of defect has silently disabled gates here.
- **Check upstream and peer repos before building tooling.** The upstream template and other public
  Cove extension repos face the same boundary and the same problems; where one solves it more simply,
  prefer that and record the reason for deviating. Never hand-mirror a list or a value that already
  lives in the upstream build — copies drift.

## Comments and doc tags

**The code explains the what; comments explain the why.** One discipline governs C# and the TS/React
bundles alike, and the worked example below is C# only by convenience. Default to no comment — where
the code says it plainly, a comment only adds drift risk. The invariants that keep an operation
correct are what earn one.

- **Earned by:** a domain rule not visible in the code; a non-obvious edge case and its reasoning; an
  external-system quirk (the Cove ABI, a host API limit, a wire-format contract, a platform path
  rule); security, performance, concurrency or data-consistency reasoning; a temporary workaround,
  with the condition under which it can go; a public-API contract a caller cannot infer from a signature.
- **Never earned by:** restating a member, variable or type name; narrating obvious code; describing
  the edit or speaking as the person making it; naming the process that produced the change — the
  shipped code is tool-agnostic and a reader should never have to know what workflow wrote it. The
  first two are the primary deletion targets when cleaning up existing comments.
- **Doc tags (`///`, `/** */`) are earned by judgment, not mandated.** Earn them on the public surface
  — the `IExtension` boundary, shared contract types, an extension's entry and exported components —
  where the tag states what a signature cannot: null behavior, an ordering guarantee, which exceptions
  a caller must catch. None that restates a parameter name; none on tests or generated code.
- **AI tools (Claude Code / Copilot / Cursor)** are bound by every line above: match the surrounding
  comment density, never narrate the edit, never restate a name.
- **Do not add a doc-presence analyzer or lint rule** on either side: they manufacture exactly the
  filler this policy forbids. The root `Directory.Build.props` holds the C# lever state.

```csharp
// BAD — the summary just restates the signature; it adds nothing a reader cannot see.
/// <summary>Gets the user by id.</summary>
User GetUserById(int id);

// GOOD — the summary states the contract; <remarks> carries the why and the edge case.
/// <summary>Resolves <paramref name="candidate"/> to its canonical on-disk path.</summary>
/// <remarks>
/// Resolves symlinks as late as possible so the gap between the safety check and the move stays
/// small (a smaller TOCTOU window). Returns the canonical path, or throws when the target escapes
/// the allowed roots.
/// </remarks>
string ResolveCanonicalPath(string candidate);
```

## Documentation upkeep

When a change alters an extension's settings, public API or user-facing behavior, update that
extension's docs in the same change — its own docs, its `README.md` and `CHANGELOG.md` as applicable,
plus the matching docs-site page. Docs are part of done; do not defer them to a later change.

## How to write documentation

The docs site (`website/docs/`) follows Diátaxis plus the Google and Microsoft style guides.

- **Keep the four Diátaxis modes separate, never blended on one page** — a how-to guide is a task
  sequence written for a competent user, reference is neutral and factual, explanation carries the
  why, and a tutorial is a single happy-path lesson.
- **A reference's structure mirrors the product** — group settings by the UI panel section, in the
  order the user meets them, and give each its default and its valid values.
- **Show a complete worked example before explaining it**, and put conditions before instructions.
- **The README is an entry point, not the user story** — it says what the extension is, links to the
  site, and holds the dev/build/release detail; what it does and how to configure it lives on the site.
- **Style:** second person, active voice, present tense; sentence-case headings; task headings take
  the bare infinitive and concept headings a noun phrase, never an -ing gerund; progressive disclosure
  — the common path first, the advanced case behind its own heading.
