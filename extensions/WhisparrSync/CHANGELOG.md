# Changelog

User-facing changes, newest first.

## Unreleased

- **When your Whisparr build can't offer something, Whisparr Sync now says so instead of asking you to
  try again.** Some operations need endpoints that older Whisparr builds do not carry. The refusal used
  to arrive as *"Something went wrong. Try again in a moment."*, with a Refresh button beside it —
  advice that could never work, because nothing about waiting changes what a build offers. Worse, a
  studio or performer's **Missing** tab read the same refusal as an empty answer and told you *"You own
  every scene Whisparr lists for this studio"* — a reassuring claim about a question that was never
  answered. Both are fixed: the tab now states that the connected Whisparr build does not offer the
  endpoints this needs, and that updating Whisparr adds them, with no retry control. If you are seeing
  this, updating your Whisparr is the fix; nothing is wrong with your connection or your settings.

- **The Whisparr status counts under the videos toolbar no longer hold your whole Whisparr library in
  memory while they are worked out.** The counts come from asking Whisparr for every scene it tracks;
  the answer used to be downloaded in full and kept as a complete copy before anything was counted. It
  is now read as it arrives, keeping only the five facts per scene the counts actually need, and
  Whisparr is asked to leave out the cover paths it would otherwise send. On a large Whisparr the
  memory that costs is about a fifth of what it was. The counts are the same, and so is the number of
  requests — one scene read and one exclusion read, whatever the size of your library. A connection
  that dies part-way through the answer, or goes quiet mid-transfer, now reports as unreachable
  instead of failing the page. This is a Whisparr v3 (Eros) surface; see the entry below for v2.

- **On Whisparr v2 the scene status counts are gone, because they were wrong.** The videos toolbar's
  Whisparr pill, its per-state count row and the per-scene badges no longer appear on a v2 connection, and
  the count request declines before contacting Whisparr. They were never right there: matching a scene needs
  a scene-level id, a v2 scene carries a ThePornDB one where Cove's scenes are matched on a StashDB one, so
  nothing ever resolved — and the row reported that as **Monitored 0 · Unmonitored 0 · Not added (every
  scene)** for every library. "Not added" means *Whisparr does not have the scene*, so a v2 user with a fully
  populated Whisparr was shown a confident report that Whisparr had none of it. Showing nothing is the honest
  answer. Studio status is unaffected and still works on both versions. If you want scene status on v2, what
  it needs is matching v2 scenes on the ThePornDB id both sides carry — a real capability, not a setting, and
  not implemented.

  The memory work that went into this read on v2 is consequently not something you will see: the walk that
  assembles a v2 scene set now hands each site over as it reads it and keeps one site at a time rather than
  building a complete copy of every scene on every site first, but no counts are worked out over it any more.
  The requests are unchanged where the walk is still used — one site list, then two reads per site — and a
  site that fails to read discards the whole walk rather than answering part of it.

- **Saving your Whisparr connection no longer reverts when you save it while something else is
  running.** The settings page lets you press Save, Test connection and Register webhook without
  waiting for each other, and if a save landed while one of the other two was in flight the URL and
  API key you had just typed snapped back to the previous ones — usually noticed only after a reload,
  when the page showed the old instance. Whichever finishes last now builds on what the others saved
  instead of overwriting it, so the connection you saved is the connection you keep.

- **The library sync's progress bar now tells you the truth.** It used to read near 100% within a second
  of starting and stay there for the whole run — one second into a 36-second sync it showed 99.2% — and
  the time-remaining estimate read "0 seconds" for almost all of it. Both came from the same cause: the
  bar's denominator counted only the work started so far, which at one item at a time is always about the
  same as the work finished. The whole run is now measured up front, so the bar starts at zero, advances
  as the sync progresses, and the time remaining is broadly right. As a consequence the **completion line
  now counts batches of scenes rather than scenes** — *"12 of 12 units succeeded"* means twelve batches;
  the scene counts are in the running progress line and in Cove's logs.

- **Syncing a very large library no longer costs memory in proportion to its size.** A sync used to
  create one background work item per scene in your library and hold all of them for the whole run —
  hundreds of megabytes at a million scenes, plus bookkeeping that got slower the more scenes you had, and
  slow enough to affect other jobs running at the same time. Scenes are now processed in at most 64
  batches regardless of library size. Every scene is still visited exactly once; nothing is skipped or
  capped.

- **Syncing your library no longer re-reads your whole Whisparr library once per studio and per
  performer.** A library sync plans one unit per studio and per performer, and each unit downloaded and
  re-parsed everything Whisparr holds — so a library with a few thousand studios did that a few thousand
  times in one run. Each unit now asks Whisparr about **that entity only**: one lookup for its scene ids,
  then those ids fetched in blocks of a thousand. On Whisparr v2 the cost is bounded by how many sites your
  Whisparr tracks rather than by one entity, which is a weaker limit and is stated as one.

- **"Add all missing" can no longer register a studio's entire catalogue into Whisparr.** Whisparr answers
  identically for a studio it has never heard of and one it knows that holds no scenes — both are empty. Read
  as the second, the first made every scene you own under that studio look missing, and the action registered
  the lot. It now tells the two apart and declines the first, asking you to monitor the studio in Whisparr
  first. A studio Whisparr knows behaves exactly as before.

- **The scene detail Whisparr tab now declines on Whisparr v2 instead of showing an empty panel.** v2 has no
  scene-level id, so the tab could never resolve a scene there; it now says so before contacting Whisparr, the
  same way every other per-scene action already did.

- **A studio whose name in Whisparr differs from Cove's now finds its scenes.** Which scenes belong to a
  studio was decided by comparing the studio's name against the name on each Whisparr row, so a studio
  Whisparr had listed under a slightly different name — or under no name at all — reported zero scenes and
  its Monitor and Search actions had nothing to act on. The match is now the studio's own StashDB id, which
  is the same value in both systems, exactly as performers have always matched. A studio whose names
  already agreed sees no change.

- **The "Not at the expected path" section has been removed.** The Wanted, queue & history page now carries
  three tabs: Wanted, Queue and History. The section could not tell a file your library genuinely never took
  up from one it already holds under a second copy, a hardlink, or without a metadata id — so most of what it
  listed needed nothing done, and no amount of guidance above the list changed that. Nothing else on the page
  changes, and nothing it ever reported was acted on: it was read-only throughout.

- **A missing id can no longer make Whisparr Sync read your entire Whisparr library.** Whisparr treats an
  empty search value as no search at all and answers with everything it holds, so a lookup for an id that
  had never been set quietly downloaded the whole library and then threw it away. Those lookups now stop
  before anything is sent, and report that the request was never made — distinct from Whisparr being
  unreachable, which it was not. Lookups with a real id behave exactly as before.

- **Acting on a single scene no longer pulls your whole Whisparr library.** Searching a scene, searching
  it for an upgrade, listing its releases and grabbing one each used to download every scene Whisparr
  tracks just to find the one you clicked — four separate times, once per action. Each now asks Whisparr
  for that scene alone, so the cost of a per-scene action no longer grows with the size of your library.
  One narrow consequence: a scene Whisparr holds under a *different* source's id, while merely carrying
  the same StashDB id, is no longer found. Those actions report the scene as not added rather than acting
  on it, exactly as they already did for a scene Whisparr does not have.

- **The Quality profile setting is gone — Whisparr Sync now reads a profile from your instance each time
  it adds.** Whisparr refuses to create anything without a quality profile it offers, and the stored
  setting started out unset, so a fresh install could not add a single scene until you found and picked
  one. Nothing is stored any more: adding a studio's scenes uses **that studio's own profile** in
  Whisparr — the one its editor labels *Quality for newly added scenes*, and the one Whisparr's own
  studio sync would give them — and every other add uses the **first profile your instance offers**. A
  profile you had saved is simply no longer consulted, and a saved profile your Whisparr no longer
  offers can no longer refuse an action. If your instance offers no profile at all, the add stops before
  anything is sent and says it could not be completed.

- **Scenes you excluded in Whisparr are now reported as skipped, not failed.** If a scene's studio is on
  Whisparr's import-exclusion list, Whisparr refuses to add it — that refusal is your own configuration
  doing its job. Cove counted it as a failure anyway, so a bulk add over a library containing excluded
  studios reported failures you could do nothing about and buried real errors among them. Excluded scenes
  are now left out of both the success and failure counts, and the result says how many were skipped and
  why. A single add of an excluded scene likewise reports a plain "excluded in Whisparr" outcome instead
  of an error. Genuine failures still count as failures, and nothing about what gets added changed.

- **Scenes Cove adds are no longer monitored by default.** Whisparr treats a monitored scene with no
  file attached as *wanted* and grabs it on its next search — and every scene Cove adds is one you
  already have on disk, so the old default quietly asked Whisparr to download a second copy of files
  you own. **Monitor new items by default** now starts off. Turn it on after reflecting your owned
  files into Whisparr: once Whisparr can see the file, monitoring means "upgrade this", which is what
  the setting is for. Nothing else about adding changed — adds are still search-free and origin-tagged.

- **The "Search on add" setting is gone.** It was shown as a locked-off toggle, but it was never wired
  to anything: whether an add searches is decided by Whisparr's own per-studio *Search on Add*, and
  Cove deliberately never asks for a search because it only ever adds scenes whose files you already
  have. A setting that could not change anything has been removed rather than left to imply otherwise.

- **Whisparr v2 site adds no longer send a `seasonFolder` field.** Whisparr v2's series API has no such
  field, so it was silently discarded on every site add. No behaviour changes; the request simply stops
  claiming a folder layout the integration does not have.

- **Now requires Cove `1.1.0` or newer.** Cove 1.1.0 is where the host itself began enforcing
  permissions and authentication on extension pages, tabs and APIs, so it is the oldest release this
  extension can state a security posture against. Nothing about importing, matching, or pushing
  behaves differently; on an older Cove the extension simply will not load rather than loading with
  its access checks unenforced.

- **A reconcile pass that never started now says so in the log.** The fifteen-minute backstop that picks
  up anything the import webhook missed can fail to queue its pass — and when it did, nothing anywhere
  recorded it: the extension carried on, the log stayed silent, and the only symptom was an import
  arriving late or not at all. A lost pass now writes a single warning line naming what went wrong. The
  backstop itself is unchanged — the next pass still runs fifteen minutes later — and nothing about
  importing, matching, or pushing behaves differently.

- **A very large library no longer costs memory it does not need to.** Several reads loaded your whole
  video library into memory to answer a question whose answer was a handful of numbers or a single page
  of rows — the Whisparr status counts on the videos toolbar, the "Not at the expected path" list, the
  Missing tab's owned-scene subtraction on a parent studio or a tag, the library-overlap check, and both
  halves of **Sync my library to Whisparr**. Each now reads the library in bounded pages, or asks only
  about the ids on the page in front of you. Every answer is the same; on a library of hundreds of
  thousands of files or more, the pages that produced them get there instead of stalling. Scrolling a
  large library also no longer grows the page's memory without limit.

- **"Not at the expected path" no longer accuses a scene you own.** Where two videos in your library
  shared one metadata id, the list could pick the wrong one and report the file as missing from Cove. It
  now considers every video carrying the id, so that row correctly drops off the list.

- **Removed:** an unused reconciliation match store. It was never written to, so nothing you have done
  is affected and no setting changed. It was the one piece of saved data that would have grown with your
  library, which is why it is gone rather than trimmed.

- **The Missing toolbar states the partial-menu caveat once, and names the menus it is about.** Where a
  metadata source publishes no list of every value, the same sentence used to sit under each affected
  menu — three copies on a ThePornDB studio page, plus the separate sort caveat. It is now one line on
  its own row in the toolbar, listing the menus it covers, so you can tell which controls it applies to
  instead of reading the same sentence three times. The Year menu still explains itself on hover, the
  sort caveat is unchanged, and no menu was disabled or renamed.

- **A Missing tab on Whisparr v2 explains why its verbs are dim, and stops advising a setting that
  would not change them.** On a v2 connection every per-scene Monitor, Unmonitor and Search is
  unavailable — v2 keeps no scene-level record — yet the tab's one line advised picking a quality
  profile, which would enable nothing there. That line is now shown only where the setting really is
  what dims the controls, so on v3 it is unchanged. In its place v2 gets one message above the grid
  naming the actual cause, covering both the dimmed verbs and the statuses. It offers no Refresh,
  because nothing can clear it, and it implies no move between Whisparr generations. Every dimmed
  control still carries its reason on hover and in its accessible name.

- **Forty cards no longer repeat the same "Status unknown" explanation.** Where every scene on a
  Missing tab abstains for the same reason, the reason is now stated once for the whole set and no card
  carries it. Each card keeps its status glyph and its **Status unknown** label. Where only some scenes
  abstain, or where the cause is a Whisparr read that did not answer, nothing changed.

- **"Not at the expected path" now says what a row means and which rows need nothing done.** The list
  found the files and then left you at them: every row's status just repeated the tab's own title, and
  nothing on the page said what to check. The section now opens with what the list actually proves —
  that nothing in your Cove library holds a file at that exact path and nothing carries Whisparr's
  StashDB or ThePornDB id for it — followed by the three things that can be. The first is that Cove may
  already have the file: a second copy or a hardlink at another path looks exactly the same from here,
  and those rows need no action at all. Each row's status now reads **Whisparr has a file**. The section
  is still read-only, and gained no button.

- **The setting warning is stated once per screen, not once per item.** When a required Whisparr Sync
  setting is not usable, the sentence explaining what that costs you used to repeat on every item it
  affected — on a studio's Missing tab with forty scenes, forty times, taking about a quarter of every
  card. It now appears once on each screen: at the Quality profile picker on the settings page, once in
  a studio or performer's Whisparr menu, once above a scene's Whisparr controls, and once on a Missing
  tab whatever its length. Nothing became less clear at the control itself — every dimmed control now
  says **Needs the Quality profile setting (Add defaults)** on hover, and the Missing tab's line is
  shown whether or not you have selected anything.

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
  A refused bulk action also keeps your selection, so you can fix the setting and press the button again.

- **The quality profile picker now lists your instance's own profiles by name, and your choice saves.**
  Every option read *"Profile undefined"*, picking one snapped straight back to the placeholder, and
  saving failed with an error you could do nothing about. Since a quality profile is the setting the add
  and monitor actions refuse to run without, and this picker is the only place to set one, that refusal
  could not be cleared from the settings page at all. The picker now shows each profile's own name, your
  choice holds, and saving keeps it.

  A save that does fail now explains itself in a sentence, instead of showing an internal address and a
  raw error code beside advice to try again.

- **The settings page now says which part of the pipeline is broken, and keeps saying what broke after it
  recovers.** Whisparr, the metadata provider, and the import channel each get a line reporting the last
  thing that went wrong in the provider's own words, when that part was last working, when it last failed,
  and how many failures in a row. Previously a failure that had already cleared left no trace at all: the
  outage that lost you an import was invisible ten minutes later, and there was nothing on the page to tell
  you a part keeps failing and recovering.

  A part failing right now appears in the red alert. A part that has recovered moves to a muted **Recently
  recovered** region below it, past-tense and without the failure count, so a stale error never reads as a
  live one. Only the most recent problem per part is kept — this is a status readout, not a history — and
  nothing is shown for a part that has never failed.

  Two signals that were misreporting are corrected alongside it. **The webhook status now answers for the
  instance you have selected**, so switching generations reads *"Cove hasn't checked this instance yet"*
  until Cove has read the one you switched to, instead of reporting a live connection as "Not registered".
  And **the version your instance reported is now remembered** and shown with the time it was last
  verified, on its own line beside the separate time Whisparr was last reachable — two clocks measuring two
  different things, no longer one borrowed from the other. An empty value reads as not yet verified, which
  is what a new install is, rather than as a failed detection. The reading is forgotten when you save a
  change of address or generation, so one instance's version is never reported over another's connection.
  Editing the address clears the on-screen test result the same way — the green **Connected to…** line and
  the detected-version note never outlive the address they were read from.

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
  as ThePornDB offers no ordering to ask for.

- **A saved quality profile your Whisparr no longer offers is now refused too, instead of being sent.**
  Previously only an *unset* profile was caught. If you pointed Cove at a different instance, or deleted
  the profile in Whisparr, the actions that add or monitor went ahead with a profile that instance could
  not honour — and Whisparr accepts a studio like that, so it looked like it worked and then never
  acquired anything. Those actions now refuse and nothing is changed in Whisparr. The message says the
  saved profile is not one this Whisparr offers, rather than telling you to pick one you already picked.
  Unlike the unset case, the control is not disabled up front — the refusal appears when you click it,
  because answering that question takes a live look at what your instance currently offers. If Whisparr
  cannot be reached, the check is skipped rather than treated as a failure, so a brief outage never
  disables a working setup. The same on Whisparr v3 (Eros) and Whisparr v2.

- **Adds and monitors now refuse before anything is sent when a required connection setting can't support
  them, and name the setting at the control.** The control is disabled and says which setting is missing
  and where to set it, so you go straight to the empty field. Nothing reaches Whisparr while it is
  refusing for an unset setting, so nothing is half-created. Two symptoms go with it: an unset Whisparr address is no longer
  reported as an unreachable Whisparr — it used to send you to check a running instance rather than to the
  blank field that was the cause — and the quality-profile picker now explains a saved profile the
  connected instance doesn't offer, rather than reading as though it were valid. The behaviour is the
  same on Whisparr v3 (Eros) and Whisparr v2.

  **Deliberately not built: a standing library-wide advisory.** The value here is the refusal *at the
  control* you were about to press, which is where the question comes up. A banner that sits over the
  whole library is a different and larger thing, it reached a working state once before and disappeared
  without a stated reason, and it is excluded from this change on purpose. It is recorded here so it is
  neither re-proposed as an oversight nor removed again as dead code.

- **Sorting and filtering the Missing tab now asks the metadata source, instead of rearranging the forty
  scenes on screen.** Choosing **Oldest first** on a tag holding 160,000 scenes used to give you the
  oldest of the forty that happened to be loaded — scenes from this month. It now gives you the oldest
  scenes in the tag, from 1900 and 1970. Choosing a facet value narrows the whole catalogue and the count
  moves with it: one performer takes that same tag from 160,157 to 41.

  On **Whisparr v3** all three orderings and all four filters are applied by StashDB. On **Whisparr v2**
  all four filters are applied by ThePornDB — including an **exact year**, which StashDB can only
  approximate — and the ordering covers the scenes currently loaded, which the sort control now says
  beneath itself rather than looking like it sorted everything. ThePornDB offers no ordering at all, so
  that half stays over the loaded scenes permanently: it is a property of the source, not a Cove limit
  and not a gap left to close.

  A studio's **Performer** menu and a performer's **Studio** menu now list the entity's whole roster
  rather than the names on the loaded page — 385 performers for a studio whose page carries 52 — and a
  parent studio's **Sub-studio** menu lists Cove's own child studios. Every other menu offers the values
  seen so far and now says so at the control, naming the source that has no such list to give. Those
  controls stay usable: a value picked from a partial menu still filters the whole catalogue.

  Reading a page costs one request to the metadata source whatever the sort and filters, plus one extra
  when you open a studio or a performer, reused for every later page of that entity. Nothing crawls the
  catalogue in the background.

- **The folder advisory now reports on both Whisparr versions, and says when it couldn't check.** The
  amber advisory on the settings page used to flag one thing only — a Whisparr root whose trailing segment
  doubles the Scene Folder Format — and only on Whisparr v3. It now also reports a Whisparr root and a Cove
  library root that sit inside one another, on **both versions**: Cove imports files where they already
  are, so a shared folder can send a file back to Whisparr as a new grab and have it picked up again. A
  single shared volume is a legitimate layout, so this is worded as a heads-up, and it names both paths so
  you can judge it.

  Where the check can't run at all it now names what stopped it — no saved connection, Whisparr not
  answering the folder read, Cove unable to resolve its own library folders, or a saved Whisparr version
  this extension doesn't manage — instead of showing nothing, which read as an all-clear. On Whisparr v2
  the scene-folder check reads as not applicable for the same reason: the Scene Folder Format is an Eros
  setting, so there is nothing on v2 to compare, and saying so is not the same as reporting no problem.

  If you dismissed the old advisory, it appears once more so the new finding reaches you.

- **Search on the Missing tab now does what it reports.** Clicking **Search** on a card, or over a
  multi-selection, used to report success and issue no grab at all — on either Whisparr version. Every
  Search you ran from that tab did nothing. It now issues the grab where it can, and where it can't it
  tells you at the control instead of claiming success: if Whisparr holds no entry for the scene, the card
  states that beneath the button and grabs nothing, so you can mark the scene wanted and let Whisparr take
  it from there. The click is also scoped to the page of scenes it was launched from, so a broad tag that
  used to return nothing after nearly two minutes now answers immediately.

  On Whisparr v2 both Search controls are now disabled and read "Currently available on Whisparr v3
  (Eros)", exactly as their Monitor and Unmonitor neighbours already did — v2 carries no scene-level
  record for a per-scene search to command. **Search all monitored** is a different verb, is unaffected,
  and still runs on both versions.

  A missing scene's Whisparr status now abstains rather than claiming the scene isn't in Whisparr when
  that can't be known. Such a scene reads **Status unknown**: always on v2, which can't report a status
  for an individual scene at all, and on v3 whenever the read from Whisparr doesn't answer. In that second
  case one message above the grid explains it for the whole set instead of marking every card, and the two
  causes are worded differently — v2's is a standing limitation, v3's is an outage you can refresh past.

  A bulk Search still reports no per-scene outcome on the page; only the per-card control tells you what
  happened.

- **A new "Not at the expected path" section on the Wanted, queue & history page** — the files Whisparr
  holds that your Cove library hasn't taken up at the path Whisparr reports, each row carrying that
  path. It is read-only: a row tells you where Whisparr has the file and offers nothing that changes
  Cove or Whisparr. An empty list is the good answer here, so an unreachable Whisparr says it is
  unreachable rather than showing you an empty one. Works the same on Whisparr v3 and v2.

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

### Discovery

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
  <!-- Superseded: per-scene Search is v3-only and is disabled on v2; "Search all monitored"
       is the verb that works on both. Corrected in the entry above. -->
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
