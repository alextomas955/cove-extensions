---
id: index
title: Whisparr Sync
slug: /
---

Whisparr Sync connects Cove to the Whisparr instance you run, so the two agree about what your
library holds.

## What you can do today

Installing this release adds a **Whisparr Sync** tab under Settings → Extensions. From it you can:

- **Connect to your Whisparr instance.** Enter its address and API key, then test the connection.
  The result names the version the instance reported and which generation answered.
- **Keep both generations.** Whisparr v3 (Eros) and Whisparr v2 each keep their own address, key and
  recorded version, so switching to the other generation and back returns the first one unchanged.
  One of the two is the generation Cove uses; saving the card you are editing makes it that one.
- **Register the import webhook.** One click writes Cove's callback into the connected instance, or
  you can copy the address and paste it into Whisparr yourself. The status line below it says whether
  the callback is registered, and whether anything has arrived through it.
- **Take in what Whisparr imports.** A delivery through that callback is matched against your Cove
  library folders and the file is brought into your library. A delivery Cove cannot place is refused
  and counted, and the tab reports the outstanding refusals.

It also adds a Whisparr button to studio and performer pages, and one to those lists' selection bars:

- **Monitor a studio or a performer from its own page**, in one click, at either of Whisparr's two
  scopes. Monitoring itself asks Whisparr to search for nothing.
- **Ask Whisparr to search for what it monitors**, from that same menu. This is the one action in
  Whisparr Sync that downloads, it is named on its own row, and nothing else here reaches it.
- **Unmonitor from the same menu.** It stops Whisparr wanting new scenes and retracts nothing that
  is already wanted.
- **Do the same for a whole selection**, as one background job that reports in Cove's job list.

Read [Monitor a studio or a performer](./monitoring.md) before the first time you press it: the
wider scope marks a whole back catalogue wanted, and on Whisparr v3 (Eros) that choice is not undone
by narrowing the scope again.

For every setting on the tab, see the [Settings reference](./settings.md).

## Network access and credentials

- **It calls outward to Whisparr, with the key you supply.** The instance it talks to is the one you
  configure, and it authenticates with your Whisparr API key. It reaches no other host.
- **Your Whisparr API key stays on the server.** Cove holds it in a table this extension owns and
  never sends it to your browser. Once stored, the settings page tells you a key is set and nothing
  more; to change it, type a new one, and to remove it, use **Clear stored key**.
- **One endpoint answers an anonymous caller, and only one.** The import callback at
  `/api/extensions/com.alextomas955.whisparrsync/callback` has to answer Whisparr, which is another
  application rather than a Cove user, so it is not behind a Cove permission. It is authenticated
  instead by a secret Cove mints and keeps server-side, and a delivery that does not present that
  secret is refused. Every other endpoint this extension exposes requires a Cove permission.
- **The secret travels outside the address wherever it can.** A registration made from the settings
  page carries it in a request header on Whisparr v3 and as Basic authentication on Whisparr v2, so
  proxies and load balancers on the delivery path do not record it. An address you copy and paste by
  hand carries it in the address itself, because a pasted address has nowhere else to put it. While
  deliveries are still arriving that way the page says so, and the note clears itself once one
  arrives out of the address.
- **Only an action you start can cause a download.** Whisparr downloads because you asked Cove for
  something that needs it, never on a timer and never as a side effect of Cove looking at your
  library.

It also runs no scraper and no downloader code of its own; Whisparr does that work on its own
machine.

## Which Cove you need

Whisparr Sync needs **Cove 1.3.1** or newer. Cove refuses to install an extension that asks for a
newer host than the one you are running, and the extension listing hides a version your Cove is
below, so on an older Cove this extension is not offered to you in the first place.

## In this section

- [Monitor a studio or a performer](./monitoring.md) - the button, the two scopes and what each
  costs, and the same actions in bulk.
- [Settings reference](./settings.md) - every setting on the tab, its default and its valid values.
- [Changelog](./changelog.mdx) - user-facing changes, newest first.

## Install and build

For install, build, and local dev deploy instructions, see the extension's
[README on GitHub](https://github.com/alextomas955/cove-extensions/blob/main/extensions/WhisparrSync/README.md).
