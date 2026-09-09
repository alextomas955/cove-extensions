---
id: missing
title: Browse what you do not own
sidebar_position: 3
---

Whisparr Sync adds a **Missing** tab to a studio page, a performer page and a tag page in Cove. It
lists the scenes your metadata source knows about for that entity and your library does not hold,
tells you whether each one is already in your Whisparr, and lets you mark them wanted.

Connect an instance first. See the [Settings reference](./settings.md).

## Where the tab is

In the row of tabs on a studio, performer or tag page, after Cove's own tabs. A number sits beside
the name. Cove asks for that number when the page loads, so it is there before you open the tab.

## Where the list comes from

From the metadata source Cove is already configured with, under Settings → Scraping → Metadata
servers. Whisparr Sync reads it with the key Cove holds for that source and asks you for no key of
its own. The list does not come from Whisparr.

Which source is read follows the Whisparr generation Cove uses: StashDB for Whisparr v3 (Eros) and
ThePornDB for Whisparr v2. Your browser also loads each cover image straight from that source.

If Cove names no metadata source, the tab says so and names the setting to fill in.

## What the number means

**The number is the size of the catalogue your metadata source lists for that entity. It is not the
number of scenes you are missing.** A studio with four thousand scenes reads four thousand whether
you own all of them or none of them. The count line above the grid says the same thing in words,
because the number on the tab has no room for it.

A number ending in a plus, such as `10,000+`, means the source will not serve a list past that
point. The real figure is larger, and the pages stop where the source stops.

The range at the left of the count line, such as `41-80 of 4,231`, is the source's own range for the
page you are on. Scenes you already own are taken out after the page arrives, so a page can show
thirty-one cards while its range still reads `41-80`. That is the range of the catalogue, not a
count of what is drawn.

A page holds at most forty scenes.

## What each card says

Each card carries the scene's cover, title, release date, studio, performers, description and its
tag and performer counts. A row the source gave no value for is left out rather than drawn empty. A
scene with no cover gets a placeholder tile carrying the title.

One pill on each card says what your Whisparr holds for that scene:

| Pill           | What it means                                                    |
| -------------- | ---------------------------------------------------------------- |
| Not added      | Whisparr has no entry for this scene.                            |
| Wanted         | Whisparr holds the scene and is monitoring it.                   |
| Unmonitored    | Whisparr holds the scene and is not monitoring it.               |
| Status unknown | No status was established, so nothing about Whisparr is claimed. |

A scene you excluded in Whisparr v3 (Eros) is not on the list at all, so there is no pill for it.

## What each card's two buttons do

**Monitor** registers the scene in Whisparr and marks it wanted. It downloads nothing. Whisparr
acquires what it wants on its own schedule.

**Search** asks Whisparr to look for that one scene and to download what it finds. **This is the
only action on the tab that downloads.** It acts on that scene alone and on nothing else the entity
monitors.

A button whose request is still on its way is dimmed and says so. A press that did not take puts the
card back as it was and states the reason beneath the buttons. Nothing else on the page changes.

Press **Search** on a scene Whisparr has no entry for and it says so rather than reporting a
failure: mark the scene wanted first, then search.

Both buttons need permission to configure extensions. Reading the tab needs Cove's **Videos** view
permission (`videos.read`).

## What Whisparr v2 can do here

Whisparr v2 keeps no per-scene records at all. On a v2 connection the tab still lists the
catalogue, and:

- Every pill reads **Status unknown**, and a line above the grid says why. No retry changes it.
- A scene you excluded in Whisparr is still listed, because v2 keeps no scene exclusions to read.
- **Monitor** and **Search** are still drawn on each card and neither can take. A press changes
  nothing and says the action is currently available on Whisparr v3 (Eros). Your instance is never
  asked, so it did not decline.
- A selection's **Monitor** and the toolbar's **Monitor all** are both refused before the run
  starts, and the page says the connected Whisparr keeps no per-scene records.

Monitoring a whole studio or performer is not affected. That lives on the Whisparr button beside the
entity's name. See [Monitor a studio or a performer](./monitoring.md).

## Act on several scenes at once

Tick the checkbox on a card to start a selection. A bar appears above the grid with the number
selected and three gestures:

| Gesture          | Keys  |
| ---------------- | ----- |
| Select all       | `s a` |
| Select none      | `s n` |
| Invert selection | `s i` |

Those are Cove's own list gestures, so a key sequence you rebound elsewhere in Cove works here too.
The keys work without clicking in the grid first.

**Monitor** in that bar marks every ticked scene wanted, in one background job. Nothing is
downloaded. The job's progress and its result appear in Cove's job area, and it ends with a count of
how many were registered, how many Whisparr already held and how many it refused. Nothing on the
page changes while it runs.

A started run clears the ticks. A run refused before it started keeps them and says why above the
grid, so you can fix the cause and press again.

**A selection covers the page you are looking at and nothing more.** Changing page clears the ticks.
There is no select-everything-that-matches gesture. Use **Monitor all** below for the whole list.

The scenes Cove already holds are a separate list, and registering those in Whisparr lives on the
Whisparr button beside the entity's name. See
[Add all missing](./monitoring.md#add-all-missing).

## Mark the whole list at once

**Monitor all**, at the right of the toolbar, marks every scene the list currently shows as wanted.
It downloads nothing, the same way each card's **Monitor** downloads nothing.

Press it and Cove asks you to confirm first. The confirmation names how many scenes the source lists
for that entity and says that the ones you already own are not part of the run. Cancel and nothing is
sent.

The run works from the search and the facet values in force, not from the page you are on, so it
covers every page of the narrowed list. Your browser sends no list of scenes: Cove works out the same
set on the server that it drew for you.

It runs in the background, one job, and reports the same three counts a selection's run reports. The
page does not change while it runs. It needs permission to configure extensions, as each card's own
**Monitor** does.

**Monitor all is not offered on a tag page.** A tag's list spans your whole library rather than one
entity, so there is no bounded run to make. Nothing you can change turns it on. Narrow with the
search and the facets and use a selection instead.

## Narrow the list

Above the grid:

- **Search titles** narrows the whole catalogue, not the page you are on. Typing settles before the
  list is re-read.
- **Sort** offers the orderings the source itself declares.
- One menu per facet the source filled. A menu lists the values the source served, and you can
  narrow by any of them. The source decides how many values it serves, so a menu can carry fewer
  than the source lists. Where it does, the menu says how many of how many it carries and that the
  rest cannot be picked there.
- **Year**, where the source filters by one. It lists every year between the oldest and the newest
  scene the source holds for that entity, so a year with nothing in it is not offered.
- **Refresh** reads the page again.

Every one of these travels in the page address, so the link you copy shows the reader what you were
looking at. Changing any of them takes you back to the first page.

## Why a control you expected is not there

**The tab leaves out a control the source cannot honour. It does not show one greyed out.** An
absence is the answer rather than a fault, and which controls are absent depends on which metadata
source your Whisparr generation reads from.

- **A sort one source does not declare.** Title A-Z is offered on StashDB and not on ThePornDB,
  which declares no title ordering.
- **A facet menu one source cannot scope to the entity.** On StashDB a studio page offers
  Performers, Sub-studios and Tags, and a performer page offers Tags. On ThePornDB a studio page and
  a performer page each offer Tags.
- **A tag page carries no facet menu on either source**, because the only menu left would narrow a
  tag to itself.
- **Monitor all on a tag page.** A studio's and a performer's list is bounded by that entity; a
  tag's spans the whole library, so there is no run of a knowable size to offer.
- **A year filter is offered on ThePornDB and not on StashDB.** ThePornDB narrows to an exact year.
  StashDB carries one date bound that cannot express a year, so no year menu is drawn there.

## When a studio page shows nothing

A studio page mirrors Cove's own **Include sub-studio content** toggle above the tab. Some metadata
sources list a parent studio's scenes under its sub-studios and none under the parent itself. With
the toggle off such a studio reads as empty while scenes exist one level down, and the tab says so
and names the toggle. Turn the toggle on to see them.

## What the tab says when it cannot answer

The tab always says which of these it is in. Only the first three clear on a **Refresh**; the rest
need something changed.

| What you see                                                                           | What to do                                                                   |
| -------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------- |
| The source could not be reached. This is not the same as owning everything.            | Press **Refresh**.                                                           |
| Cove could not reach Whisparr, so it read no status. The list below is still complete. | Press **Refresh**. The cards stay on screen.                                 |
| The last check failed, so these are the last values Cove read.                         | Press **Refresh**. The cards stay on screen.                                 |
| No Whisparr instance is connected.                                                     | Connect one on the Whisparr Sync settings tab.                               |
| No metadata source is set up.                                                          | Add one under Settings → Scraping → Metadata servers.                        |
| The source has no id for this entity.                                                  | Check the entity's link chips in Cove. An exact name match is tried as well. |
| The connected Whisparr keeps no per-scene records.                                     | Nothing. The list is complete and no status is available on that generation. |
| No titles match that search.                                                           | Clear the search.                                                            |
| No scenes match these filters.                                                         | Clear the filters.                                                           |
| You own every scene the source lists for this entity.                                  | Nothing.                                                                     |

A status Cove could not read never blanks the list. The cards stay and every pill reads **Status
unknown**, because a catalogue that was read is still a true answer.

## Hide a scene for good

Do it in Whisparr, on its own exclusion list. A scene on that list drops off this tab and stays off.
Whisparr Sync has no control that hides a scene, and nothing here writes to that list.
