---
paths:
  - "**/*.cs"
---

# C#

`.claude/rules/csharp-comments.md` carries the comment rules. This file carries the shape rules.

## Check the host before building infrastructure

`FullExtensionBase` already provides job definitions, migrations, event handlers, scan
participation and the extension store. A job runner receives an `IJobProgress` and a
`CancellationToken`; the progress object carries the sub-task and the unit tallies the host's own
job view renders, so an extension needs no progress model of its own. Cove's services arrive through
DI: the host-provided `DbContext`, `IJobService`, and the repositories in `Cove.Core.Interfaces`.
`EndpointAuthorizationExtensions` carries the endpoint authorization helpers.

Write infrastructure only where the host has none, and say in the code which host capability did not
fit. Renamer polls its own `job-status` route because the host's job route requires unrestricted
read, which a scoped account does not have. That is the standard: a named reason, not silence.

## An abstraction needs a boundary

- No one-method service that forwards to another, and no wrapper that adds a name and nothing else.
- An interface earns its place as a dependency inversion, never as a test seam. Renamer's
  `IRenamerDataPort` exists because production takes no runtime dependency on `Cove.Core` entities.
  One implementation with no inversion behind it is a class.
- A member no production code calls is deleted, not kept for symmetry.
- Validate at the boundary: request bodies, filesystem paths, responses from another service.
  Inside, rely on the invariants the boundary established. A guard against a state the framework
  rules out hides the real failure when one arrives.

## Endpoints

Every route carries a `.WithName()`. It is the ASP.NET endpoint name and the `operationId` in the
emitted document at once. Endpoint names must be unique across the host process every installed
extension shares, so prefix each with the extension's own: `RenamerGetOptions`, never `GetOptions`.
A generator names its output from the `operationId` and falls back to the method and path without
one, so a rename is a breaking change to the generated client.

## Layout

Capability slices at the project root beside the foundation folders (`Api/`, `Contracts/`,
`Options/`). One rich capability may layer by domain instead, as Renamer's `Engine/`, `Planner/`
and `Execution/` do. Name a folder for what the code does, never for an entity. A partial class file
is named for the one thing it holds and holds only that. A job body lives with the work it runs,
never with the endpoint that enqueues it. The suffix tells the kind: `*Guard` and `*Projector`
domain, `*Port` infrastructure, `*Contracts` wire.

## Simplifying must not drop a guarantee

Collapsing a layer is where the extension contract in the root `CLAUDE.md` gets lost: the System
principal for self-started work, per-entity authorization against the caller, cancellation
classified as `Cancelled`, the single log line on a swallowed exception, and the bounded table
behind a persisted journal. Filesystem safety and the undo path keep their ordering too. Check the
diff against that list before calling a simplification equivalent.
