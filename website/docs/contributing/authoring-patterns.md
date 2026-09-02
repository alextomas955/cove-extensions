---
sidebar_position: 4
---

# Extension authoring patterns

This page explains how an extension in this repo is _shaped_ — the folder conventions, the wire
contract, and the correctness rules every extension shares. It is the reasoning behind the terse rules
in the repo-root `CLAUDE.md`; when you add an extension or reshape one, follow those rules and read here
for the why.

## Classify a module, then place it

Every module is exactly one of six kinds:

| Kind               | What it is                                                          |
| ------------------ | ------------------------------------------------------------------- |
| **Feature**        | a capability slice that coordinates one use case end-to-end         |
| **Domain**         | pure, deterministic rules — no I/O, unit-testable with zero mocks   |
| **Model**          | a data or wire shape                                                |
| **Infrastructure** | the only code that touches I/O — HTTP, DB, disk, host store, timers |
| **UI primitive**   | business-agnostic presentation                                      |
| **Tooling**        | runs at commit/CI/build time, never at extension runtime            |

Classify a file by what it _is_, then place it by its tier's convention. Modules depend downward
(toward models) and sideways onto shared code — never upward, and never across sibling features.

On the frontend, lint enforces the last part rather than leaving it to review: importing a sibling
feature slice is an error, and the route between two features is `common/` or the extension entry.
Nothing needs configuring per extension — the rule finds each UI bundle through `catalog.json`.

## Structure each tier to its own idiom

The backend and the frontend are separate build artifacts that talk over an HTTP wire. Their honest
seam is the wire contract, not a shared folder layout — so do not force the two to mirror each other.

- **The C# backend** is sliced by capability at the project root, alongside foundation folders. An
  extension that is one rich capability layers it by domain instead, which is what Renamer does today:
  `Engine/`, `Planner/`, `Execution/` beside `Api/`, `Contracts/`, `Jobs/`, `Options/`. An extension
  covering several capabilities would carry a folder per capability there instead, each named for what
  it does.
- **The UI** is sliced by feature directly under `src/`, next to `index.ts`, `wire/`, and `common/`.
  Renamer's are `settings/` and `rename-action/`.

Where a capability spans both tiers, give both halves the same name, differing only in each tier's
casing. That is intended alignment - you find both halves instantly - not duplication to collapse.

### No `features/` wrapper

Slices live at the tier root, not under a `features/` directory. That wrapper is a large-app pattern;
in a plugin that is almost entirely slices it adds a level that separates nothing. A sub-concern
reachable from only one slice nests under it — Renamer's dry-run modal is `settings/dry-run/` because
you open it only from the settings panel.

### Name by capability, not by entity

Slice the backend by what the code _does_, never by the entity it touches. A folder named for an
entity becomes a home for everything that entity touches, which is how a slice stops having a
boundary. So monitoring two kinds of entity is one monitoring folder, not one folder per kind; and a
folder that projects an entity's status is named for the projection, never given the entity's bare
name, which would masquerade as an entity home. Renamer's `Planner/` and `Execution/` are the shape to
copy: both are named for the work, and neither names a media kind.

### Let the filename suffix carry the kind

Inside a slice, the suffix tells you the kind - `*Logic.ts` is domain, `*Store.ts` is infrastructure,
`use*.ts` is a data hook, `*.tsx` is a view, `wire/api.ts` is the generated wire model; in C#, a
`*Guard` or `*Projector` is domain, a `*Port` is infrastructure, and a `*Contracts` unit holds wire
shapes. Read the suffixes actually in use off the tier before adding a new one, and add one only when
it names a kind the existing set cannot. Don't add `ui/`, `lib/`, or `model/` sub-folders that only restate the suffix. Give a
section its own folder only when it holds more than one file.

## Two levels of shared code

"Shared" is reserved for **repo-level, cross-extension** code — the frontend package
`shared/ui-shared` and the backend package `shared/Cove.Extensions.Shared`. A module earns a
place there only by being business-agnostic and reusable by _every_ extension unchanged.

Before adding to one of them, check whether the host already provides it. Cove exposes shared runtime
modules and a component library to extensions, and reimplementing those is the most common way this
rule gets violated.

The frontend package's `src/` is **flat**: `index.ts` sits beside `primitives.tsx`, `primitivesLogic.ts`,
`actions.ts`, `postAction.ts`, `overlay.ts` and `entityPickerLogic.ts`. That is the suffix-as-kind rule
applied, not an omission — at this size a `ui/` and `lib/` split would only restate what the filenames
already say.

Code shared by several features of a _single_ extension is not "shared" — it lives in that extension's
own `common/` folder, which _is_ split into `common/ui/` and `common/lib/`. A component carrying one
extension's branding is local, so it belongs in that extension's `common/ui/`, not in the repo-level UI
package.

**The deciding test is reach, not a directory name.** Ask whether every extension could use the module
unchanged: if yes it is repo-level, if only one extension can it belongs in that extension's `common/`,
and if only one feature can it stays inside that feature's slice. Business-agnosticism is what the test
measures — never whether the code happens to be presentational.

## The wire contract

A response an extension writes is camelCase, property names and enum values alike, because that is the
convention on the external boundary (the Cove host). Every response the UI reads is a projection type,
never a live domain object, so the backend can evolve without breaking the wire.

**A request is not automatically the same.** The host serializer binds incoming properties
case-insensitively, so a request body is free to carry another spelling, and one here does: an
extension that persists a settings blob sends that blob back in the spelling it stores. Read the casing
off the server for each direction rather than assuming one convention covers both, and expect a request
body an extension parses itself to answer to whatever options that parse names.

**Derive the contract; do not restate it.** A hand-written TypeScript wire type is checked by the
compiler against itself and never against the server, so a wrong one still type-checks and every field
then reads `undefined` at runtime with nothing failing anywhere. That has shipped here. Where you
cannot derive a type, pin the wire values in a test whose expectation you transcribe by hand from the
server's own spelling — an expectation computed from the module it checks agrees with itself forever
and reports nothing.

The C# handler signatures are the source of truth, and each tier has one home:

- **C#** - a `Contracts/` unit in the assembly. Define an enum once, beside the behavior that owns
  it, and give it a neutral home only when a second slice needs it - a vocabulary file that exists
  before a second reader does is a layer with one member. Declare an enum's wire spelling on the enum
  TYPE, never on a serializer options
  object: an options-level converter outranks a type attribute rather than agreeing with it, so a
  second declaration can drift and win silently.
- **TypeScript** — `src/wire/api.ts`, generated by `npm run generate:wire` and gitignored. Import it
  with `import type` so it erases at runtime and a consuming `*Logic.ts` module stays offline-gate
  clean. Do not hand-write a parallel `contracts.ts`; that is the restatement this rule removes.

Between the two sits `wire/openapi.json`, committed. A test emits it from the shipped endpoint
registrations and fails when the committed copy no longer matches, so the document cannot go stale
without a red build, and the TypeScript is a pure function of it.

## Frontend conventions

Use named exports (the one default export is `defineExtension` in `index.ts`) and avoid barrel files.
Do data access through a named `use*` hook that lives beside its store, never a raw fetch in a
`useEffect`, and don't collect hooks into a `hooks/` folder. Overlays rest on one small hand-rolled
foundation shared by the popovers and dialogs: a focus, keyboard and outside-click hook offering two
navigation modes, menu and dialog. It is deliberately neither a component library nor the native
`<dialog>` element - the two modes keep Escape and focus semantics that differ on purpose, and either
alternative would flatten that difference or add a second focus manager. Use only the host's Tailwind token classes
and never `dangerouslySetInnerHTML`. Where the host contract can't be read from the code — slot props
arriving at the top level, a component key that must match a C# literal byte-for-byte — leave a short
comment.

## Correctness rules that must not regress

- Background database reads run as the System principal through one shared seam — under an anonymous
  principal Cove's authorization filters return zero rows with no error.
- A best-effort `catch` that swallows an error still emits exactly one structured log line; nothing
  fails silently.
- On shutdown, work classifies as cancelled, never as failed.
- When a backend can't honor a role or a version, it simply doesn't implement that role interface —
  there is no capability probe and no version-mismatch throw to trip over.
- A journal that must persist lives as rows in a table the extension owns, bounded by a retention
  window, never as one growing value under a host store key: a value under one key puts every writer
  into a read-modify-write race, and one such value has already grown large enough to fail an
  extension's whole settings page.

## Testing and tooling

Mirror the source folders so a test is easy to find from its subject. What a test depends on is
carried by which of an extension's two test projects it sits in. The split is on one question - does
the test need a real `CoveContext` from `Cove.Data`? If it does, it goes in the Cove-dependent project
and runs only where a Cove source checkout exists. If it does not, it goes in the other, and it runs
on every leg. Put a `CoveContext` test in the wrong one and the build fails on it.

The refusal runs one way only. A test that needs `Cove.Data` cannot compile in the checkout-free
project, so that mistake is impossible. A test that needs nothing from Cove compiles perfectly well in
the Cove-dependent project - and then runs only where a checkout exists, disappearing from the
checkout-free leg and from the Windows leg without anything reporting it. Nothing checks that
direction, so it is on you: if a test does not reach a `CoveContext`, put it in the checkout-free
project.

The lightweight "bare" CI leg is a compile-and-pure-logic smoke test; the containerized end-to-end job
is the real safety gate. Copy-paste, dead-export, dependency-drift, and import-direction checks run as
merge gates in the lint workflow. Only a check a CI workflow runs is a gate — an entry in the local
hook runner is advice a contributor can skip, so wire a check you need enforced into a workflow.
