---
id: monitoring
title: Monitor a studio or performer
---

This guide turns Whisparr monitoring on for a studio or performer straight from its Cove page, so
Whisparr watches for that entity's scenes and grabs them for you.

You need a connected Whisparr instance (see the [Connect guide](./guide)) and a metadata link on the
entity that matches your Whisparr version — a **StashDB** link on **v3 (Eros)**, or a **ThePornDB**
link on **v2**. That link is how the extension finds the same entity in Whisparr. Studio monitoring
works on both versions; performer monitoring is v3 only (see [On Whisparr v2](#on-whisparr-v2)).

## Turn monitoring on

1. Open a studio or performer page in Cove.
2. In the top-right action row (next to Favorite / Edit), click the **Whisparr** button. It opens the
   Whisparr menu.
3. Choose **Monitor in Whisparr**.

The item shows a checkmark and the Whisparr button turns accent-colored while the entity is monitored.
That one button is the whole Whisparr control for the entity — clicking it always opens this menu, and
monitoring lives inside it (there is no separate toggle elsewhere).

If the button is greyed out, hover it for the reason. The common ones are: the studio or performer has
no metadata link Whisparr can identify it by, Whisparr isn't connected yet, or the control isn't
available on your Whisparr version — for example a **performer on v2**, where the button reads
"Currently available on Whisparr v3 (Eros)".

## Read the status line

When an entity is monitored, a single quiet line appears under its stat tiles:

> Monitored in Whisparr · 1 of 147 scenes

![A studio page in Cove with the Whisparr menu open from the action-row Whisparr button, showing Monitor in Whisparr checked, the monitor-scope choices, Add all missing, Search all monitored, and a "Monitored in Whisparr · X of Y scenes" line at the foot of the menu.](/img/whisparr-sync/monitor-studio.png)

*A studio's Whisparr menu, monitored, with the scene-count status line — shown against a synthetic fixture library, no real media.*

The count is Whisparr's own, exactly as its studio/performer view shows it: **147** scenes in this
entity's full catalog, **1** already present in your Whisparr library. When Whisparr lists no catalog
for the entity yet, the line reads simply **Monitored in Whisparr** with no count. When the entity is
not monitored, no line shows — there is nothing to say.

## Turn monitoring off

Open the menu again and choose **Monitor in Whisparr** to clear its checkmark. The button returns to
its outline state and the status line disappears. Turning it off unmonitors the entity in Whisparr; it
does not delete anything Whisparr already grabbed.

## What monitoring does

Monitoring hands the entity to Whisparr's normal acquisition pipeline: Whisparr watches for its scenes
and grabs them using the root folder and quality profile you configured. Turning monitoring on never
triggers an immediate search — it only sets the entity to monitored.

When Whisparr grabs something, it imports into Cove through the **same auto-import** you already set up
(see [Connect guide → What happens on an import](./guide#what-happens-on-an-import)). There is no
second import path and no duplicate ingest: a monitored grab lands in Cove exactly like any other
Whisparr import, and you can review it in the **Import activity** section of the settings page.

## Choose new releases or all scenes

The Whisparr menu offers two monitor scopes, and the choice decides what happens to the studio's
existing back-catalogue — every scene Whisparr already knows about but you don't own yet:

- **New releases only** (the default) — you get **future** scenes as they appear, and the existing
  back-catalogue stays **visible but not wanted**, so Whisparr won't automatically grab it. You can
  still see what exists and grab any of it by hand.
- **All scenes** — the whole back-catalogue is marked **wanted**, so Whisparr will acquire it as your
  indexers allow.

Either way, scenes you already own are left alone, and switching monitoring on never kicks off a
search — you choose the scope, and Whisparr acquires on its own schedule. On Whisparr v2 the same
choice applies to the site's episodes.

## Do more from the Whisparr menu

Once an entity is monitored, the same Whisparr menu also offers **Add all missing** and **Search all
monitored** — the bulk actions covered in [Connect to Whisparr → Run bulk actions](./guide#run-bulk-actions-on-a-studio-or-performer).
Adding a scene never grabs it (Whisparr just registers it); only a search asks Whisparr to grab, so
nothing downloads until you explicitly search.

## On Whisparr v2

Monitoring works on Whisparr v2 too. Whisparr v2 is built on Sonarr, where a **site is a series**, so
a Cove **studio** monitors as its v2 site — the extension finds the site by the studio's **ThePornDB**
link, adds it (without grabbing anything), and turns monitoring on. **Search all monitored** runs the
episode search. So the studio Monitor button and its bulk search behave the same as on v3.

A few controls have no equivalent on v2 and are disabled there, reading **"Currently available on
Whisparr v3 (Eros)"** — you do not need to migrate; v2 and v3 are both fully supported:

- **Monitor a performer** — Whisparr v2 has no performer to monitor (performers are only tags on a
  scene).
- **A scene's Add / Monitor / Grab upgrades, and Add all missing** — v2 has no way to add one scene on
  its own; a scene comes in when you monitor its site and search for it.
- **The per-scene Whisparr status panel and exclusions** — these need a scene-level id that v2 doesn't
  expose.

Import, reconciliation, and the library-wide reconciliation view keep working on v2 as well.
