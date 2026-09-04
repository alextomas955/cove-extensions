---
id: monitoring
title: Monitor a studio or a performer
sidebar_position: 2
---

Whisparr Sync puts one Whisparr button on a studio page and a performer page in Cove. From it you
ask the Whisparr instance you connected to start monitoring that studio or performer, to stop, and
to change how much of its catalogue the monitor covers.

Connect an instance first. See the [Settings reference](./settings.md).

## Where the button is

In the row of buttons beside the entity's name, at the left of the row, before Cove's own controls.

The button carries Whisparr's own mark instead of a word, in full colour, and the mark is the
connected generation's own: purple for Whisparr v3 (Eros), pink for Whisparr v2. Changing which
generation Cove uses changes the colour of the button. That is expected.

The button reads the state from Whisparr each time you open the page. While that read is on its way
the button is an empty outline and cannot be pressed. Once Whisparr answers:

- **Not monitored:** a plain border and the mark.
- **Monitored:** an accent-coloured border, a faint accent tint behind the mark, and a tick in the
  bottom corner.

The button has no visible label, so its accessible name is the only name it has. That name says
whether Whisparr monitors the entity, and it carries the reason when the button cannot be pressed.
Hover the button to read the same text.

## Turn monitoring on

Click the button to open its menu. Arrow keys move between items and Escape closes it.

### A studio

The menu offers Whisparr's own two scopes, spelled the same way on both generations.

| Scope         | What the request carries                    |
| ------------- | ------------------------------------------- |
| Future Scenes | A date gate set to the moment you press it. |
| All Scenes    | No date gate.                               |

**Future Scenes is the default.** It is the option the menu marks, and it is what a request naming
no scope is treated as.

Three things the menu states, and they are the reason to read them before pressing:

- **The scope is not a limit.** Whisparr monitors every scene it lists for the studio whichever
  scope you choose. What the date gate governs is what a later catalogue read adds.
- **All Scenes marks the back catalogue wanted.** Every scene Whisparr already lists for the studio
  becomes wanted, which spends indexer traffic and disk.
- **On Whisparr v3 (Eros), All Scenes is a one-way door.** Changing the scope back to Future Scenes
  does not undo it: a scene that is already wanted stays wanted. On Whisparr v2 a scope change is
  retroactive and does rewrite the flags, so the menu does not show this warning there.

Neither scope starts a search. Whisparr acquires what it wants on its own schedule, which is why
what you mark wanted is the cost to think about.

### A performer

The menu offers one item, **Monitor in Whisparr**, and no scope. Whisparr expresses no future-only
option for a performer on either generation, so monitoring a performer covers every scene Whisparr
lists for it. The menu says so on the item.

Monitoring a performer needs Whisparr v3 (Eros). On Whisparr v2 the button is disabled and says so.

## Turn monitoring off

**Stop monitoring in Whisparr**, in the same menu.

It stops Whisparr wanting new scenes. **It does not retract what All Scenes already made wanted**: a
scene that is already wanted stays wanted, and Whisparr will still acquire it. Unmonitoring deletes
nothing, in Cove or in Whisparr.

To stop acquisition of a back catalogue you already marked wanted, unmark those scenes in Whisparr
itself. Whisparr Sync has no control that does it.

## Change the scope of a studio

The two scope rows stay in the menu once monitoring is on. Choosing one there changes the scope and
leaves the monitor flag alone.

On Whisparr v3 (Eros) the marked row is the scope the studio is actually set to, read from
Whisparr's own answer each time the page is opened.

On Whisparr v2 that answer does not carry a scope, so **no row is marked** and the menu says it
cannot tell which one is in force. An unmarked pair there is not a fault and not a scope of zero:
choosing a row still applies that scope.

While the studio is not monitored yet, the rows are the monitor gesture rather than a report, and
Future Scenes is pre-selected as the cheaper of the two.

## The three items that appear once the entity is monitored

Once Whisparr monitors the entity, three more items appear in the menu, in the order below. One of
the three is the only thing in Whisparr Sync that downloads, and it says so on its own row.

**A refused press states its reason beneath the control.** Nothing is sent, nothing is changed and
no background job starts. A press of **Reflect owned** that skips because of Whisparr's hard-link
setting states its own reason in the same place. See [What a refused press states](#what-a-refused-press-states).

### Add all missing

Registers every scene Cove holds that Whisparr does not. Nothing is downloaded.

Cove reads the identifiers its own scenes carry for this studio or performer and offers each one to
Whisparr, one at a time. **A scene Whisparr already holds is left alone.** Whisparr answers that it
already has it, Cove counts it and nothing about it is changed: the item registers what is absent
and retracts nothing.

Only the scenes Cove can name are offered. A scene carrying no link in the namespace the connected
Whisparr identifies by is left out, because there is nothing to name it by.

Each registration carries the first quality profile and the first library root Whisparr offers. Cove
reads both when you press the item, and reads them again when the work starts, because they are
Whisparr's to change in between. An instance offering neither is refused before anything is sent,
and the control states which one is absent. See
[What a refused press states](#what-a-refused-press-states).

Once every scene has been offered, Cove asks Whisparr to re-read the entity's catalogue. That is how
a newly registered scene reaches Whisparr's own lists.

The work runs in the background. Its progress and its result appear in Cove's job list, which ends
with a line saying how many were registered, how many Whisparr already held and how many it refused.
Nothing on the entity page changes while it runs.

Whisparr v3 (Eros) only: no route on Whisparr v2 adds a catalogue item at all, so the row is
disabled there and says so.

### Reflect owned

Whisparr links each file you already own into its scene's folder. This costs no extra disk while
Whisparr's hard-link setting is on. The behaviour is the same on both generations, because neither
offers an import mode that only links. Whisparr's hard-link setting lives in its own media
management configuration and is on by default.

Whisparr takes charge of each file it links and names it by its own rules. Cove and Whisparr read the
same library folders, so a later rename inside Whisparr can reach a file Cove holds. Whether such a
rename changes the library's own entry, or only Whisparr's own link to that file, is not known. Do
not assume it cannot.

**It runs by itself when you turn monitoring on**, so turning monitoring on is one press and no
dialog. Press the item to run it again at any time. Monitoring a whole selection runs it too, once
per entity, on the same hard-link condition and inside the selection's own background job. When the
run that started by itself has nothing to do because of the hard-link setting, its own line in Cove's
job list says which of the two readings stopped it.

The work runs in the background. Its progress and its result appear in Cove's job list, and nothing
on the entity page changes while it runs.

Before anything is sent, Cove reads Whisparr's hard-link setting:

- With the setting **on**, Whisparr is asked to link the files, one of the entity's folders at a
  time.
- With the setting **off**, nothing is sent. Where you are told depends on which path ran it. A press
  of **Reflect owned** states the reason beneath the control. A run that started by itself with
  monitoring states it on that run's own line in Cove's job list. Over a selection the reason is not
  reported, for the reason [Monitor a whole selection](#monitor-a-whole-selection) gives. Every
  matched file would otherwise be copied in full and use disk twice, and neither Whisparr generation
  offers a mode that only links.
- When the setting **cannot be read**, nothing is sent either. A press states that reason beneath the
  control, a run that started by itself states it on its own line in Cove's job list, and a selection
  does not report it.

### Search all monitored

**This is the one action in Whisparr Sync that downloads.** Everything else the extension does sets
flags in Whisparr and tells Whisparr where files you already own are.

Press it and Whisparr is asked to look for every scene it wants for this studio or performer, and to
download what it finds. It acts only on what the entity is already monitoring, so the scope you chose
when you turned monitoring on is what decides how much it covers. It changes no flag: an entity is
monitoring exactly what it was monitoring before you pressed it.

It is offered for one studio or one performer at a time, from that entity's own menu. **It is not
offered for a whole selection**, so a selection of a thousand entities cannot become a thousand
searches.

Cove learns only that Whisparr accepted the request, and shows nothing further. What Whisparr then
does with it is Whisparr's own: which indexers it asks, what it accepts and how long it takes are
its settings, and its own screens are where the result appears.

## Why the button or an item is unavailable

Cove decides the reason in this order and shows one reason, not several.

| What you see                                                                       | What it means                                                                                                                                                               |
| ---------------------------------------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| No Whisparr instance is connected.                                                 | Nothing is configured. Connect an instance on the Whisparr Sync settings tab.                                                                                               |
| Currently available on Whisparr v3 (Eros)                                          | The connected generation cannot do this. It is the whole button for a performer on Whisparr v2, and it is the row for a single item whose capability that generation lacks. |
| Cove holds no link for this entity that the connected Whisparr can identify it by. | The entity carries no identifier in the connected generation's namespace, or it carries one that the connected instance's own metadata source does not know.                |
| Cove holds more than one conflicting link for this entity.                         | Cove holds two links from the same source naming different entities, so neither can be chosen. Remove the ones that do not belong on the entity's page in Cove.             |
| Cove could not read what Whisparr monitors for this.                               | The read failed. The button says so rather than falling back to its unmonitored appearance, because not knowing is a different fact from Whisparr monitoring nothing.       |
| Waiting for Whisparr to answer the last thing you asked for.                       | A gesture is still on its way. Every item is disabled until it settles.                                                                                                     |

### What a refused press states

A reason divides into one of two surfaces, and which one it takes is decided by whether the entity
can be monitored here at all. A reason that leaves nothing to offer disables the control and speaks
in the control's own name. A reason that leaves the menu open speaks in a sentence beneath the
control.

The six reasons above are the first kind. They are read when the page opens, so the control is
already disabled and already carrying its reason before you press anything.

Five more reasons are the second kind. Each of them is one attempt failing rather than the entity
being unmonitorable, so the control stays pressable: nothing is sent, nothing is changed and no
background job starts, and fixing the instance and pressing again is the whole remedy.

| What was refused                                         | What you see                                                                                                              |
| -------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------- |
| Whisparr offers no quality profile                       | Whisparr offers no quality profile, so nothing was sent. Add one in Whisparr and try again.                               |
| Whisparr offers no root folder                           | Whisparr offers no root folder, so nothing was sent. Add one in Whisparr and try again.                                   |
| Whisparr answered and declined, or could not be reached  | Whisparr would not do this. Nothing here was changed.                                                                     |
| Whisparr no longer holds the entry                       | Whisparr no longer holds this entry, so there was nothing to act on. Reload the page for its current state.               |
| Whisparr's answer was larger than the extension can read | Whisparr's answer was larger than this extension reads at once, so nothing was changed. Your Whisparr answered correctly. |

Cove reads the quality profile before the root folder, so an instance offering neither names the
quality profile.

The first two arrive only in answer to a press. No read asks Whisparr for a quality profile or a
root folder, because only an action composes something to add. The third arrives on a press and also
on the read the page opens with, where an unreachable instance produces it: there too the control
stays pressable and the sentence appears beneath it, so a reload is one gesture away.

The fourth arrives on Search all monitored, on Add all missing, and on the read Cove takes straight
after a change it asked for. It is Whisparr answering that it holds nothing under that entry, which
is a different fact from Whisparr declining, so reloading the page is the remedy rather than
changing anything in Whisparr. The fifth arrives on any read or press, and it is this extension's
own limit rather than a fault in your instance: nothing in Whisparr needs changing, and a later
press may well be answered with less.

This applies to monitoring, unmonitoring and changing a scope as much as to the three items above.

**About the link:** Whisparr v3 (Eros) identifies against StashDB and Whisparr v2 against
ThePornDB, so which link an entity needs depends on which generation Cove uses. Studio and performer
pages already show the entity's link chips, so you can see there which links Cove holds. If you run
your own metadata provider address, set it under **Metadata provider endpoints** in the
[Settings reference](./settings.md).

## What each generation can do

|                      | Whisparr v3 (Eros) | Whisparr v2 |
| -------------------- | ------------------ | ----------- |
| Monitor a studio     | Yes                | Yes         |
| Monitor a performer  | Yes                | No          |
| Add all missing      | Yes                | No          |
| Reflect owned        | Yes                | Yes         |
| Search all monitored | Yes                | Yes         |

Each row is what the connected generation can honour. A row a generation cannot honour is shown
disabled and carries its reason.

## Monitor a whole selection

Select studios or performers in Cove's own lists, then press **Monitor in Whisparr** in the
selection bar.

An overlay asks what to do with every entity you selected:

- A studio selection is offered **Future Scenes**, **All Scenes** and **Stop monitoring in
  Whisparr**.
- A performer selection is offered **Monitor in Whisparr** and **Stop monitoring in Whisparr**.

None of the three items above is offered in bulk, and both of the ones that act on a whole
catalogue are left out on purpose: over a large selection, one press of Search all monitored would
become one search per entity, and one press of Add all missing would become one background run per
entity. Closing the overlay without choosing sends nothing.

What you choose runs as one background job. Its progress and its result for each entity appear in
Cove's job list, and it ends with Cove's own count of those results. An entity Whisparr now monitors
counts as succeeded. An entity Whisparr declined counts as failed, and so does one whose answer was
larger than the extension reads at once. An entity passed over for a stated reason counts as
skipped: no Whisparr instance connected, no usable link, several conflicting links, a capability the
connected generation lacks, Whisparr offering no quality profile or no library root, or Whisparr no
longer holding the entry.
Nothing on the page you are looking at changes when it finishes.

An entity counts as succeeded only when Whisparr, read again after the change, says it monitors it.
An accepted request that left the entity unmonitored counts as failed.

**Monitoring a selection also links the files Cove already holds**, for every entity in it, on the
same condition [Reflect owned](#reflect-owned) states: Cove reads Whisparr's hard-link setting once
for the whole run, and with that setting off, or unreadable, it links nothing.

**The job list does not tell you whether anything was linked.** Cove computes a run's closing line
from the per-entity counts alone, so the line Whisparr Sync composes about the linking step never
reaches it. The counts are the honest count of the monitoring: an entity Whisparr now monitors
counts as succeeded whether or not its files were linked. To see whether linking is happening, run
**Reflect owned** on one entity from its own menu, where its result does appear.

**Stop monitoring in Whisparr** over a selection leaves behind exactly what it leaves behind for one
entity: it does not retract what All Scenes already made wanted. The overlay states that on the row
before you press it.

One gesture takes at most 1000 entities. Select more and Cove refuses the whole gesture, states the
bound and changes nothing. Select fewer and repeat over the rest. A selection that lists every
entity of a kind reaches the bound on a library of any size, so this is a limit you meet in normal
use rather than an edge case.

## Permissions

- **Reading** whether Whisparr monitors an entity needs Cove's **Videos** view permission
  (`videos.read`). Every default read-only role holds it. A custom role that grants studio or
  performer viewing without it does not, and the button then says Cove could not read the state.
- **Changing** it, from the button or from the selection bar, needs permission to configure
  extensions. No default Viewer or Member role holds it.
- **Search all monitored** needs that same permission. It is the one action here that spends your
  bandwidth and your disk, so it is not reachable by a reader who cannot configure the extension.
