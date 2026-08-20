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

- **The C# backend** is sliced by capability (`Ingest/`, `Matching/`, `Monitor/`, `Push/`,
  `SceneStatus/`) alongside foundation folders (`Contracts/`, `Adapters/`, `Client/`, `State/`). If an
  extension is one rich capability, layer it by domain instead (Renamer's `Engine/`, `Planner/`,
  `Execution/`).
- **The UI** is sliced by feature directly under `src/` (`settings/`, `scene/`, `monitor/`) next to
  `index.ts`, the wire module, and `common/`.

Seeing the same name on both sides (`Monitor/` and `monitor/`) is intended alignment — you find both
halves of a capability instantly — not duplication to collapse.

### No `features/` wrapper

Slices live at the tier root, not under a `features/` directory. That wrapper is a large-app pattern;
in a plugin that is almost entirely slices it adds a level that separates nothing. A sub-concern
reachable from only one slice nests under it — Renamer's dry-run modal is `settings/dry-run/` because
you open it only from the settings panel.

### Name by capability, not by entity

Slice the backend by what the code _does_, never by the entity it touches. There is no `Studio/`,
`Performer/`, or `Scene/` folder — studio and performer monitoring both live in `Monitor/`, scene add
lives in `Push/`. A folder that projects a scene's status is `SceneStatus/`, never a bare `Scene/` that
would masquerade as an entity home.

### Let the filename suffix carry the kind

Inside a slice, the suffix tells you the kind — `*Logic.ts` is domain, `*Store.ts` is infrastructure,
`use*.ts` is a data hook, `*.tsx` is a view; in C#, `*Service` / `*Guard` / `*Projector` are domain,
`*Port` / `*Client` are infrastructure, `*Contracts` / `*Models` are models. Wire types are the one kind
you do not name by suffix, because you do not write them — see the wire contract below. Don't add `ui/`, `lib/`, or `model/` sub-folders that only restate the suffix. Give a
section its own folder only when it holds more than one file.

## Two levels of shared code

"Shared" is reserved for **repo-level, cross-extension** code — the frontend package
`shared/ui-shared` and the backend package `shared/Cove.Extensions.Shared`. A module earns a
place there only by being business-agnostic and reusable by _every_ extension unchanged.

The frontend package's `src/` is **flat**: `index.ts` sits beside `primitives.tsx`, `primitivesLogic.ts`,
`actions.ts`, `postAction.ts`, `overlay.ts` and `entityPickerLogic.ts`. That is the suffix-as-kind rule
applied, not an omission — at this size a `ui/` and `lib/` split would only restate what the filenames
already say.

Code shared by several features of a _single_ extension is not "shared" — it lives in that extension's
own `common/` folder, flat for the same reason: at this size a `ui/` and `lib/` split restates the
filenames. A component carrying one extension's branding is local, so it belongs in that extension's
`common/`, not in the repo-level UI package.

**The deciding test is reach, not a directory name.** Ask whether every extension could use the module
unchanged: if yes it is repo-level, if only one extension can it belongs in that extension's `common/`,
and if only one feature can it stays inside that feature's slice. Business-agnosticism is what the test
measures — never whether the code happens to be presentational.

## The wire contract

A response an extension writes is camelCase, property names and enum values alike, because that is the
convention on the external boundary (the Cove host). A request is not automatically the same. The host
serializer binds incoming properties case-insensitively, so a request body is free to carry another
spelling, and one here does: an extension that persists a settings blob sends that blob back in the
spelling it stores. Read the casing off the server for each direction rather than assuming one
convention covers both — and expect a request body an extension parses itself to answer to whatever
options that parse names, not to the response convention.

Keep the wire types in one home per tier: a `Contracts/` unit in the C# assembly, with cross-cutting
enums defined once in a neutral vocabulary file, and a single wire module per UI `src/` root that every
slice imports from. Import a wire type with `import type` so it erases at runtime and costs the bundle
nothing. Every response the UI reads is a projection type, never a live domain object, so the backend can
evolve without breaking the wire.

Treat a hand-declared wire type as an unverified assumption. The compiler checks your declaration against
itself and never against the server, so a response interface with the wrong casing still type-checks, and
every field then reads `undefined` at runtime with no error anywhere. That has shipped here. It is why the
UI's wire module is better derived from the server than written by hand: a type the server produces cannot
disagree with the server, which removes the mistake rather than looking for it afterward. Where you cannot
derive one, pin the wire values in a test whose expectation you transcribe by hand from the server's own
spelling. An expectation computed from the module it checks agrees with itself forever and reports
nothing, so the transcription is the whole of what makes such a pin worth keeping.

## Frontend conventions

Use named exports (the one default export is `defineExtension` in `index.ts`) and avoid barrel files.
Do data access through a named `use*` hook that lives beside its store, never a raw fetch in a
`useEffect`, and don't collect hooks into a `hooks/` folder. Overlays are two small primitives — a
popover and a native `<dialog>` — with no component library. Use only the host's Tailwind token classes
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
- Append-only stores are bounded and compact themselves; a journal nothing displays becomes a small
  status record rather than growing forever.

## Testing and tooling

Tag every backend test with a tier trait — pure-logic, host-double, in-process endpoint, or
containerized end-to-end — and mirror the source folders so a test is easy to find from its subject.

The in-process endpoint tier (`Tier=L2`) covers a suite that builds a real ASP.NET host and exercises
the endpoint pipeline through it: it sends requests to mapped routes, or reads back what route
registration produced. A suite that calls a handler as a plain method belongs to the host-double tier
however endpoint-shaped its subject, and so does one that executes a result against a
`DefaultHttpContext` — that context is an ordinary object and needs no host. The trait is a
class-level fact, so a class takes the tier of the strongest dependency any of its cases needs.

The lightweight "bare" CI leg is a compile-and-pure-logic smoke test; the containerized end-to-end job
is the real safety gate. Only checks a CI workflow runs are blocking merge gates — an entry in the
local hook runner is advice a contributor can skip, so wire a check you need enforced into a workflow.
