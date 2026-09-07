---
slug: /
---

# alextomas955 Cove Extensions

This site documents the `cove-extensions` monorepo — a single git repository that holds
extensions for [Cove](https://github.com/yourcove/cove), a self-hosted media library platform.

> **Community project.** These are personal, third-party extensions maintained by
> [alextomas955](https://github.com/alextomas955). They are not affiliated with, or endorsed by,
> the Cove project.

Extensions add features to a Cove instance without changing Cove's core. Each extension in
this repo lives in its own folder, builds against the Cove SDK, and ships and versions
independently.

## The extensions

| Extension | What it does | Needs Cove |
| --- | --- | --- |
| [Renamer](/extensions/renamer) | Bulk-renames — and optionally relocates — library items from metadata templates you control. Previews every change, updates file and database record together, and can undo the last batch. | 1.3.1 or later |
| [Whisparr Sync](/extensions/whisparr-sync) | Connects Cove to a self-hosted [Whisparr](https://whisparr.com) v3 (Eros) or v2 instance: imports what Whisparr acquires, reflects what Cove already has, and lists the scenes a studio or performer is missing. | 1.3.1 or later |

## Install an extension

Extensions are released one at a time. Each release attaches a single `.zip` — named for the
extension id and version, such as `com.alextomas955.renamer-0.3.0.zip` — to a GitHub release in
[the repository](https://github.com/alextomas955/cove-extensions/releases).

1. Check your Cove version against the table above. An extension will not load on a Cove older
   than the version it declares.
2. Install the `.zip` into your Cove instance from its release URL, or unpack it into Cove's
   extensions folder.
3. In Cove, open **Settings → Extensions** and confirm the extension is enabled.
4. Follow that extension's own guide — [Rename your library](/extensions/renamer/guide) or
   [Connect to Whisparr](/extensions/whisparr-sync/guide) — for first-run setup.

## Where to go next

- **[Repository architecture](./architecture.md)** — how the monorepo is shaped: the extension
  catalog, the shared build wiring, what an extension may and may not ship, and the modules both
  extensions share. Start here to understand the repo rather than a single extension.
- **[Contributing](/contributing)** — how to work in this repo: branching, releasing, repository
  configuration, the authoring patterns every extension follows, and end-to-end tests.
- **Extension documentation** — the per-extension doc sets linked in the table above. Each one
  carries its own guide, settings reference, architecture page, and changelog.
