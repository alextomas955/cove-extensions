---
id: index
title: Whisparr Sync
slug: /
---

Whisparr Sync connects Cove to the Whisparr instance you run, so the two agree about what your
library holds.

## Current state

**Its capability surfaces are not built yet.** Installing this release adds a **Whisparr Sync** tab
under Settings → Extensions, and that tab says setup arrives in a later release. There is no
connection form, no matching and no syncing behind it, and nothing in it reads or changes your
library.

There is nothing for you to configure and nothing to try. This page exists so you can read what the
extension binds itself to before any of it arrives.

## Network access and credentials

These are the terms Whisparr Sync holds itself to. None of the surfaces they describe is built yet,
so read them as the contract the finished extension owes you rather than as behaviour you can
exercise today.

- **It calls outward to Whisparr, with the key you supply.** The instance it talks to is the one you
  configure, and it authenticates with your Whisparr API key. It reaches no other host.
- **Everything it exposes inbound is gated on a Cove permission.** Its API endpoints sit under
  `/api/extensions/com.alextomas955.whisparrsync`. Each one requires a permission Cove already knows
  about, and none of them answers an anonymous caller.
- **Only an action you start can cause a download.** Whisparr downloads because you asked Cove for
  something that needs it, never on a timer and never as a side effect of Cove looking at your
  library.
- **Your Whisparr API key stays on the server.** Cove holds it server-side and never sends it to
  your browser.

It also runs no scraper and no downloader code of its own; Whisparr does that work on its own
machine.

## Which Cove you need

Whisparr Sync needs **Cove 1.3.1** or newer. Cove refuses to install an extension that asks for a
newer host than the one you are running, and the extension listing hides a version your Cove is
below, so on an older Cove this extension is not offered to you in the first place.

## In this section

- [Changelog](./changelog.mdx) - user-facing changes, newest first.

## Install and build

For install, build, and local dev deploy instructions, see the extension's
[README on GitHub](https://github.com/alextomas955/cove-extensions/blob/main/extensions/WhisparrSync/README.md).
