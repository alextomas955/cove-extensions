---
id: missing
title: Browse what you do not own
sidebar_position: 3
---

Whisparr Sync adds a **Missing** tab to a studio page in Cove, and to a performer page where the
connected instance is Whisparr v3 (Eros). It lists the scenes your Whisparr knows about for that
entity and your library does not hold, tells you whether Whisparr is monitoring each one, and lets
you mark them wanted.

A tag page carries no Missing tab. A tag names no entity Whisparr holds a catalogue under, so there
is nothing to list it against.

Connect an instance first. See the [Settings reference](./settings.md).

## Where the tab is

In the row of tabs on a studio or performer page, after Cove's own tabs. A number sits beside
the name. Cove asks for that number when the page loads, so it is there before you open the tab.

## Where the list comes from

**From your Whisparr.** The tab asks the connected instance which scenes it lists for that entity,
and takes out the ones your library already holds. Your metadata source is not asked for a scene
list at all.

How the instance answers depends on the generation:

- **Whisparr v3 (Eros)** lists a studio's or a performer's own works. It holds those only for an
  entity it has been told about, which the tab offers to do; see below.
- **Whisparr v2** lists a site's scenes. It has no performer entity, so a performer page has nothing
  to ask about there.

Cove's metadata source is still what says which scene in Whisparr is which video in your library, so
one has to be configured under Settings → Scraping → Metadata servers. If Cove names none, the tab
says so and names the setting to fill in.

## An entity Whisparr does not have yet

Whisparr lists scenes only for an entity it holds, so a studio or performer it has never been told
about has no list to compare against. The tab says so and offers **Add to Whisparr**.

That add asks Whisparr to track the entity's catalogue and **wants none of it**: every scene it
pulls in arrives unmonitored and nothing is searched or downloaded. You then monitor what you choose,
one scene at a time or as a selection.

If Cove holds no StashDB id for the entity on v3, or no ThePornDB id on v2, the tab says that
instead: Whisparr cannot be told which entity it is, so there is nothing to add.

## What the number means

**The number is how many scenes you are missing**: what Whisparr lists for the entity, minus what
your library already holds. A studio whose scenes you all own reads zero.

The range at the left of the bar above the grid, such as `41-80 of 1,205`, is your position in that
same set, so the cards on screen and the figure beside them always agree.

A page holds at most forty scenes.

## What each card says

Each card carries the scene's cover, title, release date, studio, performers, description and its
tag and performer counts. A row the source gave no value for is left out rather than drawn empty. A
scene with no cover gets a placeholder tile carrying the title.

One pill on each card says what your Whisparr holds for that scene:

| Pill        | What it means                                      |
| ----------- | -------------------------------------------------- |
| Wanted      | Whisparr holds the scene and is monitoring it.     |
| Unmonitored | Whisparr holds the scene and is not monitoring it. |

Every scene on this tab is one Whisparr already has an entry for, since the list is Whisparr's own,
so no card reads "not added" and none reads an unknown status.

A scene you excluded in Whisparr v3 (Eros) is not on the list at all, so there is no pill for it.

## Open a scene at its source

The cover and the title are links. Follow either and the scene opens at your metadata source, in a
new tab. Nothing else on the card is a link, so the checkbox and the two buttons act on the card
where they are.

This works on both sources. Each one shows its scenes only to a reader signed in to it, so follow a
link without an account there and you land on that site's sign-in page instead of the scene.

A cover is drawn where Whisparr holds one for the scene. Whisparr v2 often holds none for an
episode, and those cards carry the placeholder tile.

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

Whisparr v2 keeps a row per scene under a site, so the tab lists a site's scenes and each card reads
the state Whisparr holds for it. What v2 cannot do here:

- **A performer page has nothing to ask about.** v2 has no performer entity, so only studio pages
  carry a usable list.
- **A card's Monitor and Search are refused.** Both are built on the v3 route that adds a scene to
  the catalogue, and v2 needs neither: the scene is already in its catalogue. Pressing either says
  the action is available on Whisparr v3 (Eros), and your instance is never asked.
- **A selection's Monitor and the toolbar's Monitor all** are refused for the same reason.
- v2 keeps no scene exclusions, so a scene you excluded elsewhere is still listed.

Monitoring a whole studio is not affected. That lives on the Whisparr button beside the entity's
name. See [Monitor a studio or a performer](./monitoring.md).

## Act on several scenes at once

Tick the checkbox on a card to start a selection. A bar appears above the grid with the number
selected and three gestures:

| Gesture      | Keys  |
| ------------ | ----- |
| Select all   | `s a` |
| Invert       | `s i` |
| Deselect all | `s n` |

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

## Narrow the list

One bar above the grid carries the tab's name, the range you are looking at and every control:

- **Search titles** narrows the whole catalogue, not the page you are on. Typing settles before the
  list is re-read.
- The ordering control offers the orderings the source itself declares. It reads the ordering in
  force, such as **Newest first**, from the moment the page loads: with none picked the source
  applies its own, and the control names that one.
- One dropdown per facet the source filled, listing the values the source served. It reads what it
  covers while nothing is picked, such as **All tags**, and reads the value once you pick one.
  Choose the first entry again to clear it. The source decides how many values it serves, so a
  dropdown can carry fewer values than the source lists.
- A value in force the served list does not carry stays in the dropdown, so you can always clear it.
- **Year**, where the source filters by one. It lists every year between the oldest and the newest
  scene the source holds for that entity, so a year with nothing in it is not offered. Its years are
  worked out from those two dates rather than listed by the source.
- **Refresh** reads the page again.

Every one of these travels in the page address, so the link you copy shows the reader what you were
looking at. Changing any of them takes you back to the first page.

## Why a control you expected is not there

**The tab leaves out a control the source cannot honour. It does not show one greyed out.** An
absence is the answer rather than a fault, and which controls are absent depends on which metadata
source your Whisparr generation reads from.

- **A sort one source does not declare.** Title A-Z is offered on StashDB and not on ThePornDB,
  which declares no title ordering.
- **A facet one source cannot scope to the entity.** On StashDB a studio page offers
  Performers, Sub-studios and Tags, and a performer page offers Tags. On ThePornDB a studio page and
  a performer page each offer Tags.
- **A year filter is offered on ThePornDB and not on StashDB.** ThePornDB narrows to an exact year.
  StashDB carries one date bound that cannot express a year, so no year control is drawn there.

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
