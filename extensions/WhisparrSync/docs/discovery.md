---
id: discovery
title: Missing tab reference
---

The **Missing** tab lists the scenes an entity offers on its metadata catalogue that you don't own in
Cove yet, and lets you act on them. It appears on **studio**, **performer**, and **tag** detail pages,
in the detail rail beside the entity's other tabs. This page describes every part of the tab, panel by
panel; for a step-by-step walkthrough of finding and acquiring scenes, see
[Find the scenes you're missing](./find-missing-scenes).

To see anything here you need a connected Whisparr instance (see the [Connect guide](./guide)) and a
metadata link on the entity that matches your Whisparr version — a **StashDB** link on **v3 (Eros)**,
or a **ThePornDB** link on **v2**. That link is how the extension lines the entity's catalogue up
against what you own. Discovery presents its results as **Scenes** on both versions.

## Where the tab appears

| Entity | Whisparr v3 (Eros) | Whisparr v2 |
| --- | --- | --- |
| Studio | Yes (StashDB) | Yes (ThePornDB) |
| Performer | Yes (StashDB) | Yes (ThePornDB) |
| Tag | Yes (StashDB) | Yes (ThePornDB) |

All three tabs are version-neutral: on v3 they read from StashDB, on v2 from ThePornDB (a v2 studio is
a site in v2's model).

Discovery is a **metadata-source** feature, so it is available on more entities than *monitoring* is:
performer monitoring stays v3-only because Whisparr v2 has no performer entity to monitor, but
ThePornDB lists a performer's scenes and filters scenes by tag — so the performer and tag **Missing**
tabs work on both versions.

A tag's catalogue can be enormous. The count is the source's own — 766 for a performer, 392 for a
studio — but ThePornDB stops counting at 10,000, so a broader set reads **"10,000+"** rather than
pretending to an exact figure. Paging walks the whole reported range either way.

### Tags you never linked

Most Cove tags are your own, so they carry no metadata-source id — and a source needs one to list a
tag's scenes. When a tag has no stored id, the extension looks the tag up **by name** on the connected
source (StashDB on v3, ThePornDB on v2) and uses the match, so an ordinary Cove tag works with nothing
for you to set up. A stored id always wins when there is one.

The lookup has to be confident: only an exact name match counts (StashDB also accepts its aliases). If
the source knows the idea under a different label — it has "Blonde Hair (Female)" where you wrote
"Blonde" — the tab says it has no id for that tag rather than showing a different tag's scenes.

## What "missing" means

A scene is missing when the metadata source lists it for this entity but Cove doesn't own it. The set
is the entity's full catalogue **minus what you already own minus what you've excluded in Whisparr**:

```text
missing = catalogue − owned − excluded
```

Ownership is matched on the metadata id each scene carries — the **StashDB** id on v3, the
**ThePornDB** id on v2 — never on whether Whisparr has a file. A scene you own is recognised as owned
even when Whisparr tracks it without a file (for example when its file lives outside a Whisparr root),
so an owned scene is never shown as missing. A scene you've excluded in Whisparr drops out of the set
entirely.

### If your Whisparr build doesn't offer the per-entity lookup

The tab asks your Whisparr instance which scenes it already tracks for this entity, using two routes the
instance itself advertises. If your build doesn't advertise them, the tab reports that this Whisparr build
doesn't offer what it needs, rather than falling back to reading your whole Whisparr library. Every build
this extension supports advertises them; a build that doesn't is either much older or a custom fork.

## The count badge

The tab carries a live **count badge** — the number of scenes this entity is missing right now, drawn
by Cove from the extension. It updates when you refresh.

## The scene cards

Missing scenes render as a card grid that mirrors Cove's native videos card — a wrapping grid of the
current page's cards that flows down the page (see [Paging through the list](#paging-through-the-list)).

Each card shows, from top to bottom:

- A 16:9 **cover** — one image from the metadata source (StashDB's first scene image, ThePornDB's
  poster), the same field Cove itself uses for a scene cover. A placeholder tile shows when the scene
  carries no image.
- The scene **title** (up to two lines).
- A **date · studio** meta line (each part shown only when the scene reports it).
- **Performer chips** — an avatar and name per performer, up to four, then a `+N` overflow. The avatar
  comes from the metadata source, or from Cove's own performer avatar when the source carries none.
- A two-line **description**.
- A **performer / tag count** footer.
- An always-on **Whisparr status** pill (see below).
- An **action row** (see [Card actions](#card-actions)).

Rows that carry no performers, tags, or description omit those parts rather than showing an empty
strip.

### The status pill

Every card shows one of four Whisparr states, always on (unlike the owned videos page, which hides
status behind a toggle):

| State | Meaning |
| --- | --- |
| **Not added** | The scene isn't in Whisparr yet. |
| **Wanted** | The scene is monitored in Whisparr without a file — Whisparr is watching for it. |
| **Unmonitored** | The scene is in Whisparr but not monitored, so Whisparr won't grab it. |
| **Status unknown** | Cove can't tell which of the three above applies, so it doesn't claim one. |

The library tracks four scene states — not added, excluded, unmonitored and monitored. **Excluded**
is the one that never appears here: an excluded scene has left the set. The other three are all
assertable on this tab, with a monitored scene Whisparr holds no file for shown as **Wanted**; a scene
Cove can't place among them reads **Status unknown** rather than **Not added**. The pill reuses the same glyph as the videos-page badge, so a Wanted scene reads with the
monitored glyph; **Status unknown** has its own glyph, and the label is what distinguishes it at a
glance.

**Status unknown** has two causes, and each is named once above the grid rather than on every card:

- **On Whisparr v2 it is permanent.** v2 has no scene-level record, so it can't report a status for an
  individual scene and every row on that generation reads this way. One message above the grid says so
  for the whole set: "Monitor, Unmonitor and Search act on a scene-level Whisparr row, which this
  connection has none of — Currently available on Whisparr v3 (Eros). The connected Whisparr can't
  report a per-scene status here, so Cove doesn't guess." There is nothing to retry, so the message
  offers no Refresh. Each card keeps its glyph and its **Status unknown** label.
- **On Whisparr v3 the read didn't answer.** Whisparr was unreachable when the tab checked, so no row's
  status could be established. One message above the grid explains it for the whole set and offers a
  Refresh — see [Empty and unavailable states](#empty-and-unavailable-states).

On a connection where the per-scene verbs are unavailable, the tab does not advise a settings change:
changing one would not enable them there. The setting is still described on the Whisparr Sync settings
page, at the control that sets it.

### Card actions

Each card pairs one immediate-grab action with one monitoring action:

- **Search** (magnifier) — grabs the scene from Whisparr **now**. This is the only per-card action that
  starts a download. If Whisparr holds no entry for the scene, nothing is grabbed and the card states
  that beneath the button: "Whisparr has no entry for this scene yet, so there is nothing to search for —
  mark it wanted first."
- **Monitor** — marks the scene wanted (adds it to Whisparr monitored, with no immediate grab). Shown
  when the scene isn't already wanted.
- **Unmonitor** — removes the scene from the wanted list (no grab). Shown when the scene is already
  wanted.

If Whisparr Sync can't complete one of these, the card says why in a line beneath the action row and the
card goes back to how it was. Where the cause is one of your settings the line names it — for example
"Set the Whisparr URL in Whisparr Sync settings (Connection) before acting — without it, every Whisparr
action fails as though Whisparr were not running." Where Whisparr can't be reached it says so instead. The line stays until you click again or press Refresh, and it is separate from Search's
nothing-to-search-for note above: a request that failed and an honest "there is nothing to grab" are
different answers.

All three are per-scene, v3 only — Whisparr v2 carries no scene-level record, so it has no independent
per-scene add and nothing for a per-scene search to command. On v2 all three are disabled and read
"Currently available on Whisparr v3 (Eros)". The whole-entity **Search all monitored** in the Whisparr
menu is a different verb and stays available on both versions (see [Monitoring](./monitoring)).

## The controls header

Above the grid, a non-scrolling header holds:

- **Search titles** — filter the list to titles that match what you type.
- **Sort** — **Newest first** (the default), **Oldest first**, or **Title A–Z**. On Whisparr v3 the
  ordering is applied by StashDB across the entity's whole catalogue, so **Oldest first** on a tag with
  160,000 scenes leads with releases from 1900 and 1970, not with the oldest of the forty on screen. On
  Whisparr v2 the control reorders the scenes currently loaded and says so beneath itself: ThePornDB
  offers no ordering at all, so there is nothing for Cove to ask it for. That is a permanent property
  of the source rather than a Cove limitation or a gap waiting to be closed — the same control orders
  the whole catalogue on v3. Scenes with no release date sort last under the date modes.
- **Refresh** — re-check the entity against Whisparr and the metadata source. Scenes you've since
  acquired drop off; newly available scenes appear.
- **Monitor all** — mark every missing scene wanted in one background job (no immediate grab). Like the
  per-card Monitor, it is v3 only and disabled on v2 with the version note above. If the run is refused
  before it starts, the reason appears in the selection bar's row beneath the header rather than in the
  job drawer, because there is no job yet to report it. **Not offered on a tag
  page:** a tag can match tens of thousands of scenes, so there is no one-click "mark them all wanted".
  Select the scenes you want and use the selection bar's Monitor instead — that stays available on every
  entity.
- A count line reading **"X–Y of N"** — the same format as Cove's list pages. `N` is the catalogue
  total under the filters you've set, not an exact count of what you're missing, and `X–Y` is the
  page's position in that total rather than the number of cards on screen. Both figures are covered
  under "Two things the tab deliberately doesn't do" below.

Your search, sort, and filter choices round-trip through the page URL, so a link carries the view you
were looking at. Opening such a link narrows or reorders the whole catalogue directly — it asks the
metadata source for what the link names, rather than applying it to whichever page loaded first.

Two limits still apply, and the controls say so where they bite. A source with no ordering to offer
(ThePornDB) reorders only the scenes loaded on the page, and the sort control carries that sentence
beneath it. A filter value the source's own menu can't resolve falls back to narrowing the loaded
scenes, which is what a hand-edited link or a link from a different entity will usually do.

## The filters

Below the header, a row of facet menus narrows the list. The facets are **context-aware** — they lead
with the axes that discriminate on the current page, and the entity's own axis is never offered
(filtering a studio page by that studio would select everything):

| Page | Facets, in order |
| --- | --- |
| Performer | Studio · Tag · Year |
| Studio | Performer · Tag · Year |
| Tag | Performer · Studio · Year |
| Parent studio | **Sub-studio** · Performer · Tag · Year |

Each facet offers an "All …" option that clears it. A facet with no values on the loaded scenes is
dropped — if none of the loaded scenes carry a tag, for instance, the Tag facet doesn't appear.

A **parent studio** (one with child sub-studios) is the single case where a studio page offers a studio
facet: its Missing tab aggregates the catalogues of the parent and every child sub-studio, so a
**Sub-studio** facet leads the row to let you narrow to one label. This aggregation is StashDB-only;
v2 has no sub-studio model, so a v2 studio stays single-site.

### Choosing a value filters the whole catalogue

Picking a facet value asks the metadata source for a fresh first page narrowed on that value, so the
count line moves with it. On a StashDB tag holding 160,157 scenes, choosing one performer takes the count
to **41**; choosing a studio takes it to **1,459**. On a ThePornDB site holding 392, choosing a performer
takes it to **3**.

Both sources narrow on all four axes. They differ on **year**:

- **Whisparr v2 filters by an exact year.** Asking a 392-scene site for 2025 gives you 31 scenes, all
  from 2025.
- **Whisparr v3 approximates it.** StashDB accepts one date bound rather than a year, so the extension
  asks for everything on one side of the year and then keeps the rows that match. Every scene you see is
  from the year you chose, but the count beside the list is the bound's total and reads high — and
  because the trimming happens after a page is fetched, an early page can come back empty while later
  pages carry the year's scenes. Page forward when that happens.

### Where a facet's options come from

A facet's menu is filled one of two ways, and the control tells you which.

Three menus offer **every value in the whole catalogue**, read once when you open the entity:

| Page | Menu | Example |
| --- | --- | --- |
| Studio (v3) | Performer | 385 names for a studio whose loaded page carries 52 |
| Performer (v3) | Studio | 225 studios for a performer whose loaded page carries 35 |
| Parent studio (v3) | Sub-studio | Cove's own child studios, complete by definition |

Every other menu offers the values the loaded scenes carry. The toolbar says so once, naming the menus
it covers — on a v3 studio page, where the Performer menu is complete and the Tag and Year menus are
not:

> StashDB offers no list of every value for tag and year here; these are the ones seen so far.
> Choosing one still filters the whole catalogue on the axes it supports.

The name in that line is the metadata source the page read — StashDB or ThePornDB. The line appears
once, on its own row in the toolbar, and lists every menu it applies to: a tag page's Performer and
Studio menus on v3, every Year menu, and every menu on v2, because neither source publishes those
roll-ups. **The control stays usable**: a value you pick from a partial menu still narrows the whole
catalogue, exactly as one picked from a complete menu does. What the line tells you is that a value you
don't see in the menu may still exist in the catalogue.

## Selecting and acting in bulk

Hover a card to reveal its selection checkbox (top-left of the cover), the same as the scenes page;
selected cards keep it solid. A **selection bar** appears with the count and these actions — the same
interaction model as the owned-videos bulk view:

- **Select all** / **Invert** / **Deselect all** — select every visible scene, flip the selection, or
  clear it.
- **Monitor** — mark every selected scene wanted (v3 only).
- **Unmonitor** — un-mark every selected scene (v3 only).
- **Search** — grab every selected scene now (v3 only).

As on the cards, all three are disabled on v2 and read "Currently available on Whisparr v3 (Eros)".

A bulk action Whisparr Sync can't start states why on its own row in the selection bar, naming that verb —
"Couldn't mark the selected scenes wanted — …" — and your selection is kept, so you can fix the setting it
names and press the button again.

Bulk actions run as background jobs on the server — no pop-up dialogs, and no per-scene result on the
page: once a job starts the selection clears and the run reports nothing back, so refresh the tab to see
what changed. The
server re-derives the missing set and acts only on the scenes still in it, so a bulk action never touches
a scene you've since acquired.

## Paging through the list

The grid shows one page of scenes at a time with **paging controls** below it — page numbers, previous/
next, and a "Go to page" jump — the same pagination as Cove's other list pages. The count line reads
**"X–Y of N"**. This is what keeps a broad tag with tens of thousands of scenes usable: you page through
it rather than loading everything at once.

### What each page costs

Sorting and filtering are asked of the metadata source, so it is worth knowing what that costs. Reading
a page of any entity is **one request** to the source, whatever the sort and whichever filters are set.

Opening a studio or a performer costs **one extra request** — the whole-catalogue menu described above —
and that answer is reused for every later page of the same entity, so paging through a studio costs one
request a page. Opening a tag costs **one request** in total, because no whole-catalogue menu exists for
one. Opening a parent studio also costs one: its Sub-studio menu comes from Cove's own records, not from
the source. Changing the sort or a facet costs **one** fresh page read.

Opening a bookmarked link that carries a **filter** costs **one extra read**, once, as the link opens:
the menu that turns a filter name into something the source can be asked for arrives with the first
answer, so the filter can only be applied on the read after it. A link carrying only a sort or a year
costs no extra read — those need no menu.

Nothing here walks the catalogue in the background. A 160,157-scene tag costs the same one request a page
as a 392-scene site.

### Two things the tab deliberately doesn't do

**The count is the catalogue's size, not the number of scenes you're missing.** "1–40 of 160,157" means
the source lists 160,157 scenes for this entity under the filters you've set. Some of those you already
own. Counting the difference exactly would mean fetching the entire catalogue and comparing every scene
against your library — for one tag, four thousand requests to answer a number in a badge — so the tab
reports the figure it can get honestly instead.

**A page holds up to forty scenes, and often fewer.** Scenes you already own are removed after the page
arrives, so a studio page whose catalogue you own three of shows 37 cards rather than 40. The count
line's range half is the page's position in the catalogue, not a tally of the cards, so that page still
reads **"1–40"** above its 37 cards. Topping each page back up to forty was considered and rejected:
the cost is unbounded for exactly the person it would
help — someone who owns most of a set, whose next forty unowned scenes may be hundreds of rows away — and
it needs a stable ordering across requests, which one of the two sources cannot give. A short page is not
the end of the list; keep paging.

## How the catalogue is sourced

The catalogue always comes from **Cove's own configured metadata source** — StashDB on v3, ThePornDB on
v2, under **Settings → Scraping → Metadata servers**. You never enter a key in the extension, and the
source is the same whether or not the entity is monitored in Whisparr.

| Situation | Source | Card detail |
| --- | --- | --- |
| Cove has a matching metadata source | Direct from StashDB (v3) / ThePornDB (v2) | Rich — cover, performer avatars, tags, description |
| Cove has no matching metadata source | — | The tab shows how to set one up (see below) |

Because there is a single source, the cards are always rich, and this holds on both versions equally:
with a ThePornDB server configured, a **v2** studio's Missing-tab cards carry the same cover, performer
avatars, tags, and description as a **v3** studio's — each version reaches the same rich card through
its own metadata source. When Cove has no matching source configured, the tab shows an actionable
message instead of a misleading empty list:

> Set up a StashDB (v3) metadata source in Cove (Settings → Scraping → Metadata servers) to discover
> `{entity}`'s catalogue.

Add the metadata server in Cove and reopen the tab. Whisparr is read only for each scene's status (see
[The status pill](#the-status-pill)), never for the catalogue itself. Every discovery read is
read-only: sourcing the catalogue never adds, monitors, or grabs anything.

## Empty and unavailable states

The tab never renders a failure as an empty catalogue. Each outcome is distinct:

| State | What you see |
| --- | --- |
| Nothing missing | A green check and "You own every scene StashDB (v3) / ThePornDB (v2) lists for `{entity}` ✓". |
| Metadata source unreachable | "Couldn't reach StashDB to check what's missing for `{entity}`. This isn't the same as owning everything — try again shortly." |
| Whisparr unreachable | The catalogue still lists from the metadata source; only the per-scene status is affected. Every scene reads **Status unknown** instead of claiming it isn't in Whisparr, and one message above the grid — "Whisparr isn't reachable. Check the connection in Settings." — explains it for the whole set. It offers a **Refresh**, because a retry can clear it. No card carries a message of its own. |
| Whisparr v2 connected | The catalogue lists normally. Every scene reads **Status unknown** and the per-scene Monitor, Unmonitor and Search are dimmed, because v2 has no scene-level record. One message above the grid states that cause for the whole set. It offers **no Refresh**, because nothing can clear it; each card keeps its status glyph and label, and each dimmed control keeps its reason on hover. |
| No metadata source in Cove | The set-up-a-source message above. |
| No metadata id on the entity | "No StashDB id for `{entity}`, so there is no catalogue to check." |
| Search matches nothing | "No titles match …" with a Clear search button. |

If a refresh fails while a list is already on screen, the existing list stays put under an outage
banner ("Couldn't reach Whisparr — showing the last known list.") rather than blanking.

## Excluding a scene

To keep a scene out of the Missing list for good, **exclude it in Whisparr** (from the scene's exclusion
controls). An excluded scene leaves the missing set entirely and won't come back. There is no local
per-scene hide — Whisparr's own exclusion is the mechanism, so the hide and the acquisition pipeline
stay in agreement.

## Loop safety

Discovery only ever acts on scenes you don't already own. Marking a scene wanted (per-card Monitor,
bulk Monitor, or Monitor all) arms Whisparr's acquisition without grabbing anything — only an explicit
**Search** grabs now. Anything Whisparr then acquires imports back through the same webhook and
reconcile path as everything else, so acting from the Missing tab can't start a download loop. Watch
what Whisparr has queued or grabbed on the [Wanted, queue & history](./activity) page.
