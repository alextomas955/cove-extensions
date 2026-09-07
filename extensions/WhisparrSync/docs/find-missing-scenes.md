---
id: find-missing-scenes
title: Find the scenes you're missing
---

This guide walks you from opening a studio, performer, or tag page to marking the scenes you don't own
wanted and letting Whisparr acquire them — without risking a download loop.

Say you want everything a studio has released this year that isn't already in your library. Open the
studio, filter its Missing tab to the year, select the scenes you want, and Monitor them: Whisparr
starts watching for those scenes and grabs them as they become available. Nothing you already own is
touched.

## Before you start

- A Whisparr instance connected in the extension (see the [Connect guide](./guide)).
- A metadata link on the entity that matches your Whisparr version — a **StashDB** link on **v3
  (Eros)**, a **ThePornDB** link on **v2**.
- For the richest cards, and to discover scenes for an **unmonitored** entity, a matching metadata
  source configured in Cove under **Settings → Scraping → Metadata servers** (StashDB on v3, ThePornDB
  on v2). You never enter a key in the extension — Cove's own source is used.

## Open the Missing tab

1. Open a studio, performer, or tag page in Cove. (All three tabs work on Whisparr v3 and v2.)
2. Select the **Missing** tab in the detail rail.

The tab lists every scene the entity offers that you don't own yet, with a live count badge. Scenes
show one page at a time with paging controls below the grid, so even a large catalogue stays quick.

## Narrow the list

If the catalogue is large, narrow it before you act:

- Type in **Search titles** to filter by title.
- Choose a **Sort** — newest first, oldest first, or title A–Z.
- Use the **filter menus** to narrow by performer, studio, tag, or year. The menus adapt to the page —
  a studio page offers performer and tag filters, a performer page offers studio and tag, and a parent
  studio adds a **Sub-studio** filter across its child studios.

Your search, sort, and filter choices are kept in the page URL, so you can bookmark the view. Opening
that link later asks the metadata source for the same ordering and filters — not just the page you
happened to be looking at.

## Mark scenes wanted

Marking a scene wanted adds it to Whisparr's monitored list **without grabbing anything** — Whisparr
watches for it and acquires it when it can. You have three ways to do it:

- **One scene** — click **Monitor** on its card.
- **Several scenes** — select them with the checkbox on each cover, then click **Monitor** in the
  selection bar that appears.
- **The whole list** — click **Monitor all** in the controls header.

Bulk actions run as a background job on the server. The selection clears and the page reports no
per-scene result, so refresh the tab to see what changed. To stop watching for a scene, click
**Unmonitor** on its card or in the selection bar.

Marking wanted is v3 only. On Whisparr v2 the Monitor controls are disabled and read "Currently
available on Whisparr v3 (Eros)" — turn on [monitoring](./monitoring) for the whole studio instead, and
its scenes flow through Whisparr's normal acquisition.

## Grab a scene now

Grabbing a scene immediately needs Whisparr v3 (Eros). On v3, click **Search** on the scene's card, or
select several scenes and click **Search** in the selection bar. Search is the only action that starts a
download now.

If Whisparr holds no entry for the scene yet, a per-card Search grabs nothing and says so beneath the
button: "Whisparr has no entry for this scene yet, so there is nothing to search for — mark it wanted
first." Mark the scene wanted, and Whisparr grabs it when it can.

On Whisparr v2 both Search controls are disabled and read "Currently available on Whisparr v3 (Eros)".
Turn on [monitoring](./monitoring) for the whole studio instead and use **Search all monitored**, which
runs on both versions.

## Watch what arrives

Once scenes are wanted or searched, follow their progress on the
[Wanted, queue & history](./activity) page: **Wanted** shows what Whisparr is still looking for,
**Queue** shows what's downloading now, and **History** shows what has arrived. When Whisparr acquires
a wanted scene, it imports into Cove automatically and drops off the Wanted list — and off the Missing
tab on the next refresh.

## Keep a scene out of the list

To permanently drop a scene you're not interested in, **exclude it in Whisparr** from the scene's
exclusion controls. An excluded scene leaves the Missing list and won't reappear.
