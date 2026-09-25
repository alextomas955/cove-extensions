---
id: library-status
title: Show Whisparr status on library cards
sidebar_position: 4
---

Whisparr Sync adds a button to the toolbar of Cove's videos, studios and performers lists. Press it
and every card on the page gains a small badge saying what your Whisparr holds for that scene,
studio or performer.

Connect an instance first. See the [Settings reference](./settings.md).

## Where the button is

In the toolbar above the list, in the group with Cove's own display-mode buttons. It carries
Whisparr's mark rather than a word, so its name is on hover: **Show Whisparr status**, and **Hide
Whisparr status** once the badges are on.

**The badges start off, every time.** The button is off after each page load, and nothing about it is
stored in your browser. One press covers every list page you visit until you reload.

## What each badge says

One badge per card, drawn in the small box Cove keeps below the card body. It never covers the
artwork.

| Badge         | What it means                                                     |
| ------------- | ----------------------------------------------------------------- |
| ● Monitored   | Whisparr holds it and is monitoring it.                           |
| ○ Unmonitored | Whisparr holds it and is not monitoring it.                       |
| – Not added   | Whisparr has no entry for it.                                     |
| ⊘ Excluded    | The scene is on your Whisparr's exclusion list. Scene cards only. |

These are the same words the Whisparr button and the Missing tab use. **Not added** and
**Unmonitored** are different answers: the first means your Whisparr has never heard of the entity,
the second means it holds it and is leaving it alone.

A studio or performer card never reads **Excluded**. Whisparr Sync reads your Whisparr's exclusion
list for scenes only, so a badge on either of those cards would be claiming something your instance
was never asked.

**A card shows the state alone. There is no count beside it**, such as how many of a studio's scenes
you hold. See [What this does not show](#what-this-does-not-show).

## The badges appear in the Grid display mode only

Cove mounts the in-card box this badge draws in only in its **Grid** display mode. In any other mode
the page offers, such as List, Wall or Tagger, there is nowhere on a card to put it.

The button is in the toolbar of every display mode, so you can press it where no badge can appear.
Where that happens it says so on hover: switch to Grid and the badges are there, with no second
press.

## When a card shows nothing at all

A card with no badge looks exactly as it does with the button off.

**Cove holds no link to the scene, studio or performer.** Whisparr Sync asks your Whisparr about an
entity by the identifier your library already stores for it, from StashDB on Whisparr v3 (Eros) or
ThePornDB on Whisparr v2. A card with no such link, or with two links naming different scenes, is
left blank. Nothing is sent for it, and no state is guessed: a badge reading **Not added** would
claim your Whisparr has no entry, which is a different fact from Cove not knowing what to ask about.

**Your Whisparr's own source does not name one entity for the link.** On Whisparr v2 the instance
resolves your stored studio identifier against its own metadata source. Where that source names
nothing, or names more than one series, nothing about the card is established and it is left blank.
The button states no reason for it. Your instance answered every request it was given, and which of
those two it was is a fact about that one card rather than about your connection.

**The status could not be read.** No card claims anything, and the reason is on the button rather
than on every card. Which reason it gives says what happened:

| On the button                                                                                                   | What happened                                                                          |
| --------------------------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------- |
| No Whisparr is connected, so no card can show a status. Nothing was asked of Whisparr.                          | No connection is configured. See the [Settings reference](./settings.md).              |
| The connected Whisparr keeps no record of these, so no card can show a status. It was not asked.                | Your Whisparr's generation keeps no record of this kind of card.                       |
| Cove could not reach Whisparr, so no card can show a status. That is not the same as Whisparr holding nothing.  | Cove asked your instance and no answer came back.                                      |
| Cove could not complete the status read, so no card can show a status. Whisparr may not have been asked at all. | The request to Cove itself did not answer, so whether Whisparr was asked is not known. |

The button still works in every case, and pressing it again asks again.

## What one press costs

Whisparr Sync asks your Whisparr about each card separately, one request after another. A full page
of studio or performer cards is one request to your instance per card, and it takes noticeably longer
than a single card would.

**One press asks about every card on the page, at any page size Cove offers**, including its largest.
Whisparr Sync carries at most 40 cards in one request to itself and sends as many of those as the
page needs, one after another, so no card is left without an answer and none is dropped.

- **Studio and performer cards on Whisparr v3 (Eros):** one request per card.
- **Studio cards on Whisparr v2:** two requests per card. The first asks the instance which of its
  series your stored identifier names, and the instance resolves that against its own metadata
  source. An instance that cannot reach that source establishes nothing, so the card stays blank.
- **Scene cards:** one request for the exclusion list covering each group of up to 40 cards, then one
  per card your library holds a scene identifier for.

Scroll far enough that Cove draws cards it had not drawn yet and those cards are asked about too, as
one further round of requests rather than one per card.

Nothing is cached between pages. Leaving the page and coming back asks again.

## What this does not show

**Pressing the button changes nothing.** It adds nothing to Whisparr, monitors nothing, downloads
nothing and writes nothing to your library. Whisparr Sync itself contacts no metadata source to
answer it: the identifier it asks about is the one your library already stores.

**There is no count row.** The toolbar shows no tally of how many of the cards are monitored, wanted
or absent. A tally that summed to the whole library would mean fetching every matching entity from
Cove and asking Whisparr about all of them on every press, and one that summed only to the forty
cards on screen would read as a figure about your library while being a figure about one page. The
badges say the same thing exactly, per card, at a cost that does not grow with your library.

**There is no marker for owning the file.** A scene card says whether Whisparr holds and monitors
the scene, not whether Whisparr has the file. No Whisparr read reports that, and Cove's own answer
is about your library rather than about Whisparr's.

## What Whisparr v2 can do here

On a v2 connection the button and the badges are absent from the videos and performers lists
altogether. They are not drawn greyed out and no empty box appears on the cards. The studios list
keeps both.

Whisparr v2 keeps no per-scene records, so there is nothing to read for a scene card, and it has no
performer entity at all. A studio is a series matched through the instance's own metadata source, so
studio cards work on both generations.

Switching the connected generation on the settings tab takes effect on the next page load. Reload the
list to see the change.

## Permissions

Reading a card's status needs Cove's **Videos** view permission (`videos.read`). No part of this
surface needs permission to configure extensions, because no part of it writes anything.
