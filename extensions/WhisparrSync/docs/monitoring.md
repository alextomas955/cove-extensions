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

The menu does not report which scope is in force. Whisparr's answer to Cove does not carry the
scope, so the menu always marks Future Scenes whatever the studio is actually set to.

## The three items this version does not carry out

Once Whisparr monitors the entity, three more items appear in the menu. **This version carries out
none of them.** Each row is present and disabled, and says `This version of Whisparr Sync does not
carry out this action.` They are described here so you know what they will do when they arrive, and
what the last one will cost.

- **Add all missing.** Registers every scene Cove holds that Whisparr does not. Nothing is
  downloaded. Whisparr v3 (Eros) only: no route on Whisparr v2 adds a catalogue item.
- **Reflect owned.** Whisparr links each file you already own into its scene's folder. This costs no
  extra disk while Whisparr's hard-link setting is on, and is skipped, with the reason stated, while
  that setting is off. The behaviour is the same on both generations, because neither offers an
  import mode that only links. Whisparr's hard-link setting lives in its own media management
  configuration and is on by default.
- **Search all monitored.** Asks Whisparr to search for every scene it wants for the entity and to
  download what it finds. **This is the one action that downloads.**

## Why the button or an item is unavailable

Cove decides the reason in this order and shows one reason, not several.

| What you see                                                                       | What it means                                                                                                                                                               |
| ---------------------------------------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| No Whisparr instance is connected.                                                 | Nothing is configured. Connect an instance on the Whisparr Sync settings tab.                                                                                               |
| Currently available on Whisparr v3 (Eros)                                          | The connected generation cannot do this. It is the whole button for a performer on Whisparr v2, and it is the row for a single item whose capability that generation lacks. |
| Cove holds no link for this entity that the connected Whisparr can identify it by. | The entity carries no identifier in the connected generation's namespace.                                                                                                   |
| Cove holds more than one conflicting link for this entity.                         | Cove holds two links from the same source naming different entities, so neither can be chosen. Remove the ones that do not belong on the entity's page in Cove.             |
| Cove could not read what Whisparr monitors for this.                               | The read failed. The button says so rather than falling back to its unmonitored appearance, because not knowing is a different fact from Whisparr monitoring nothing.       |
| Waiting for Whisparr to answer the last thing you asked for.                       | A gesture is still on its way. Every item is disabled until it settles.                                                                                                     |

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

The last three rows are what the generation can honour. This version of Whisparr Sync carries out
none of the three on either generation, as above.

## Monitor a whole selection

Select studios or performers in Cove's own lists, then press **Monitor in Whisparr** in the
selection bar.

An overlay asks what to do with every entity you selected:

- A studio selection is offered **Future Scenes**, **All Scenes** and **Stop monitoring in
  Whisparr**.
- A performer selection is offered **Monitor in Whisparr** and **Stop monitoring in Whisparr**.

The three items above that this version does not carry out are not offered in bulk either. Closing
the overlay without choosing sends nothing.

What you choose runs as one background job. Its progress and its result for each entity appear in
Cove's job list, and it ends with a line saying how many were applied and how many were refused.
Nothing on the page you are looking at changes when it finishes.

One gesture takes at most 1000 entities. A larger selection is refused, so split it.

## Permissions

- **Reading** whether Whisparr monitors an entity needs the same Cove permission the entity page
  itself needs.
- **Changing** it, from the button or from the selection bar, needs permission to configure
  extensions. No default Viewer or Member role holds it.
