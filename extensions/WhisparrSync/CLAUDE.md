## Project

A Cove extension (**Whisparr Sync**, `com.alextomas955.whisparrsync`) that keeps Cove in step with
the Whisparr instance its user configures. Cove holds the Whisparr API key server-side and calls
outward with it; Whisparr does its own downloading, on its own machine.

**Core Value:** the user's two libraries agree with each other without the user reconciling them by
hand, and nothing downloads that the user did not ask for.

> The monorepo-wide rules live in the repo-root `CLAUDE.md` and apply here too: the
> extension-authoring contract, build wiring and Cove source selection, the bans on bundling host
> assemblies and writing to the DB directly, the O(library) rule, the C# and TypeScript comment
> policies, and documentation upkeep. This file adds only what is specific to Whisparr Sync.

## Current shape

The connection surface is built: a settings tab that tests a connection, detects and separately
remembers each Whisparr generation, and registers the import callback in the connected instance. The
import path is not - the inbound callback authenticates a delivery and acknowledges it, and reads
nothing from its body. Matching and sync are not built either.

Every user-facing document this extension owns has to keep agreeing with that: a capability described
before it exists is a defect, not a doc, and the manifest description is one of those documents.

## Contract

- **The manifest is the sole source of identity.** `extension.json` carries the id, name, version,
  description, host floor and the loadable file names, and the host applies it to the instance
  before reading any of them. Every identity property on the base class is virtual, so declaring one
  in C# silently outranks the manifest and nothing reports the conflict. Override none of them.
- **The API route prefix is derived from that id at runtime**, never written as a literal:
  `/api/extensions/` plus `Id`, on instance members, so a route read before the host has applied the
  manifest throws rather than mounting the endpoints under the wrong prefix.
- **An endpoint declares its permission gate and re-checks the principal in its handler**, both
  reading one shared array. Both gates, because each covers what the other misses: the host's
  attribute filter is MVC-only and inert on a minimal-API endpoint, and a handler that only checks
  itself advertises nothing to the host. One array, because an endpoint advertising one gate while
  enforcing another still passes every test that drives the handler directly.
- **The settings component name is a cross-tier byte-identical string.** The name the C# UI manifest
  advertises and the key in the bundle's `defineExtension` components map must match exactly. A
  mismatch is silent: the host still draws the tab button, the heading and the manifest description,
  and renders the component as nothing with no error anywhere. Pin it by reading both files and
  comparing them to each other, never against a literal typed into the test.
- **A capability a generation cannot honour is a role the backend does not hold, never a probe.** The
  generation's capability set either carries the role interface or it does not, so a caller obtains it
  or is refused before any request leaves. There is no `Supports*` call to forget and no version
  mismatch to throw, and a capability the older generation gains later is one registration away.
  Bind the role to what was MEASURED, not to the field the measurement went looking for: the v2
  out-of-band role was first tied to a `headers` field v2 does not have, and v2 turned out to be able
  to carry the secret anyway.
- **The Whisparr API key lives in a table this extension owns, not in the extension store.** Cove's
  bulk extension-data route returns everything an extension stored, whole, so a key left in the store
  is a key a different route hands out. The callback secret is in the same table for the same reason.
  Everything else stays in the one O(1) options blob.
- **Exactly one route answers a caller holding no Cove permission**, the inbound import callback, and
  it says so with the SDK's own anonymous convention. A route that declares nothing is admitted
  anonymously too, silently and with only a host warning, which is an access tier no document states.
  That route is authenticated by a secret this extension mints and stores server-side, presented in a
  header, as Basic auth, or in the address; the permitted count is pinned by a test, so a second
  anonymous route is a failure rather than a line someone adds beside the first.

## Working on this extension

Whisparr Sync lives inside the monorepo and is not its own git repo: no own remote, no own CI
workflow. CI is defined once at the monorepo root and driven by this extension's entry in
`extensions/catalog.json`.
