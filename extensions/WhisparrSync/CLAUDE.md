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

The connection, matching and sync surfaces are not built. What exists is the registration, a
read-gated probe endpoint, and a settings tab whose body says setup arrives later. Every
user-facing document this extension owns says so plainly, and they have to keep agreeing with the
code: a capability described before it exists is a defect, not a doc.

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

## Working on this extension

Whisparr Sync lives inside the monorepo and is not its own git repo: no own remote, no own CI
workflow. CI is defined once at the monorepo root and driven by this extension's entry in
`extensions/catalog.json`.
