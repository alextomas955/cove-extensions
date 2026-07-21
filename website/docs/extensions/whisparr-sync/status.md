---
id: status
title: Whisparr status reference
---

This page describes the Whisparr status the extension shows for your scenes: the states it can report,
how each is derived, and the three places it appears. Status is **opt-in, read-only, and off by
default** — turning it on changes nothing in Cove or Whisparr.

## The states

Every scene resolves to exactly one Whisparr state. The state is derived from the reconciliation movie
set the extension already builds plus a read of Whisparr's exclusion list — **no new StashDB calls**.

| State | Label | Meaning | How it is derived |
| ------- | ------- | --------- | ------------------- |
| `downloaded` | Downloaded | Whisparr has the scene as a movie with a file on disk. | The scene's Whisparr movie has `hasFile: true`. |
| `monitored` | Monitored | Whisparr is tracking the scene and watching for it, but has no file yet. | The movie is present and monitored, `hasFile: false`. |
| `unmonitored` | Unmonitored | The scene is in Whisparr but not being watched for. | The movie is present, not monitored, `hasFile: false`. |
| `notAdded` | Not added | Whisparr does not have the scene at all. | No Whisparr movie resolves for the scene's StashDB id. |
| `excluded` | Excluded | The scene is on Whisparr's exclusion list, so Whisparr will not add it. | The scene's StashDB id appears in Whisparr's exclusions (v3 only). |

The library status row shows a count for each of the four primary states — **Downloaded · Monitored ·
Not added · Excluded** — with each count marked by its own glyph. `Unmonitored` is reported per scene
(in the scene panel and the reconciliation column) but is not counted in the compact library row.

A scene with no StashDB id, or a Whisparr v2 instance (which defers exclusions), degrades gracefully:
the state is reported as far as the data allows and never blocks the view.

## Where status appears

Whisparr status appears in four places: **on the library cards themselves**, on the scene detail
Whisparr tab, and in the reconciliation Whisparr column. The card badges and the library summary are
off by default behind a single toolbar pill, so your library stays clean until you turn them on.

### Per-card badges

In the videos, studios, and performers library views, turning on the **Whisparr** toolbar pill paints
a small status marker directly on each card:

- **Scene cards** show the scene's Whisparr state (Downloaded · Monitored · Not added · Excluded) as a
  glyph in the card.
- **Studio and performer cards** show a **"Monitored · present/catalog"** badge — the number of the
  entity's scenes already in your library over its full Whisparr catalog (for example `1/147`).

The pill reveals both the badges and the count row below in one toggle, and hides them again when you
turn it off. Studio badges work on both Whisparr v2 and v3; performer and scene badges are v3-only
(Whisparr v2 has no performer entity and no scene-level identity).

### Scene detail Whisparr tab

Open a scene in Cove and select the **Whisparr** tab in the detail rail. This is the native per-scene
surface: it shows the scene's status as a header badge plus Whisparr-only facts (quality, cutoff, and
an expandable "releases available at indexers" count), along with live **Add to Whisparr**, **Monitor
this scene**, and **Search for this scene** controls (see the [Connect
guide](./guide#add-search-or-monitor-a-scene)). The panel shows only Whisparr-owned facts — it never
restates Cove-owned metadata such as release date or file size.

![A scene's detail rail in Cove with the Whisparr tab open, showing the status badge and the Add, Monitor, and Search controls.](/img/whisparr-sync/scene-panel.png)

*The scene-detail Whisparr tab, shown against a synthetic fixture library — no real media.*

### Library toolbar summary

In the videos library view toolbar, the **Whisparr** pill is off by default. Turning it on paints the
per-card badges (above) and reveals a compact library-level summary — one glyph-marked count per state
(Downloaded · Monitored · Not added · Excluded) — on its own row just below the toolbar, the same way
Cove's selection bar appears when you select items. Toggling it off hides both again. It is a quiet,
removable view option, like Cove's other toolbar toggles.

![The videos library toolbar in Cove with the Whisparr pill turned on, revealing the per-state glyph-marked count summary on its own row below the toolbar.](/img/whisparr-sync/library-status.png)

*The library status pill and its per-state count row, shown against a synthetic fixture library — no real media.*

When you multi-select scenes in the library, the selection bar's **Whisparr** action opens the batch
menu with its four ordered actions:

![The videos-list selection bar in Cove with the Whisparr batch menu open, showing the Add, Search now, Search for upgrades, and Exclude actions.](/img/whisparr-sync/videos-batch.png)

*The videos-list Whisparr batch menu, shown against a synthetic fixture library — no real media.*

### Reconciliation Whisparr column

The reconciliation table (Settings → Extensions → Whisparr Sync) carries a **Whisparr** column: each
row shows its scene's state as a glyph-and-label badge, with the Excluded state distinctly tinted. This
is the full per-scene status list — the reconciliation view is where the extension already lines every
Whisparr scene up against your Cove library. Sort by the column to group scenes by state. The column is
display and sort only; it makes no extra network call — the state arrives on each reconciliation row.

## Safety

All three surfaces are read-only in this release. Status reads reuse the reconciliation movie set and a
single exclusion read; they make **no StashDB calls** and mutate nothing in Cove or Whisparr.
