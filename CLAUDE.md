# Cove Extensions Monorepo

## Project

This is the Cove extensions monorepo, following [yourcove](https://github.com/yourcove)'s official
`multi-extension-repo-template` pattern. See `README.md` for the extension list and dev setup.

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
  catalog entry builds on every PR, and a `<tagPrefix>v<semver>` tag releases exactly that extension.

## Build wiring

The root `Directory.Build.props`/`Directory.Build.targets` wire `Cove.Sdk` (which transitively carries
`Cove.Plugins` + `Cove.Core`) into every project, against either a local sibling `../cove` checkout or
NuGet. Package versions are centrally managed: they live in `Directory.Packages.props` and each
`.csproj` carries a version-less `PackageReference`. `Cove.Sdk`/`Cove.Plugins` are the one exception,
taking their version from `$(CoveSdkVersion)` in `Directory.Build.props` so the host SDK is bumped by
hand in lockstep with the host rather than by a dependency bot. An extension's `.csproj` should add
none of this — no Cove reference, no relative-path math to `../cove`, no package version.

The one non-obvious piece: on a local project reference, the `Cove.Sdk` host-assembly stripping rules
ship inside the package and so are not imported, which is why the root targets file imports
`Cove.Sdk.targets` explicitly. That import looks redundant next to the NuGet path, where the same
import arrives transitively — remove it and the transitive `Cove.Core.dll` gets published.

## Extension authoring

Every extension here is a dynamically-loaded `Cove.Sdk` plugin. The rules below apply to all of them;
an extension's own `CLAUDE.md` adds only what is specific to it. First-party code shared across
extensions lives in `shared/`; its runtime packages ship bundled, since the host does not provide
them, while the test-support packages there never ship.

- **Implement `IExtension` from `Cove.Plugins`** — typically by subclassing `FullExtensionBase`.
  `extension.json` is the load manifest, and its `entryDll` MUST match the built assembly name.
- **Do not add a direct Cove reference or a `Directory.Build.props` to an extension `.csproj`.** Both
  are wired once at the repo root (see _Build wiring_).
- **Never bundle host-provided assemblies.** `Cove.Core` / `Cove.Plugins` / `Cove.Sdk`, EF Core,
  Npgsql and Pgvector come from the host and are referenced `Private=false`. The host ignores a
  bundled copy and warns once naming it, so a leak costs weight, not correctness. `Cove.Sdk.targets`
  strips them, and `extensions/catalog.json` declares what ships, readable without a build.
- **Never write to Cove's database directly** — direct SQLite/Postgres writes are schema-fragile and
  corrupt the DB. Go through `CoveContext` + `SaveChangesAsync`.
- **Register the extension in `extensions/catalog.json`** so CI can build and release it.

## Extension authoring patterns

The rules above are the load/build contract. The durable _shape_ rules — folder conventions, wire
layout, correctness and test structure — are stated with their reasoning at
`website/docs/contributing/authoring-patterns.md`, which is the page to read before adding or
reshaping an extension. What stays below is what that page does not carry, plus the invariants whose
seam or vocabulary has to be named exactly to be actionable.

- **Every module is exactly one of six kinds** — FEAT (a capability slice) · DOM (pure logic) · MODEL
  (a data or wire shape) · INFRA (I/O: HTTP, DB, disk, host store, timers) · UIP (a business-agnostic
  UI primitive) · TOOL (commit, CI and build time). Classify a file by what it _is_, then place it.
- **Depend downward (toward MODEL) and sideways onto shared code and UI primitives, never upward and
  never across sibling features.** This is the taxonomy's one dependency rule, and it is enforced at
  lint time by the `boundaries/dependencies` block in `eslint.config.mjs`: a feature slice may reach
  `common/`, the shared package and `wire`, and a sibling slice is an error. Each extension's entry
  is deliberately left unclassified, because the entry is the one module allowed to reach any slice.
  The rule was briefly deleted on the grounds that its resolver named one extension's tsconfig by
  path inside config claiming to be extension-generic. That defect was real and the remedy was not:
  the resolver now derives its project list from `catalog.json`, so a second extension enters with no
  edit — the rule survives and the extension-specific path does not. Fix a generic rule's config
  rather than deleting the rule; a structural guarantee is worth keeping even when its wiring is
  wrong. The `*Logic.ts` import rule below is the narrower purity constraint and is separate.
- **Before adding to a repo-level shared package, check whether the host already provides it.** Cove
  exposes shared runtime modules and a component library to extensions, and reimplementing those is
  the most common way the two-level-shared rule gets violated.
- **Models live with their behavior.** Do not strip behavior out into a data-only "models layer" (the
  anemic-domain anti-pattern); only wire contracts get a home of their own.
- **A hand-declared wire type is an unverified assumption.** A TypeScript declaration is checked by the
  compiler against itself and never against the server, so a wrong one still type-checks and every field
  then reads `undefined` at runtime with no error anywhere. That has shipped here. "It type-checks"
  therefore proves nothing about the wire — and neither does a test whose expectation was computed from
  the module it checks, which agrees with itself forever. Only an expectation the server owns proves
  anything: a type the server itself produces, so no hand-written one exists to be wrong, or failing that
  a pin transcribed by hand from the server's own spelling, so drift fails loudly instead of reading
  `undefined`. Prefer the first — an error made impossible beats an error detected afterward — and keep
  the second wherever the first would only be checking itself.
- **A `*Logic.ts` module imports nothing but its relative siblings.** That is what keeps the L0 tier
  worth having — pure, mock-free, deterministic, runnable with no environment — so a test of one needs
  no setup, no doubles and no running service. What purity does **not** buy is drift detection, and
  conflating the two is worth guarding against: a pinned contract catches drift because its expectation
  was transcribed by hand instead of derived from the module under test, which is a property of how the
  expectation was written and holds wherever the pin lives. A pin inside a pure module that computes its
  expectation from that module is exactly as blind as one anywhere else. The import rule once held only
  as a side effect of a test runner that compiled each module alone in a temp dir, where a runtime import
  simply failed to resolve; that runner is gone, and the constraint is now stated directly as a
  `no-restricted-imports` rule in `eslint.config.mjs`. Prefer that shape generally: a structural
  guarantee that fails at lint time beats one that depends on how the suite happens to run.
- **Nothing may be O(library).** Libraries here reach millions of files, so treat library size as
  unbounded input in every design — storage, memory, wire payload and browser state alike. Persist
  **aggregates**, and serve **rows paged on demand**; planning and projection are pure per entity, so
  a slice computes identically to a full pass. A row cap is not the remedy — it converts a hard
  failure into a silently truncated answer, which is worse. Where a full pass is unavoidable (a count,
  a reconcile) it may be O(library) in _time_, but its output must still be O(1) in size. This has
  already happened here: a per-file collection persisted to the host's extension store grew large
  enough to fail that extension's entire settings page, survive reinstall, and require SQL to remove.
  Where a journal must persist at all, persist it as **rows in a table the extension owns**, bounded by
  a retention window rather than by a row cap, and never as one growing value under a store key. The
  undo journal is the worked example: a value under one key put every writer into a read-modify-write
  race and made "how much history is kept" a number someone had to choose, where a row insert is atomic
  and a whole batch either falls inside the window or is gone.
- **Two correctness invariants whose failure is silent.** A detached database body takes its scope from
  the one `RunAsSystem` seam, which hands one out already elevated to System — under a present but
  under-privileged principal Cove's authorization filters return zero rows with no error, so an empty
  result is the symptom of getting this wrong rather than of an empty library. Which principal arrives
  decides the symptom, and that is worth knowing before trusting a row count: Cove bypasses those filters
  for a NULL principal exactly as for System, and a queued job body has none — the host starts its queue
  processor before any request and holds the principal in an `AsyncLocal` — so on that path forgetting to
  elevate reads the whole library instead, and no row count at any tier can detect it. Assert on the
  principal at the command. A request path is the mirror image and must NOT elevate, because that would
  bypass its caller's own authorization. And on shutdown, work classifies as `Cancelled`, never `Failed`,
  so a clean stop is never read as a defect.
- **Every backend test class carries exactly one tier trait** — L0 pure logic · L1 host double · L2
  in-process endpoint · L3 containerized end-to-end — so a tier runs in isolation. A shared reflection
  guard (`TierTraitGuard`) fails the suite when a class lacks one, which keeps the taxonomy exhaustive
  by mechanism rather than by hand. **The L3 slot is permanently empty on the C# side, by design**:
  containerized end-to-end is the Playwright harness, which an xUnit process here has no way to stand
  up, so a behavior needing L3 gets an e2e spec rather than a fourth trait value. On the C# side the
  leg that gates a merge is the one compiled against the released host's own assemblies — the
  cove-absent leg proves less by construction and is not the safety gate.
- **Only a check that a CI workflow runs can block a merge.** An entry in the local hook runner is
  advice a contributor can skip, so wire a check you need enforced into a workflow.
- **A required context and the workflow that reports it change in opposite orders.** Removing goes
  protection first, so no window exists where a required context reports nothing; adding goes
  trigger first, so the check is already reporting before anything waits on it. Getting the second
  one backwards does not fail loudly — it holds every pull request at "waiting for status to be
  reported" until someone notices the workflow was never wired. The orders being mirror images is
  exactly why one of them cannot be reused as a rule for both.
- **A gate must be able to fail, and must prove it ran.** A gate that inspects zero input and exits 0
  is a bug, not a pass — it reads as coverage while providing none, and can stay that way for weeks.
  Every gate reports what it actually examined and treats empty input as a hard failure. A gate that
  cannot run in an environment must say so loudly, never skip silently.
- **A green is not evidence about the tree until you have shown the gate could see the tree.** The
  failure comes in more shapes than "inspected nothing", and every shape prints the same green: a
  gate reading a subset of the manifests it was meant to, a sweep whose pattern cannot match the
  spelling the stale sites actually use, a control edited on both sides of a diff so it agrees with
  itself. So make a gate fail on purpose before trusting it, and read its own report of what it
  examined rather than only its exit code. A gate passing against a budget with headroom is a
  passing gate, not a clean one; a record that deletes it should say which it was.
- **Justify a gate by a failure that is still possible.** When a design change makes a bug class
  impossible — the problem removed at its source — delete the gate in that same change; keeping it as
  extra defense costs maintenance and rots unnoticed. Prefer a structural guarantee (an allowlist, a
  single source of truth, a generated artifact) over a scan that looks for the bad outcome afterward.
  **And a gate's own reporting is not a gated surface.** Counters proving the prover ran, and tests
  asserting the grammar of a report sentence, are the recursion to refuse: they gate the instrument
  instead of the thing, and they grow without bound, because each new counter is itself unproven.
- **Repo tooling is catalog-driven, never per-extension.** A script that understands one extension's
  layout multiplies with every extension added. Drive it from `catalog.json` so a new extension needs
  no tooling change.
- **Scripts must be portable.** Development and CI both span Linux, Windows and macOS, so a script
  has no home platform it is allowed to be merely correct on. Do not derive a filesystem path from
  `import.meta.url` via `.pathname` — on Windows it yields a leading-slash form that resolves to a
  doubled drive prefix; use `import.meta.dirname` or `fileURLToPath`. Do not assume POSIX binaries are
  on PATH, and do not assume the newest shell: a macOS runner's bash predates `mapfile`, so a step
  mirrored verbatim from the Windows leg fails on every single run — invisibly, when that leg is
  advisory. This class of defect has silently disabled gates here.
- **Check upstream and peer repos before building tooling.** The upstream template and other public
  Cove extension repos face the same boundary and the same problems; where one solves it more simply,
  prefer that and record the reason for deviating. Never hand-mirror a list or a value that already
  lives in the upstream build — copies drift.
- **A second extension is the test of whether a rule generalizes.** If following one requires
  duplicating code or editing shared tooling, the rule is wrong, not the extension.

## Guardrails against regrowth

Deleting machinery one piece at a time does not stop it growing back — the milestone that cleared a
backlog of gates added new pins while doing it. These rules target the growth rather than its
instances, so they apply to a shape nobody has met yet.

- **One file per seam, not per phase.** A new test lives in the existing file for its seam; a new
  file requires a new seam. Left alone, each phase drops its own small file beside the last, and a
  handful of tests for one behavior ends up spread across as many files as there were phases, with
  no seam readable anywhere.
- **One tier per behavior, plus e2e.** A behavior gets the lowest tier that can observe it, and e2e
  on top when it is user-visible. Adding a second tier requires naming what the first cannot
  observe. The elevation tests are the model: the host installs its authorization filters only under
  its real database, so the host-double tier can prove which principal reached the command and
  genuinely cannot reproduce the row-level consequence — a fact the lower tier cannot see is what
  buys the higher one. Without that test, "prove it at another layer too" is how one intentional
  change comes to need four edits.
- **A pin freezes a cross-system contract only.** Wire bytes, the document the server itself
  produces, a constant two languages must agree on — yes. An internal constant or mapping with one
  consumer — no. That is a second place to edit for every intentional change, and by the wire rule
  above it detects nothing anyway: its expectation can only have come from the one module it checks.
- **Migrations carry expiry dates.** Any NEW one-shot conversion ships with the milestone that
  deletes it, written into the roadmap when the conversion is introduced — that is the one moment
  when anyone still knows what the condition for deleting it is, and skipping it is how one-shot
  code becomes permanent. **The pair shipping today is a stated exception, not an oversight**: by
  owner decision (2026-08-13) `OptionsMigration` and `JournalBlobMigration` have their retirement
  deliberately left unscheduled, to be called when it is actually due, because the release carrying
  them has not reached users and real stores still hold the legacy formats. Recorded here so their
  absence from any roadmap reads as the decision it is.
- **Cap what a plan may propose, before the code exists.** A plan proposing a new test file or a new
  gate must name the existing file it would otherwise extend, and the still-possible failure the
  gate guards. Those are the two questions an audit asks afterwards; asking them first is the whole
  difference between a review and a cleanup.
- **An incident narrative is a claim; a guard is earned by a red test under mutation.** A repo's
  documented incidents were written by the same process that wrote the code, so they are not
  independent evidence about it — they fail in both directions, blessing a boundary no test pins and
  dismissing as disposable the only check that pins its pair. Before keeping a guard as earned or
  deleting one as redundant, weaken it by one line and watch what goes red. Mutate by inverting a
  condition rather than by making code unreachable: an `if (false)` or a newly-unused parameter
  trips a compiler-warning-as-error setting and breaks the build, which proves nothing about the
  test.

  Three ways a mutation lies, each of them measured here rather than reasoned:

  - **Grep FINDS candidate pins; only mutation PROVES one is sole.** Where an assertion is _written_
    and what actually _detects_ a defect are different questions, and only the second licenses a
    deletion. A search-derived "sole pin" is a hypothesis: one such claim, when the behavior was
    actually dropped, reddened a whole family of tests that replay the same recorded data, while an
    unprescribed probe elsewhere reddened exactly one test in the entire suite. The second result is
    evidence of soleness; the first was a search result wearing the word.
  - **Confirm the mutation landed before believing a green.** A mutation that did not mutate produces
    a green indistinguishable from a deleted pin. Locate the site by symbol on every run, never by a
    line number carried over from an earlier one — an edit anywhere above shifts it — and read the
    mutated text back out of the file before reading the result.
  - **One green mutation does not prove a guard is unpinned.** Defense in depth absorbs it: where a
    second, lower layer also refuses the bad outcome, neutralizing the first changes nothing
    observable and the suite stays green. Defeat every layer that can catch it, or report the
    finding as "not pinned at this layer" rather than "not pinned".

## The re-add ledger

Machinery built for a repo many times this size was deleted rather than kept against a someday, and
the condition for its return written down in its place. **Every entry is a trigger, not a regret** —
"we removed this and maybe we should not have" gives a reader nothing to act on, while "this returns
when a second extension exists" is a decision already made, waiting on a condition. Check the
condition; do not re-litigate the removal. Where the reason also matters to someone reading the file
that changed, it is stated at that file too, since this list cannot reach them there.

Two entries are not removals at all. They are here so nobody restores something already running, or
spends a day rediscovering a door that is deliberately closed.

- **jscpd** — returns when a second extension exists in the catalog. Copy-paste _between_ two
  extensions is the duplication this repo actually fears, and it cannot exist while there is one;
  everything the gate can currently find is inside a single extension, where that extension's
  reviewer sees it anyway. Re-add both configs rather than only the budget one — the zero-threshold
  half is what catches a copied fixture, and it is the half a second extension needs.
- **syncpack** — nothing to re-add: it never left. It runs on every pull request in the lint
  workflow, and its second-extension trigger is already satisfied by construction, since its source
  globs are patterns and a new extension's manifests enter its subject set with no edit at all. This
  entry exists because a removal list once paired it with jscpd; writing "both return at the second
  extension" would put a false claim in this file, which is worse than saying nothing.
- **knip** — returns at the first real dead-export incident: a shipped export nothing imports, or a
  generated module that stopped being generated with nothing noticing. An incident, not a schedule.
  Zero findings is what a dead-export gate looks like when there are no dead exports, and it looks
  identical to one that read nothing; its real cost is not the dependency but the setup its run
  needs before it can see anything, down to generating the wire modules its own config names as
  entry points.
- **The node:test end-to-end tier** — returns at its first subscriber: a catalog entry declaring the
  node-tests path field. The tier was subscribed to through that field, no entry has declared one for
  a long time, and a tier with no subscriber gates nothing while reading as coverage. The validator
  deliberately still accepts the field, so a subscriber restores workflow steps and nothing in the
  scripts tree has to move.
- **A newest-GA leg on the pull-request axis** — returns on either condition: upstream publishes a GA
  above the declared floor, so that role stops collapsing onto the floor image and starts examining
  something the floor leg does not; or the daily schedule catches a break a pull request should have
  caught first. Until one fires, such a leg boots the same container the floor leg boots. The
  accepted cost is that upstream breakage above the floor surfaces within a day instead of on the
  next pull request. Carry one consequence with this entry: registry tag resolution is now a
  schedule-only path, and a path exercised daily rather than per-pull-request is easier to break
  unnoticed, so its pins are the last guard on it — a later trim reading them as over-testing would
  be deleting exactly that.
- **Assemblies mode on macOS and Windows** — foreclosed, not pending. There is no trigger and nothing
  to restore: extraction sources the host's assemblies from a running Linux container, which those
  runners cannot provide, so both legs are bare by design. Recorded so a future plan does not spend a
  day discovering it, and so "bare" is never read as a gap someone forgot to close.
- **TypeScript 7** — adopt at 7.1, **and** with `typescript-eslint` and `openapi-typescript`
  supporting it. Both halves, not either. This repo's wire contract is generated by the second and
  its one structural import guarantee is a rule of the first, so adopting the major ahead of them
  trades a generated contract and a lint guarantee for a version number, which inverts the whole
  point of having them. A dependency bot will propose the major long before the trigger fires; that
  proposal is not the trigger.

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
- **One canonical comment per invariant.** The invariant's owning site carries the one full
  statement; every other place that touches it gets at most a line naming that site. A retelling is
  not free redundancy — each copy drifts on its own, and once two disagree a reader cannot tell which
  is current. That has happened here: a comment asserted a mechanism that two other comments in the
  same assembly explicitly denied, about a data-loss-critical classification. Two consequences worth
  stating, because both are easy to get backwards. Reducing a retelling to a pointer requires the
  owning site to already carry the whole fact including the _why_, so complete it first or the
  reduction deletes the only copy of what made the rule matter. And a comment stating a contract
  nothing else states sheds nothing, however long it is — the unit of deletion is the retelling, never
  the file.
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

**A changelog entry is headed with the version it will ship as — never "Unreleased" — and carries
user impact rather than an inventory of the version.** The version is knowable when the first change
lands, since semver follows what the change does and not when a tag is pushed, so a placeholder is
only a second edit someone has to remember at release; forget it and the published changelog says the
shipped release has not shipped. Refactors, tests, internal renames and tooling do not appear at all,
because padding an entry with them buries the two lines that mattered; a change with no user-facing
effect earns a bullet only where a user would still want to know, such as a data-loss fix or a raised
host floor. The full rule, with the entry-shape guidance, is at
`website/docs/contributing/releasing.md`.

## How to write documentation

The docs site (`website/docs/`) follows Diátaxis plus the Google and Microsoft style guides.

- **Keep the four Diátaxis modes separate, never blended on one page** — a how-to guide is a task
  sequence written for a competent user, reference is neutral and factual, explanation carries the
  why, and a tutorial is a single happy-path lesson.
- **A reference's structure mirrors the product** — group settings by the UI panel section, in the
  order the user meets them, give each its default and its valid values, and flag a setting that
  exists but is not exposed in the UI as exactly that.
- **Show a complete worked example before explaining it**, and put conditions before instructions.
- **Document a token vocabulary as it is** — never impose an UPPERCASE_UNDERSCORE convention, which
  belongs to placeholders a user replaces, not to tokens the product defines.
- **The README is an entry point, not the user story** — it says what the extension is, links to the
  site, and holds the dev/build/release detail; what it does and how to configure it lives on the site.
- **Style:** second person, active voice, present tense; sentence-case headings; task headings take
  the bare infinitive and concept headings a noun phrase, never an -ing gerund; progressive disclosure
  — the common path first, the advanced case behind its own heading.
