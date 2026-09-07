---
id: activity
title: Wanted, queue & history
---

The **Wanted, queue & history** page is a single read-only place to see what Whisparr is acquiring for
you: what it's still looking for, what it's downloading right now, and what has already arrived. It reads
live from Whisparr every time you open it —
nothing here is a stale local copy, and nothing on the page changes anything in Cove or Whisparr.

It lives in its own home, separate from the main settings page and from your library's owned-scenes
view, so it never clutters either.

## Open the page

Go to **Settings → Extensions → Whisparr Sync**, then open the **Wanted, queue & history** page. Use
the **Refresh** button at the top to re-read every section from Whisparr at any time.

## The sections

A tab bar switches between the sections below. **Wanted** and **Queue** each carry a live count badge so
you can see at a glance how much is outstanding and how much is in flight.

### Wanted

Scenes Whisparr is monitoring but doesn't have a file for yet — everything it's still trying to
acquire. The list is derived live from Whisparr, so it always reflects Whisparr's current state.

A scene lands in **Wanted** when it's monitored in Whisparr without a file — for example, after you
turn on [monitoring](./monitoring) for a studio or performer. When Whisparr finally acquires a wanted
scene and it imports into Cove, the scene gains a file and drops off the Wanted list on the next
refresh — closing the loop.

### Queue

What Whisparr is downloading right now. Each row shows the scene, its progress, and, when Whisparr
reports one, an estimated time to completion. A row that's actively downloading shows a progress bar;
one that's still queued shows an indeterminate bar. The Queue refreshes itself periodically while you
have the tab open, so you can watch progress without clicking Refresh.

This view is read-only — it shows the queue but doesn't cancel or remove items. Manage the queue in
Whisparr itself.

### History

What has already been acquired — the grabbed, imported, and failed events Whisparr recorded. It's the
record of what came in and when.

## Empty vs unavailable

Like the rest of the extension, each section is honest about the difference between "nothing here" and
"couldn't reach Whisparr." An empty Wanted list means Whisparr genuinely has nothing outstanding; a
Whisparr outage shows a distinct unavailable message, never a misleading "nothing wanted." If a read
fails, use **Refresh** to try again.

## On Whisparr v2

Wanted, queue, and history read the same way on Whisparr v2 and v3, and present uniformly as
**Scenes** regardless of the version's underlying model.
