---
id: changelog
title: Changelog
---

User-facing changes, newest first.

## Unreleased

- **The Quality profile setting is gone — Whisparr Sync now reads a profile from your instance each time
  it adds.** Whisparr refuses to create anything without a quality profile it offers, and the stored
  setting started out unset, so a fresh install could not add a single scene until you found and picked
  one. Nothing is stored any more: adding a studio's scenes uses **that studio's own profile** in
  Whisparr — the one its editor labels *Quality for newly added scenes*, and the one Whisparr's own
  studio sync would give them — and every other add uses the **first profile your instance offers**. A
  profile you had saved is simply no longer consulted, and a saved profile your Whisparr no longer
  offers can no longer refuse an action. If your instance offers no profile at all, the add stops before
  anything is sent and says it could not be completed.

- **Now requires Cove `1.3.1` or newer.** Cove 1.1.0 is where the host itself began enforcing
  permissions and authentication on extension pages, tabs and APIs, so that is the oldest release this
  extension could state a security posture against; the floor sits at 1.3.1 because that is the host
  version this extension is now built against. Nothing about importing, matching, or pushing behaves
  differently; on an older Cove the extension simply will not load rather than loading with its access
  checks unenforced.

- **A Missing tab on Whisparr v2 explains why its verbs are dim, and stops advising a setting that
  would not change them.** On a v2 connection every per-scene Monitor, Unmonitor and Search is
  unavailable — v2 keeps no scene-level record — yet the tab's one line advised picking a quality
  profile, which would enable nothing there. That line is now shown only where the setting really is
  what dims the controls, so on v3 it is unchanged. In its place v2 gets one message above the grid
  naming the actual cause, covering both the dimmed verbs and the statuses. It offers no Refresh,
  because nothing can clear it, and it implies no move between Whisparr generations. Every dimmed
  control still carries its reason on hover and in its accessible name.

  *Later corrected: the Quality profile setting has since been removed, so the advisory line no longer
  appears on v3 either. The v2 message above the grid is unaffected.*

- **Forty cards no longer repeat the same "Status unknown" explanation.** Where every scene on a
  Missing tab abstains for the same reason, the reason is now stated once for the whole set and no card
  carries it. Each card keeps its status glyph and its **Status unknown** label. Where only some scenes
  abstain, or where the cause is a Whisparr read that did not answer, nothing changed.

- **The setting warning is stated once per screen, not once per item.** When a required Whisparr Sync
  setting is not usable, the sentence explaining what that costs you used to repeat on every item it
  affected — on a studio's Missing tab with forty scenes, forty times, taking about a quarter of every
  card. It now appears once on each screen: at the Quality profile picker on the settings page, once in
  a studio or performer's Whisparr menu, once above a scene's Whisparr controls, and once on a Missing
  tab whatever its length. Nothing became less clear at the control itself — every dimmed control now
  says **Needs the Quality profile setting (Add defaults)** on hover, and the Missing tab's line is
  shown whether or not you have selected anything.
  *Later corrected: the Quality profile setting has since been removed, so neither its picker nor the
  **Needs the Quality profile setting (Add defaults)** hover text exists any more. Stating a reason once
  per screen still applies to the reasons that remain — see the first Unreleased entry above.*

- **A dimmed control says what it is before it says why.** A screen reader used to read the whole
  thirty-word warning where a control's name should have been, so "Monitor" was announced as a
  paragraph about quality profiles and never as Monitor. Every guarded control now reads as its own
  name followed by the reason. **Sync my library to Whisparr** gained a reason at all — it was dimmed
  with nothing to hover and nothing to hear.

- **One sentence was removed, not relocated.** The settings page printed the quality-profile
  consequence twice: once beside **Sync my library to Whisparr** and once at the **Add defaults**
  quality-profile picker. The copy beside the sync button is gone. The same sentence still renders on
  the same page, at the picker that resolves it, and the sync button keeps the setting name on hover
  and in its accessible name.
  *Later corrected: the Quality profile setting and its picker have since been removed, so the sentence
  this entry relocated no longer renders anywhere — see the first Unreleased entry above.*

- **A part that recovered stops looking like a part that is broken.** When Whisparr, the metadata
  provider or the import channel failed and then started working again, the **Recently recovered**
  region kept its error on screen for good — so an error from hours ago sat under a heading claiming
  recency, second from the top of the settings page, with no way to clear it. The error text now
  clears itself one hour after the failure, which is the point at which the page stops describing the
  failure in minutes; the failure time itself is kept, so the record of when the part last broke is
  not lost. A part that is failing right now is untouched and keeps its error, and its red alert, for
  as long as it keeps failing.

- **The settings page opens on your settings, not on half a screen of notices.** The **Recently
  recovered** region has moved below the settings sections, so the Connection section is the first
  thing on the page. The red "isn't working right now" alert has not moved — a current problem still
  greets you at the top.

- **Whisparr's last-reachable time is stated once.** It was printed both under the Whisparr version
  selector and again in the health readout, from the same measurement. The health readout no longer
  repeats it; the metadata provider and the import channel keep their own **Last healthy** lines,
  since nothing else on the page states those.

- **The folder advisory no longer prints a line about a check that could not run.** On a v2 connection
  the sentence *"The scene-folder-format check reads a setting that only Whisparr v3 (Eros) has, so
  there's nothing for it to compare on your v2 connection."* used to sit among the findings, where it
  read as another problem. It is not deleted — it is now the advisory's tooltip, so hovering the
  advisory still shows it.

- **A Missing-tab action that can't run now tells you why, at the button you pressed.** Monitor, Unmonitor
  and Search on a card, the selection bar's three bulk actions, and **Monitor all** all used to do nothing
  visible when the request was refused: the card flicked back to how it was, and no message appeared
  anywhere. The most likely cause was a quality profile your Whisparr no longer offers — a setting you
  could not have known was the problem, because nothing on the page can tell before you click.

  Each of those seven actions now states the reason as a line beneath the control you used: on the card for
  the per-card actions, on its own row in the selection bar for the bulk ones. Where the cause is a setting
  the line names it and the section to change it in; where Whisparr can't be reached it says that instead.
  A refused bulk action also keeps your selection, so you can fix the setting and press the button again. See
  [Discovery → Card actions](./discovery.md#card-actions).

- **The quality profile picker now lists your instance's own profiles by name, and your choice saves.**
  Every option read *"Profile undefined"*, picking one snapped straight back to the placeholder, and
  saving failed with an error you could do nothing about. Since a quality profile is the setting the add
  and monitor actions refuse to run without, and this picker is the only place to set one, that refusal
  could not be cleared from the settings page at all. The picker now shows each profile's own name, your
  choice holds, and saving keeps it. See
  [Settings → Quality profile](./settings.md#quality-profile).

  A save that does fail now explains itself in a sentence, instead of showing an internal address and a
  raw error code beside advice to try again.

  *Later corrected: the quality profile picker has since been removed altogether. Nothing is stored, and
  a profile is read from your Whisparr instance on each add, so the add and monitor actions no longer
  refuse for want of this setting — see the first Unreleased entry above.*

- **The settings page now says which part of the pipeline is broken, and keeps saying what broke after it
  recovers.** Whisparr, the metadata provider, and the import channel each get a line reporting the last
  thing that went wrong in the provider's own words, when that part was last working, when it last failed,
  and how many failures in a row. Previously a failure that had already cleared left no trace at all: the
  outage that lost you an import was invisible ten minutes later, and there was nothing on the page to tell
  you a part keeps failing and recovering.

  A part failing right now appears in the red alert. A part that has recovered moves to a muted **Recently
  recovered** region below it, past-tense and without the failure count, so a stale error never reads as a
  live one. Only the most recent problem per part is kept — this is a status readout, not a history — and
  nothing is shown for a part that has never failed. See
  [Settings → Pipeline health](./settings.md#pipeline-health).

  Two signals that were misreporting are corrected alongside it. **The webhook status now answers for the
  instance you have selected**, so switching generations reads *"Cove hasn't checked this instance yet"*
  until Cove has read the one you switched to, instead of reporting a live connection as "Not registered".
  And **the version your instance reported is now remembered** and shown with the time it was last
  verified, on its own line beside the separate time Whisparr was last reachable — two clocks measuring two
  different things, no longer one borrowed from the other. An empty value reads as not yet verified, which
  is what a new install is, rather than as a failed detection.

- **A bookmarked Missing view now asks the metadata source for the ordering and filters the link names.**
  Previously opening a shared or bookmarked link restored every control to the value it named but applied
  that value only to the page of scenes that happened to load — so a link saying "oldest first" on a
  160,000-scene tag gave you the oldest of the newest forty, and on Whisparr v3 it said nothing about it,
  because the control reported what the source *could* order rather than what had actually been asked.
  Opening the link now narrows or reorders the whole catalogue, and the sort control's note appears
  whenever the scenes on screen were not ordered by the source. Refresh no longer drops your ordering
  either. A link carrying a filter costs one extra read as it opens — the menu that resolves a filter name
  arrives with the first answer — and a link carrying only a sort or a year costs no extra read. On
  Whisparr v2 the filters apply to the whole catalogue and the ordering still applies to the loaded scenes,
  as ThePornDB offers no ordering to ask for. See [Discovery](./discovery.md#what-each-page-costs).

- **A saved quality profile your Whisparr no longer offers is now refused too, instead of being sent.**
  Previously only an *unset* profile was caught. If you pointed Cove at a different instance, or deleted
  the profile in Whisparr, the actions that add or monitor went ahead with a profile that instance could
  not honour — and Whisparr accepts a studio like that, so it looked like it worked and then never
  acquired anything. Those actions now refuse and nothing is changed in Whisparr. The message says the
  saved profile is not one this Whisparr offers, rather than telling you to pick one you already picked.
  Unlike the unset case, the control is not disabled up front — the refusal appears when you click it,
  because answering that question takes a live look at what your instance currently offers. If Whisparr
  cannot be reached, the check is skipped rather than treated as a failure, so a brief outage never
  disables a working setup. The same on Whisparr v3 (Eros) and Whisparr v2. See the
  [settings reference](./settings.md#quality-profile).

- **Adds and monitors now refuse before anything is sent when a required connection setting can't support
  them, and name the setting at the control.** The control is disabled and says which setting is missing
  and where to set it, so you go straight to the empty field. Nothing reaches Whisparr while it is
  refusing for an unset setting, so nothing is half-created. Two symptoms go with it: an unset Whisparr address is no longer
  reported as an unreachable Whisparr — it used to send you to check a running instance rather than to the
  blank field that was the cause — and the quality-profile picker now explains a saved profile the
  connected instance doesn't offer, rather than reading as though it were valid. The behaviour is the
  same on Whisparr v3 (Eros) and Whisparr v2. See the
  [settings reference](./settings.md#refusing-before-anything-is-sent).

  **Deliberately not built: a standing library-wide advisory.** The value here is the refusal *at the
  control* you were about to press, which is where the question comes up. A banner that sits over the
  whole library is a different and larger thing, it reached a working state once before and disappeared
  without a stated reason, and it is excluded from this change on purpose. It is recorded here so it is
  neither re-proposed as an oversight nor removed again as dead code.

- **Sorting and filtering the Missing tab now asks the metadata source, instead of rearranging the forty
  scenes on screen.** **Oldest first** on a tag holding 160,000 scenes used to give you the oldest of the
  forty that happened to be loaded; it now gives you the tag's oldest scenes, from 1900 and 1970. Choosing
  a facet value narrows the whole catalogue and the count moves with it — one performer takes that tag
  from 160,157 to 41. On Whisparr v3 all three orderings and all four filters are applied by StashDB; on
  Whisparr v2 all four filters are applied by ThePornDB, including an exact year that StashDB can only
  approximate, while the ordering covers the scenes currently loaded and the control says so. ThePornDB
  offers no ordering at all, so that half stays over the loaded scenes permanently — a property of the
  source, not a Cove limit and not a gap left to close. A studio's
  Performer menu and a performer's Studio menu now list the whole roster rather than the loaded page, and
  every menu that can't offer one says so at the control while still filtering the whole catalogue. See
  the [Missing tab reference](./discovery.md#where-a-facets-options-come-from).

- **The folder advisory now reports on both Whisparr versions, and says when it couldn't check.** It used
  to flag a Whisparr root that doubles the Scene Folder Format, and only on Whisparr v3. It now also
  reports a Whisparr root and a Cove library root that sit inside one another — on both versions — because
  a shared folder can send an imported file back to Whisparr as a new grab. Where the check can't run it
  names what stopped it rather than showing nothing, which read as an all-clear; on Whisparr v2 the
  scene-folder check reads as not applicable, since the Scene Folder Format is an Eros setting. See the
  [settings reference](./settings.md#folder-overlap-advisory).

- **Search on the Missing tab now does what it reports.** Clicking **Search** on a card, or over a
  multi-selection, used to report success and issue no grab at all, on either Whisparr version. It now
  issues the grab where it can and tells you at the control where it can't — if Whisparr holds no entry
  for the scene, the card states that beneath the button and grabs nothing. On Whisparr v2 both Search
  controls are now disabled and read "Currently available on Whisparr v3 (Eros)", as their Monitor and
  Unmonitor neighbours already did; **Search all monitored** is unaffected and still runs on both
  versions. A missing scene whose Whisparr status can't be known now reads **Status unknown** instead of
  **Not added**, and an unreachable Whisparr is explained once above the grid rather than on every card —
  see [Missing tab reference](./discovery.md).

- **A new "Not at the expected path" section on the Wanted, queue & history page** — the files Whisparr
  holds that your Cove library hasn't taken up at the path Whisparr reports, each row carrying that path,
  so you can see where Whisparr has a file you can't find. It is read-only: a row reports the mismatch
  and offers nothing that changes Cove or Whisparr. An empty list is the good answer here, so an
  unreachable Whisparr says it is unreachable rather than showing you an empty one. Works the same on
  Whisparr v3 and v2 — see [Wanted, queue & history](./activity.md).

- **Removed three endpoints nothing could reach, and the documentation that described them as
  features.** `GET /reconciliation`, `GET /root-overlap` and `POST /rootfolders` had no caller: the
  reconciliation table was deliberately removed from the settings page earlier, and the other two were
  never wired to any control. The docs had drifted with them — the guide told you to pick a **Root
  folder** that no longer exists as a control, the reconciliation page documented a table you could
  open with confirm/reject buttons, and the status page described a Whisparr column on that table. All
  of that is corrected. What actually runs is unchanged: the 15-minute reconcile still imports anything
  the webhook missed, and sharing a directory with Whisparr is still safe because the import guard is
  fail-closed.

- **"Monitor all" is no longer offered on a tag page** — a tag can match tens of thousands of scenes, so marking
  every missing one wanted in a single click isn't something you could reasonably intend or undo. It stays on
  studio and performer pages, and the per-card and multi-selection Monitor still work everywhere, including on
  tags. A whole-tag request is refused by the server too, not just hidden in the UI.

- **The Missing tab reports the real catalogue size again** — on Whisparr v2 the count badge and the "X–Y of N"
  line showed the size of the single page just fetched (always 40), so a tag with thousands of scenes claimed 40
  and offered one page. The tab now uses the source's own count (766 for a performer, 392 for a studio) and pages
  through all of it. ThePornDB stops counting at 10,000, so a broader set reads "10,000+" rather than an exact
  figure it cannot vouch for.

- **Tags you never linked now work** — a Cove tag usually has no metadata-source id, and a source needs one to
  list a tag's scenes, so most tag Missing tabs had nothing to show. The extension now looks an unlinked tag up by
  **name** on the connected source (StashDB on v3, ThePornDB on v2) and uses the match; a stored id still wins when
  there is one. Only a confident exact match counts (StashDB also accepts aliases) — if the source knows the idea
  under another label, the tab still says it has no id rather than showing a different tag's scenes.

- **Missing-scene covers no longer come up blank** — the card now takes the one image field the source
  actually serves (ThePornDB's poster rather than its landscape image, which points at studio CDNs that refuse
  outside requests), matching the field Cove itself uses for a scene cover. Cards no longer issue requests that
  were always going to fail.

- **The tag Missing tab now works on Whisparr v2** — previously it was registered for v3 (StashDB) only and a v2
  tag request was refused outright. ThePornDB filters scenes by tag, so a v2 connection now gets the same rich,
  paged tab on tag pages. A tag Cove hasn't linked to ThePornDB shows the honest "not linked to ThePornDB yet"
  state. As on v3, a tag's missing set is diffed against your **whole** library — a scene you own under any tag is
  never listed as missing.

  Because a tag can match far more scenes than ThePornDB will report, the tag tab shows a window onto the newest
  matching scenes and loads more as you page, rather than claiming an exact total.

- **The performer Missing tab now works on Whisparr v2** — previously it appeared only for v3 (StashDB)
  users. ThePornDB lists a performer's scenes, so a v2 connection now gets the same rich, paged,
  filterable tab on performer pages: cover, title, date, studio, performer chips with avatars, tags and
  description, each with its Whisparr status. A performer Cove hasn't linked to ThePornDB shows the
  honest "not linked to ThePornDB yet" state rather than an empty grid. Nothing new to configure — the
  catalogue is read with the ThePornDB credential Cove already has.

  Note that performer *monitoring* remains v3-only: Whisparr v2 has no performer entity to monitor. Only
  the discovery (Missing) tab gained v2 support.

## v1.4 — Discovery

Whisparr Sync grows from "reconcile what you own" into a way to find what you don't. It surfaces the
scenes a studio, performer, or tag offers that Cove doesn't have yet, lets you mark them wanted or grab
them, and gives you one read-only place to watch what Whisparr is acquiring — all with the same
near-zero setup and no download-loop risk.

- **Discover missing scenes** — a new **Missing** tab on studio, performer, and tag pages shows the
  scenes that entity offers on its metadata catalogue that Cove doesn't own yet (its catalogue minus
  what you own minus what you've excluded in Whisparr — never just "Whisparr has no file"). Scenes
  render as a card grid mirroring Cove's native videos card — cover, title, date and studio, performer
  chips with avatars, description, and performer/tag counts — each with an always-on Whisparr status
  (Not added / Wanted / Unmonitored). A live count badge tracks the total, and the list loads more as
  you scroll, so even a broad tag with tens of thousands of scenes stays responsive.
- **Search, sort, and filter** — narrow a large catalogue by title, sort it (newest, oldest, or
  title), and filter by performer, studio, tag, or year. The filters adapt to the page — a studio page
  filters by performer and tag, a performer page by studio and tag — and a parent studio's Missing tab
  aggregates all its child studios' catalogues, with a sub-studio filter across them. Your view is kept
  in the page URL, so you can bookmark or reload it.
- **Act on what's missing** — mark a scene **wanted** (Monitor), stop watching for it (Unmonitor), or
  **Search** to grab it now — per card, or across a multi-selection, or over the whole list with
  Monitor all. Bulk actions run as background jobs you follow in the Job Drawer. Marking wanted arms
  Whisparr's acquisition without grabbing; only an explicit Search downloads now, so acting from the
  Missing tab can't start a download loop. Marking wanted is available on Whisparr v3; on v2 you monitor
  the whole studio instead. Search works on both versions.
  *Later corrected: per-scene Search is v3 only and is disabled on v2. "Search all monitored" is the
  verb that runs on both — see the Unreleased entry above.*
- **No extra credentials** — the Missing tab reads its catalogue directly from **Cove's own configured
  metadata source** (StashDB on v3, ThePornDB on v2, under Settings → Scraping → Metadata servers) for
  rich cards with covers, performer avatars, tags, and descriptions — the same source for every entity,
  monitored or not. Those rich cards land the same on **both** Whisparr versions — a v2 studio (ThePornDB)
  matches a v3 studio (StashDB) field for field — and every source is held to the same card completeness,
  so a card never quietly drops to a thinner layout. There's no separate key to enter in the extension,
  and Whisparr is read only for each scene's status. If Cove has no matching metadata source set up, the
  tab tells you how to add one rather than showing a misleading empty list.
- **Wanted, queue & history** — a new read-only page in its own home (separate from the settings page
  and your library) shows what Whisparr is still looking for (**Wanted**), downloading now (**Queue**,
  with live progress), and has already acquired (**History**). Everything is read live from Whisparr,
  and a wanted scene clears from the list once it arrives in Cove. Wanted, queue, and history read
  uniformly across v3 and v2.
- **Honest states everywhere** — a metadata-source or Whisparr outage is never rendered as "you own
  everything" or "nothing wanted"; each surface distinguishes an empty result from an unavailable one
  and offers a retry.

The Missing tab replaces its earlier local per-scene hide with Whisparr's own exclusion — excluding a
scene in Whisparr removes it from the missing set, so the hide and the acquisition pipeline stay in
agreement. The tag tab and the per-scene wanted controls need Whisparr v3; v2 keeps its site/episode
model.

## v0.1.0 — Initial release

Whisparr Sync keeps a Cove library and a Whisparr instance in agreement, in both directions, with
near-zero setup. It works with both Whisparr **v3 ("Eros")** and **v2** — the version is detected
automatically and the extension keys on the id each carries (StashDB on v3, ThePornDB on v2).

- **Connect** with a guided setup — enter the URL and API key, test the connection, and pick a
  quality profile from auto-populated lists. The connection panel reports exactly what happened
  (connected, wrong key, unreachable, or not-Whisparr), names the version it detected, and stays
  honest when a saved connection is only temporarily unreachable.
- **Automatic import** — when Whisparr finishes a grab, Cove ingests the new file automatically via a
  webhook, with a periodic reconcile against Whisparr's history as a backstop so nothing is missed.
  Imported scenes are auto-identified by the StashDB/ThePornDB id Whisparr already carries — title,
  date, studio, performers, tags, and cover, creating the studio and performers when missing and
  generating covers/previews/phashes — so you never land a blank item. Enrichment runs once per scene
  and never overwrites your edits. If Cove can't find an imported file — for example when Whisparr and
  Cove see the library at different paths — the settings page flags it with a warning banner that
  clears as soon as an import succeeds. Setting up the import webhook is friendlier too: the settings
  page reads its URL and registered status from Whisparr's own
  connection, so it shows the address Whisparr actually posts to and whether it's registered; Register
  updates the existing connection in place — re-registering never errors or creates a duplicate — and
  the host you set is remembered across a refresh.
- **Monitor from Cove** — turn Whisparr monitoring on for a studio or performer from its Cove page, or
  in bulk across the studios/performers list, with a quiet "Monitored · present / catalogue" status
  line. Choose the scope — **All scenes** or **New releases only**, mapped to Whisparr's own modes;
  "New releases only" leaves the existing back-catalogue visible but unarmed, so it can't silently turn
  into "grab everything." On Whisparr v2 a studio monitors as its site (series) by ThePornDB id, and
  "Search all monitored" runs the episode search. Adding never grabs.
- **Push, search & exclude** — from a scene's Whisparr panel or in bulk (across a studio/performer, or
  a multi-selection on the videos list): add a scene, monitor or unmonitor it, search for it, grab
  quality upgrades, run an interactive release search, or exclude / un-exclude. Monitoring never
  downloads — a scene not yet in Whisparr is registered search-free first, and unmonitoring a scene
  Whisparr doesn't have is simply skipped. Adding never downloads either — only an explicit search
  does — so pushing your library to Whisparr can't start a download loop. Every action reports a
  plain-English reason if it fails.
- **Sync my library to Whisparr** — one click on the settings page registers everything Cove already
  owns (studios and performers, plus owned scenes on v3) in Whisparr as present, so a first-time setup
  doesn't mean hand-selecting every studio. A preview counts what will sync versus what's skipped for
  lacking a metadata id; a separate, off-by-default toggle also monitors what you sync at a scope you
  pick (New releases only or All releases). It runs as a background job you track in the Job Drawer,
  it's safe to re-run, and — like every add — it registers without downloading. On Whisparr v2 it
  covers studios (sites) only, and it registers each owned studio's site even with "Also monitor" off
  (previously a clean v2 registered nothing) — the registration never grabs and is safe to re-run.
- **Edit Whisparr's file settings from Cove** — because sync is in-place, the settings page surfaces
  Whisparr's own file-affecting toggles (rename movie files, replace illegal characters, auto-rename
  folders, delete empty folders) with a warning that they act on Cove's real files. Available on
  Whisparr v3; saving preserves the rest of Whisparr's config.
- **In-library status** — an opt-in library view shows each scene's Whisparr state (downloaded /
  monitored / not added / excluded); off by default, so nothing changes until you turn it on.

**Safety.** Every outward action is idempotent and tagged as Cove-originated, adding never triggers a
download (only an explicit search does), the inbound webhook is authenticated with a generated secret,
and the extension never moves or deletes files inside a Whisparr-managed folder. It warns if a Cove
library root overlaps a Whisparr root. On Whisparr v3 it also warns — advisory only, never blocking —
when a root folder's trailing segment matches the start of the Scene Folder Format (for example root
`/data/media/scenes` with the default `scenes/…` format), which would make Whisparr write to a doubled
`/data/media/scenes/scenes/…` path where Cove can't find the files, and it suggests the root to use
instead (`/data/media`).
