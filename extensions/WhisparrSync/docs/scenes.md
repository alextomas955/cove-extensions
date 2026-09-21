---
id: scenes
title: Control one scene, or a selection of scenes
sidebar_position: 5
---

Whisparr Sync adds a **Whisparr** tab to a scene's page in Cove. The tab says what your Whisparr
holds for that one scene and gives you four controls over it. A second surface, a Whisparr button in
the videos selection bar, applies one of five actions to every scene you selected.

Both need a Whisparr v3 (Eros) connection. Connect an instance first. See the
[Settings reference](./settings.md).

## Where the tab is

In the row of tabs on a scene's own page, beside Cove's own tabs. It is named **Whisparr**.

**Nothing is asked of your Whisparr until you open the tab.** Opening a scene's page sends no
request, and the extension puts nothing anywhere else on that page. The tab reads when you open it,
and it reads again each time you open it.

On a Whisparr v2 connection the tab is absent. See
[What Whisparr v2 can do here](#what-whisparr-v2-can-do-here).

## What the tab says

The tab leads with a header carrying the Whisparr mark, the word Whisparr, and the scene's state on
the right. The state is one of Monitored, Unmonitored, Not added, Excluded or Status unknown.

Beneath the header the tab states up to three facts, all of them your Whisparr's own. It repeats no
title, performer, studio, date or runtime, because Cove's own page already carries those above the
tab.

| Fact                | What it reads                                                 |
| ------------------- | ------------------------------------------------------------- |
| **Quality**         | The name your Whisparr gives the file it holds for the scene. |
| **Quality profile** | The name of the profile your Whisparr applies to the scene.   |
| **Cutoff**          | The quality that profile stops upgrading at, by name.         |

**A fact your Whisparr names nothing for is left out.** A scene it holds no file for draws no
Quality row, and a scene it has no entry for at all draws no fact card. The state in the header is
where you read that, and it reads for every scene.

**A read that did not finish says so on its own**, above the facts, and never in a value's place. So
an absent row is your Whisparr having nothing to name, and never a failed read.

When the scene read succeeds and the profile read does not, the tab keeps the facts it did establish
and says the read did not complete.

## The four controls

Each control is a full-width bar on its own row, in one fixed order. None asks for confirmation. The
controls sit in a rail beside Cove's own, so each names your Whisparr; the selection popover's rows
name the same verbs more shortly.

| Control                         | What it does                                                                         |
| ------------------------------- | ------------------------------------------------------------------------------------ |
| **Add to Whisparr**             | Adds the scene to your Whisparr. Downloads nothing.                                  |
| **Monitor in Whisparr**         | Tells your Whisparr to want the scene. Downloads nothing now.                        |
| **Stop monitoring in Whisparr** | The same control once the scene is monitored. Nothing already downloaded is removed. |
| **Search now**                  | Asks your Whisparr to look for the scene now.                                        |
| **Exclude from Whisparr**       | Puts the scene on your Whisparr's exclusion list, so it is not added again.          |
| **Remove exclusion**            | The same control once the scene is excluded. Takes it back off the list.             |

The set is four controls. Monitor and Stop monitoring are one control with two labels, and so are
Exclude and Remove exclusion, so the tab never shows both labels of either.

After every press the tab reads the scene again and draws what your Whisparr then holds. What
changes on screen is the state and the facts, not a message.

### Search now is the only control that downloads

**Search now is the only control here that can make your Whisparr download a file.** The other three
set flags in your Whisparr and acquire nothing.

It reports that **Whisparr has the search**, and says that what the search finds arrives the way
every other import does. That is what Cove establishes: it asks your Whisparr for the search, then
reads the command back off your instance under the identifier your instance gave it. It does not
report that anything was downloaded, because nothing about a download has happened yet. Whisparr
decides which indexers it asks, what it accepts and how long it takes, and its own screens are where
that appears.

Nothing is retried. One press is one request.

### There is no separate control for better files

A monitored scene already takes better files up to its cutoff on its own. Search now is how you ask
your Whisparr to look straight away. On Whisparr v3 (Eros) a
search for a better file and a search for the scene are the same request, and the scene carries no
separate flag for one, so a second control would send the same thing under another name.

### Excluding is undone where you did it

**Exclude from Whisparr** becomes **Remove exclusion** on that same scene's tab. A scene excluded by
mistake is fixed there, and you do not have to go into Whisparr's own settings for it.

While a scene is excluded, Add, Monitor and Search now are unavailable and say why. Your Whisparr
will not act on an excluded scene.

**An exclusion Cove creates carries the scene's identifier as its name.** In your Whisparr's own
exclusion list the row reads `Scene <identifier>` rather than the scene's title. The row is correct
and removing it works; only the name it displays is the identifier. Cove holds no title at that
point that it could send instead.

## Why a control is unavailable

A control that cannot act is disabled and carries its own reason. Hover it, or read it with a screen
reader, and the reason is the second half of the control's name. Cove gives one reason, not several.

| What you read                                                                                             | When                                                                 |
| --------------------------------------------------------------------------------------------------------- | -------------------------------------------------------------------- |
| Whisparr already holds this scene, so there is nothing to add.                                            | On Add, once your Whisparr holds the scene.                          |
| Whisparr has no entry for this scene, so there is nothing to monitor. Add it first.                       | On Monitor, while your Whisparr has no entry.                        |
| Whisparr has no entry for this scene, so there is nothing to search for. Add it first.                    | On Search now, while your Whisparr has no entry.                     |
| Whisparr only looks for a scene it is monitoring, so nothing was sent. Monitor it first.                  | On Search now, on a scene your Whisparr holds and is not monitoring. |
| This scene is on Whisparr's exclusion list, so Whisparr will not act on it. Remove the exclusion first.   | On Add, Monitor and Search now, on an excluded scene.                |
| Cove could not read what Whisparr monitors for this. That is not the same as Whisparr monitoring nothing. | On all four, while the state is unknown.                             |
| Waiting for Whisparr to answer the last thing you asked for.                                              | On all four, while any one press is on its way.                      |

A press that fails returns all four controls to pressable and states its reason beneath them, so you
can fix the instance and press again.

| What you read                                                                               | What it means                                                   |
| ------------------------------------------------------------------------------------------- | --------------------------------------------------------------- |
| Whisparr would not do this. Nothing here was changed.                                       | Your Whisparr answered and declined.                            |
| Cove could not carry that out. Nothing here was changed; try again shortly.                 | The request did not reach your Whisparr.                        |
| Whisparr offers no quality profile, so nothing was sent. Add one in Whisparr and try again. | On Add. Your Whisparr offers no profile to add the scene under. |
| Whisparr offers no root folder, so nothing was sent. Add one in Whisparr and try again.     | On Add. Your Whisparr offers no library root.                   |

Three reasons cover the whole tab rather than one control, and appear above the controls: no
Whisparr instance is connected, Cove holds no link for the scene that your Whisparr can identify it
by, and Cove holds more than one conflicting link for the scene. The last two are stated apart
because they ask different things of you: the first needs a link added, the second needs the links
that do not belong on the scene's page in Cove removed.

**Nothing is said about your Whisparr's indexers.** Cove does not read your indexer list and disables
no control for it. With no indexer configured, a search reaches your Whisparr and finds nothing, and
Cove does not explain why. Your Whisparr's own screens do.

## What one tab open costs

**Three requests to your Whisparr, each time you open the tab.** One for the scene, one for your
Whisparr's exclusion list, and one for the quality profile the scene names. Nothing is cached, so the
facts are always the ones your instance holds now and leaving the tab and coming back asks again.

The cutoff is the reason for the profile request: your Whisparr states the cutoff on the profile
rather than on the scene, and as a number that has to be resolved against that profile's own
qualities to become a name.

## Act on a selection of scenes

Select scenes in Cove's videos list, then press **Whisparr** in the selection bar.

A small popover opens, headed by the Whisparr mark, the product's name and how many scenes you
selected. It offers five rows, each drawing a glyph and its own name, in one fixed order that never
changes: safest first, the only row that can download fourth, and the row that changes what your
Whisparr accepts in future last.

A row names its verb alone. The popover is already headed with the product's name, so these rows are
shorter than the tab controls above that carry the same verbs.

| Row               | What it does                                                                     |
| ----------------- | -------------------------------------------------------------------------------- |
| 1. **Add**        | Adds every selected scene your Whisparr does not hold yet. Downloads nothing.    |
| 2. **Monitor**    | Tells your Whisparr to want every selected scene. Downloads nothing now.         |
| 3. **Unmonitor**  | Tells your Whisparr to stop wanting them. Nothing already downloaded is removed. |
| 4. **Search now** | Asks your Whisparr to look for every selected scene it is monitoring.            |
| 5. **Exclude**    | Puts every selected scene on your Whisparr's exclusion list.                     |

**Row 4 is the only row that can download files.** Closing the overlay without choosing a row sends
nothing.

There is no row that takes a scene back off the exclusion list. Take one off on that scene's own
Whisparr tab.

### What the selection run reports

What you choose runs as one background job. Its progress and its result appear in Cove's own job
list, and nothing on the page you are looking at changes while it runs.

The entry counts every scene in the selection, and Cove computes the entry's own line from those
counts:

- **Succeeded** is a scene the verb was applied to.
- **Failed** is a scene your Whisparr declined, or one the request did not reach it for.
- **Skipped** is a scene passed over for a stated reason: no instance connected, no usable link, a
  capability the connected generation lacks, your Whisparr offering no quality profile or no library
  root, a scene it already holds, a scene it has no entry for, or a scene it is not monitoring.

A scene selected twice is acted on once. The entry names no scene: it reports counts, so nothing it
holds grows with the size of your selection.

### The two limits

One gesture is bounded, and which limit applies depends on the row.

| Rows          | Limit       |
| ------------- | ----------- |
| 1, 2, 3 and 5 | 1000 scenes |
| 4, Search now | 100 scenes  |

Search now has the lower limit because its cost multiplies outside Cove: one press becomes one search
per scene against every indexer your Whisparr has.

**A selection over a limit sends nothing at all.** Cove refuses the whole gesture, states the limit
that applied in the same overlay, and keeps your selection, so you can select fewer and repeat over
the rest. No part of the selection is acted on.

## What Whisparr v2 can do here

On a Whisparr v2 connection neither surface exists. The Whisparr tab is absent from a scene's page,
and a selection of scenes offers no Whisparr button. Neither is drawn disabled, and no empty box is
left behind.

Whisparr v2 keeps no per-scene records at all, so there is nothing for either surface to read or
write there.

**A studio or performer selection still offers a Whisparr button on Whisparr v2**, and that button
explains what it cannot do. A scene selection offers nothing. The difference is deliberate: a
generation that keeps no scene records has nothing to say about a scene, and the two other selections
do have verbs that work there.

Switching the connected generation on the settings tab takes effect on the next page load. Reload the
page to see the change.

## Permissions

- **Reading** the tab needs Cove's **Videos** view permission (`videos.read`). Every default
  read-only role holds it.
- **Every control on the tab**, and the selection bar's Whisparr button, needs permission to
  configure extensions. No default Viewer or Member role holds it.
- **Search now** needs that same permission, on the tab and in the selection overlay. It is the one
  action here that spends your bandwidth and your disk, so it is not reachable by a reader who cannot
  configure the extension.
