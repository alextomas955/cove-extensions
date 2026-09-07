---
id: status
title: Whisparr status reference
---

This page describes the Whisparr status the extension shows for your scenes: the states it can report,
how each is derived, and the three places it appears. Status is **opt-in, read-only, and off by
default** — turning it on changes nothing in Cove or Whisparr.

## The states

Scene status is a **Whisparr v3 (Eros)** surface. On a v2 connection none of the three places below
appears, and the pages that would show them say nothing rather than showing a count — see [Scene status on
Whisparr v2](#scene-status-on-whisparr-v2). Everything in this section describes a v3 connection.

Every scene resolves to exactly one of **four** Whisparr states. The state is derived from the movie set
the extension already fetches plus a read of Whisparr's exclusion list — **no new StashDB calls**.

The axis is Whisparr's **monitored** flag, so whether a scene has a file on disk is deliberately *not* a
state of its own. A monitored scene stays `monitored` once its file lands; file presence is reported
separately (see [the secondary in-library count](#the-secondary-in-library-count) below).

| State | Label | Meaning | How it is derived |
| ------- | ------- | --------- | ------------------- |
| `monitored` | Monitored | Whisparr is tracking the scene, whether or not it already has the file. | The scene's Whisparr movie is present and monitored. |
| `unmonitored` | Unmonitored | The scene is in Whisparr but not being watched for. | The movie is present and not monitored. |
| `notAdded` | Not added | Whisparr does not have the scene at all. | No Whisparr movie resolves for the scene's provider id. |
| `excluded` | Excluded | The scene is on Whisparr's exclusion list, so Whisparr will not add it. | The scene's provider id appears in Whisparr's exclusions (v3 only). |

Exclusion is checked first, so an excluded scene reads `excluded` rather than `notAdded`.

The library status row shows a count for each of the four states, in the fixed order **Monitored ·
Unmonitored · Not added · Excluded**, with each count marked by its own glyph so a state is never
signalled by colour alone. The four counts partition the library, so they sum to its scene total.

### The secondary in-library count

A fifth count sits beside the four, tinted apart from them: **In library** is the number of scenes whose
Whisparr movie holds a file. It is not a state, and it does not partition anything — it cross-cuts
`monitored` and `unmonitored`, so a scene counted here is also counted in exactly one of the four.

A scene with no provider id degrades gracefully: the state is reported as far as the data allows and never
blocks the view. A Whisparr **v2** connection is a different case — it is not a partly-answered state but no
state at all, so nothing is shown rather than something reported as far as the data allows. See [Scene
status on Whisparr v2](#scene-status-on-whisparr-v2).

## Where status appears

Whisparr status appears in three places: **on the library cards themselves**, in the library toolbar
summary, and on the scene detail Whisparr tab. The card badges and the library summary are off by
default behind a single toolbar pill, so your library stays clean until you turn them on.

### Per-card badges

In the videos, studios, and performers library views, turning on the **Whisparr** toolbar pill paints
a small status marker directly on each card:

- **Scene cards** show the scene's Whisparr state (Monitored · Unmonitored · Not added · Excluded) as a
  glyph in the card, with a second small marker when Whisparr holds the file.
- **Studio and performer cards** show a **"Monitored · present/catalog"** badge — the number of the
  entity's scenes already in your library over its full Whisparr catalog (for example `1/147`).

The pill reveals both the badges and the count row below in one toggle, and hides them again when you
turn it off. Studio badges work on both Whisparr v2 and v3; performer and scene badges are v3-only
(Whisparr v2 has no performer entity and no scene-level identity).

### Scene detail Whisparr tab

Open a scene in Cove and select the **Whisparr** tab in the detail rail. This is the native per-scene
surface: it shows the scene's status as a header badge plus the Whisparr-only quality and cutoff rows,
along with live **Add to Whisparr**, **Monitor this scene**, **Grab quality upgrades**, **Search for
this scene** and **Exclude from Whisparr** controls (see the [Connect
guide](./guide#add-search-or-monitor-a-scene)). **Interactive search** expands into the pickable list of
releases Whisparr's indexers offer, fetched only on the first expand, and each row grabs that release.
The panel shows only Whisparr-owned facts — it never restates Cove-owned metadata such as release date
or file size.

The tab is a **Whisparr v3 (Eros)** surface. On a v2 connection it declines before contacting Whisparr and
reports that the connected version doesn't offer it — v2 has no scene-level id, so a single scene cannot be
addressed there at all. This is the same split every other per-scene action already follows; the tab used to
run on v2 and answer from a whole-library read that could not resolve the scene anyway.

![A scene's detail rail in Cove with the Whisparr tab open, showing the status badge and the Add, Monitor, and Search controls.](/img/whisparr-sync/scene-panel.png)

*The scene-detail Whisparr tab, shown against a synthetic fixture library — no real media.*

### Library toolbar summary

In the videos library view toolbar, the **Whisparr** pill is off by default. Turning it on paints the
per-card badges (above) and reveals a compact library-level summary — one glyph-marked count per state
(Monitored · Unmonitored · Not added · Excluded), plus the secondary In library count — on its own row
just below the toolbar, the same way
Cove's selection bar appears when you select items. Toggling it off hides both again. It is a quiet,
removable view option, like Cove's other toolbar toggles.

Like the scene tab, this is a **Whisparr v3 (Eros)** surface: on a v2 connection the pill is not in the
videos toolbar and there is no count row (see [Scene status on Whisparr
v2](#scene-status-on-whisparr-v2)).

![The videos library toolbar in Cove with the Whisparr pill turned on, revealing the per-state glyph-marked count summary on its own row below the toolbar.](/img/whisparr-sync/library-status.png)

*The library status pill and its per-state count row, shown against a synthetic fixture library — no real media.*

The summary asks Whisparr for its whole scene set once each time you open it, and on Whisparr v3 (Eros)
it now reads that answer as it arrives instead of holding all of it: it keeps only the five facts it
needs per scene, and asks Whisparr to leave out the cached cover paths. On a large Whisparr the memory
that read costs is about a fifth of what it was, and the number of requests is unchanged — one scene
read plus one exclusion read, whatever the size of your library.

On Whisparr v3 that is a much lower ceiling rather than no ceiling. The summary still keeps one entry per
scene Whisparr tracks, so it still grows with your Whisparr — roughly a fifth as fast as before, not a
fixed amount. Two other costs are worth knowing about because they behave differently. Asking about a single studio or
performer costs one request plus one more per thousand of that entity's scenes, so it is bounded by the
entity rather than by your library. And marking a batch of scenes wanted re-derives its root folder and
tags for each scene, which is a small fixed number of extra requests per scene — it does not use more
memory, but a very large batch is a lot of requests.

When you multi-select scenes in the library, the selection bar's **Whisparr** action opens the batch
menu with its six ordered actions — Add to Whisparr, Monitor, Unmonitor, Search now, Search for
upgrades, and Exclude. Monitor and Unmonitor flip the selected scenes' monitored state without
downloading anything: a scene not yet in Whisparr is registered first, search-free, and Unmonitor
skips scenes that are not in Whisparr.

![The videos-list selection bar in Cove with the Whisparr batch menu open, showing the Add to Whisparr, Monitor, Unmonitor, Search now, Search for upgrades, and Exclude actions.](/img/whisparr-sync/videos-batch.png)

*The videos-list Whisparr batch menu, shown against a synthetic fixture library — no real media.*

## Scene status on Whisparr v2

**On a v2 connection, scene status is not shown at all.** The Whisparr pill, the per-card scene badges, the
count row and the scene detail Whisparr tab are all absent from the videos library, and the count request
declines before contacting Whisparr. Studio status is unaffected and works on both versions — see [Per-card
badges](#per-card-badges).

The reason is the same one that makes every other per-scene action v3-only: **a v2 scene has no scene-level
id to match your Cove scene against.** Whisparr v2 is Sonarr-shaped, so a site is a series and a scene is an
episode, and those episodes carry a ThePornDB id where Cove's scenes are matched on a StashDB one. No scene
resolves, so no scene has a state.

Earlier versions of this extension did show the count row on v2, and it was wrong in a way that read as
right: because nothing resolved, it reported **Monitored 0 · Unmonitored 0 · Not added (every scene)** for
every library — and "Not added" means *Whisparr does not have the scene*, which is exactly the conclusion
those counts could not support. A v2 user with a fully populated Whisparr saw a confident report that
Whisparr had none of it. Showing nothing is the honest answer, so that is what it now does.

If you want scene status on v2, the change it needs is for Cove and Whisparr to match v2 scenes on the
ThePornDB id they both carry. That is a real capability rather than a setting, and it is not implemented.

## Safety

**Reading a status never changes anything.** Every state above is derived from what Whisparr already
tracks plus, on Whisparr v3, one exclusion read; the derivation makes **no StashDB calls** and mutates
nothing in Cove or Whisparr, so turning the pill on is a view option and nothing more.

The controls beside the status are a separate matter, and they are the only part of these surfaces that
writes. The scene panel's controls and the selection bar's batch menu act on Whisparr when you press
them, and each one says what it does before you do. None of them touches a file: an add registers the
scene without grabbing, and only an explicit **Search**, **Search for upgrades** or a picked release
ever asks Whisparr to download.
