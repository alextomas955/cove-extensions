---
id: settings
title: Settings reference
---

Every setting on the Whisparr Sync page (Settings → Extensions → Whisparr Sync), in the order it
appears.

## Pipeline health

Whisparr Sync reports how each part of the sync pipeline last behaved. It is a readout, not a setting
— there is nothing to configure. A part that is failing **right now** is reported at the top of the
page, above the connection settings; a part that has already recovered is reported at the bottom,
below the settings sections.

Three parts are reported, each on its own line:

| Row | What it covers |
| ------ | ---------------- |
| Whisparr | Every call the extension makes to your Whisparr instance — connection tests, the folder read, the 15-minute reconcile. |
| Metadata provider | The scene metadata source your connection uses: StashDB on Whisparr v3 (Eros), ThePornDB on Whisparr v2. |
| Import channel | Whisparr's incoming webhook deliveries and the files they name. |

Each line carries the same four facts: what went wrong last (in the provider's own words), when the
part was last working, when it last failed, and — while it is still failing — how many failures in a
row.

### A part that is failing now

If a part is failing right now, its line appears inside a **red** alert with a warning triangle,
headed **"Whisparr Sync isn't working right now"**. The line names the part, quotes the last
outcome, and gives the consecutive failure count, for example:

> **Whisparr is failing** — last outcome "unreachable", 1 consecutive failure.
> Last failed just now · 1 in a row
> `Network is unreachable (host.docker.internal:6971)`

When Whisparr itself is the failing part, this readout does not repeat when it was last reachable —
that time is stated once, under the Whisparr version selector in the **Connection** section. The
metadata provider and the import channel have no other home for theirs, so their lines keep a
**Last healthy …** clause.

If Cove also can't find your imported files, the red block is headed **"Sync problem — Cove can't
find imported files"** instead and reports both findings together.

### A past error stays visible for an hour after the part recovers

When a part starts working again, its error does **not** disappear straight away. It moves into a
separate, muted region headed **Recently recovered** at the **bottom of the page**, below the
settings sections, with a history glyph instead of a warning triangle, and every line there reads in
the past tense:

> **Whisparr recovered** — this was its last problem.
> Last failed 2 minutes ago
> `Network is unreachable (host.docker.internal:6971)`

This is deliberate. Without it, a failure that has already cleared leaves no trace, so you never
find out that anything went wrong — the outage that lost you an import is invisible ten minutes
later. Keeping the error on screen after recovery is what lets you notice a part that keeps failing
and recovering.

**One hour after the failure, the error text clears itself** and the region stops showing that part.
The failure time is kept, so the record of when the part last broke is not lost. An hour is where
the page stops describing the failure in minutes and starts describing it in hours: inside the
window every line under **Recently recovered** reads "… minutes ago" or "just now", so the heading
never claims a recency the lines below it contradict.

A part that is failing **right now** is not affected by any of this. It keeps its error for as long
as it keeps failing, however long ago the first failure was.

**To tell a past error from a current one, read the region, not the error text.** The error sentence
is identical either way. What differs:

| | Failing now | Recovered |
| ------ | ------------- | ----------- |
| Where it appears | inside the red alert block, above the settings | in the muted **Recently recovered** region, below the settings |
| Glyph | red warning triangle | grey history glyph |
| Wording | "*is failing* — last outcome …" | "*recovered* — this was its last problem." |
| Failure count | shown ("3 in a row") | not shown |
| How long the error text is kept | for as long as the part keeps failing | one hour from the failure |

Nothing is shown at all for a part that has never failed, and nothing is shown on a fresh install.
An error clears from the page once that part fails again (the new error replaces it) — Whisparr Sync
keeps only the most recent problem per part, never a history.

**While any part is failing, the Recently recovered region is not on screen.** The red alert has the
page to itself, so a part that recovered earlier waits until nothing is currently failing before its
last problem is readable again. The record is kept either way, so nothing is lost — read it once the
current failure clears.

## Connection

| Setting | What it does | Default | Values |
| --------- | -------------- | --------- | -------- |
| Whisparr URL | The address of your Whisparr instance the extension calls. Until it is set, every action that would add or monitor something is refused before anything is sent. | empty | A URL, e.g. `http://localhost:6969` |
| API key | Authenticates the outbound call to Whisparr. Stored server-side; never shown back to you. Until it is set, those same actions are refused before anything is sent. | empty | Your Whisparr API key (from Whisparr → Settings → General → API Key) |
| Whisparr version | Which Whisparr API generation to use. A successful test auto-selects the detected version. | v3 (Eros) | v3 (Eros) / v2 |

### Refusing before anything is sent

If the URL or the key is unset, the controls that would add or monitor something are disabled and each
one says which setting is missing and where to set it. This replaces the older behaviour, where an unset
address was reported as an unreachable Whisparr — which sent you to check a running instance rather than
to the empty field that was actually the cause.

The refusal happens on the Cove side, before any request leaves, so nothing reaches Whisparr and nothing
is half-created. It reads the same way on both Whisparr versions.

### API key (write-only)

The key is never returned to the browser. When a key is stored, the field renders empty with a **Key
is set** pill. Typing a new value replaces the stored key; leaving the field blank on save keeps the
existing key. This is why the field is always empty on reload — the value stays on the server (see
[Architecture](https://github.com/alextomas955/cove-extensions/blob/main/extensions/WhisparrSync/docs/ARCHITECTURE.md)).

### Whisparr version

Both **v3 (Eros)** and **v2** are supported; a successful test auto-selects the detected version.
Connect, import, and reconciliation work on both. On **v2**, reconciliation matches scenes by their
**ThePornDB id** — Whisparr v2 carries no StashDB id, so the StashDB-id check does not apply (see
[Reconciliation](./reconciliation#matching-on-whisparr-v2)).

The version is shown as two cards at the top of the Connection section: the connected one reads
**Active** and the other offers **Switch →**. Each version's connection — its URL and its
API key — is **remembered separately**, so switching to the other version and
back restores that version's settings without re-entering them. To switch, click **Switch →**, fill
in (or confirm) the other instance's URL and key, **Test connection**, then **Save**. Saving a
version change **reloads the page** so every Whisparr surface re-reads the manifest for the newly
active version.

:::note Some surfaces are v3-only
Studio monitoring and status work on both versions. Performer monitoring, per-scene push, and
exclusions have no Whisparr v2 counterpart; on a v2 connection those controls read "Currently
available on Whisparr v3 (Eros)" and their card badges are not shown. See the
[status reference](./status#per-card-badges).
:::

#### The version Whisparr reported

Under the two version cards, Whisparr Sync shows what your instance actually reported and when that
was checked:

> Whisparr reported version 3.3.4.794 · verified 4 minutes ago
>
> Whisparr last reachable just now

These are **two separate lines because they measure two different things**. The first is the version
string a **Test connection** read off your instance, and the time beside it is when that version was
**last verified**. The second is the last time Whisparr answered *any* call at all — the background folder
read and the 15-minute reconcile both refresh it. A version verified last week beside a Whisparr
reached a minute ago is an honest reading, not a contradiction.

Before your first successful test the version line reads **"Version not verified yet — Test
connection and Cove will record what your instance reports."** That is the normal state of a new
install; it does **not** mean detection was attempted and failed. If detection had failed you would
see it on the **Whisparr** row of the [pipeline health readout](#pipeline-health) instead.

Once recorded, the version changes only when a test against **your stored address** succeeds.
Testing some other address — a second instance you are about to switch to, say — never overwrites
it. Saving a change of address or of generation **discards** the recorded version, because the
reading described the instance you were connected to. The line goes back to the not-verified
sentence until you test the new connection.

### The result line under Test connection

A test result describes **the address that was in the field when you ran it**, so editing the
**Whisparr URL** removes it:

> Connected to Whisparr — Whisparr 2.2.0.108.

That sentence disappears the moment you change the address, along with the "Detected v2 — change
only if this is wrong." note under the version cards. Failure results go the same way. This is the
transient line under the button, not the recorded version above it — that one changes only on a
save.

Nothing is lost. Type the address back and the same result reappears; only a new **Test connection**
produces a new one. Trailing slashes and letter case do not count as a change, so
`http://localhost:6969/` is still the same address as `http://localhost:6969`.

### Folder overlap advisory

An amber advisory above the connection panel reports how your Cove and Whisparr folders line up. It
carries two findings, and it also tells you when it could not compare them at all. Both findings and
the could-not-check line share one block and one **Dismiss** button.

#### A shared root

A Whisparr root folder and a Cove library root sit inside one another, or are the same folder. Cove
imports files where they already are, so a file landing in a shared folder can come back to Whisparr as
a new grab and be picked up again. The advisory names both paths.

This finding is reported on **both Whisparr versions**. A single shared volume is a legitimate layout,
so treat it as a heads-up rather than a fault: if both systems are meant to see the same folder, that is
expected — otherwise point Whisparr's root somewhere outside your Cove library. The
[storage requirement](#storage-requirement) below describes the shared-path model this finding is about.

#### A doubled scene folder

Your Whisparr root folder ends in the same segment the **Scene Folder Format** begins with. With the
root `/data/media/scenes` and the default format `scenes/…`, Whisparr Eros builds each scene folder as
the root plus the format output and writes to `/data/media/scenes/scenes/…` — a doubled path where Cove
can't find the files and every scene shows as missing. The advisory names the offending root, shows the
doubled path it would produce, and suggests the level above (here, `/data/media`).

The Scene Folder Format is an Eros concept and a **Whisparr v2** naming config carries none, so on a v2
connection this finding reads as not applicable. That is not an all-clear: it says the check has nothing
to compare on that connection. Because it reports no finding and asks nothing of you, it is not one of
the advisory's lines — hover the advisory to read it.

#### When the check couldn't run

If Cove could not compare your folders, the advisory says so beside a question-mark glyph and names the
cause. It never stays silent instead, because silence reads as "your folders are fine". There are four
causes:

| What the advisory says | What it means |
| ------------------------ | --------------- |
| No Whisparr connection is saved | Nothing has been compared yet. Add a connection under **Connection** and the check runs on the next load. |
| Whisparr didn't answer the folder read | Whisparr is unreachable, rejected the API key, or answered as something other than Whisparr. **Test connection**, then reload the page. |
| Cove couldn't resolve its own library folders | Cove has no library path it can read. Add one in Cove's settings, then reload the page. |
| The saved Whisparr version isn't one this extension manages | The stored version is neither v3 nor v2. **Test connection** to detect it again. |

:::note The no-connection cause is not shown on the page
The advisory belongs to the connection panel and is only read once a Whisparr URL and key are stored,
so on a first run — before any connection exists — the whole block is absent rather than reporting **No
Whisparr connection is saved**. The other three causes all reach you, because each of them happens with
a connection already saved.
:::

The advisory is guidance only: it never blocks any action and never changes Whisparr's config for you,
so make the change in Whisparr yourself. Dismiss it and it stays hidden for the session, returning next
session while the finding still stands.

## Storage requirement

Whisparr and Cove must see the media library at the **same path**. When Whisparr finishes a download
it tells Cove the file's path, and Cove imports the file **in place** at that exact path — so Cove's
scanner has to be able to read it there. In practice that means both mount the same storage at the
same location (for example both at `/data/media`). If the two see the library at different paths, point
Whisparr's root folder at the path Cove uses. There is no picker for a path mapping, though the stored
options carry a [path-translation table](#advanced-settings-not-shown-in-the-ui) for setups that cannot
be lined up that way.

Sharing one folder between the two is a legitimate layout that needs nothing from you — and it is also
what the [shared-root finding](#a-shared-root) reports, so the advisory and this section describe the
same model from either side.

If they *don't* line up, the settings page shows a red **"Sync problem — Cove can't find imported
files"** banner naming the offending path, so you're never left guessing why imports aren't appearing.
It clears itself as soon as one import succeeds.

## Import webhook

| Setting | What it does | Default | Values |
| --------- | -------------- | --------- | -------- |
| Webhook URL | A ready-to-use URL with an embedded secret that Whisparr posts events to. Editable so you can set a host Whisparr can reach. | read from Whisparr, or derived on first connect | Editable |

The URL and its **Registered** status are read from the **"Cove Whisparr Sync"** connection in your
Whisparr instance. When that connection exists, the URL and status shown are Whisparr's own — the
address it will actually post to, marked **Registered**. Before it exists (a genuine first run, or when
Whisparr is unreachable), the URL is derived from the host you last set — or the address you open Cove
at — and the status reads **Not registered yet**.

**Copy URL** copies it to your clipboard. **Register in Whisparr** adds the connection to Whisparr, or
updates the existing one in place — so re-registering is safe: it never reports an error and never
leaves a duplicate. When you edit the URL only its host is used; the embedded secret is always Cove's
own, re-applied on every register. The host you set is remembered, so a refresh keeps your edit instead
of reverting to the browser address. Cove receives events at this URL and ingests the imported file in
place; see the [Connect guide](./guide) for the host-reachability note and how auto-import behaves.

Below the buttons a **status line** tells you honestly where the webhook stands. It answers for the
version you have selected, and it has three states:

| Status line | What it means | What to do |
| ------------- | --------------- | ------------ |
| **Registered · last event `{time}` ago** (green tick) — or **Registered · no events received yet** before the first import | The **"Cove Whisparr Sync"** connection exists on the selected instance. | Nothing. |
| **Not registered yet** (amber triangle) | Cove read that instance and it has no such connection. | **Register in Whisparr**, or paste the URL into Whisparr → Settings → Connections → Webhook (On Import). |
| **Cove hasn't checked this instance yet** (grey question mark) | Cove has not read the selected instance, so it does not know either way. | **Test connection**, or **Save** to switch to it. |

The third state exists because the answer belongs to **one instance**, not to your setup as a whole.
If you switch generations, the status reads *not checked* until Cove has read the instance you
switched to — it never carries the other instance's answer across. Registering the webhook on your
v3 instance and then switching to v2 correctly shows *not checked*, then *not registered yet*, and
switching back shows *registered* again. An unknown answer is never reported as "not registered".

A muted helper line is always shown: the URL must be reachable **by Whisparr, not from your browser**
— if Whisparr runs on another host or in a container, use an address it can reach (for example
`http://host.docker.internal:5073`), not `localhost`.

![The Import webhook section of the Whisparr Sync settings page, showing the read-only webhook URL field, the Copy URL and Register in Whisparr buttons, and a "Registered · last event" status line.](/img/whisparr-sync/settings-import-webhook.png)

*The Import webhook section: the read-only webhook URL, its Copy URL and Register in Whisparr buttons, and the registration status line.*

:::note Correcting the webhook host
Before a connection exists, the URL is derived from the address you open Cove at, which is not always
the address Whisparr can reach (for example if you browse Cove at `localhost` but Whisparr runs in a
container or on another host). The field is editable, so you can set the host Whisparr can reach — then
**Register in Whisparr** or copy it. Your edit is remembered across a refresh, and once the connection
is registered the URL and status shown come from Whisparr's own connection. Only the host you set is
used; the secret token is always Cove's own. The registration status line ("no events received yet"
until the first import arrives) is the real confirmation it works.
:::

### Sharing a directory with Whisparr

A Cove library root and a Whisparr root can be the same directory, or one inside the other. That is a
normal shared-storage setup and needs nothing from you: auto-import never moves or deletes files inside
a Whisparr root — it only registers them where they already are — and the import guard is fail-closed,
so a file outside a known root is skipped rather than guessed at.

## Add defaults

How Whisparr adds a scene when you send it from Cove.

| Setting | What it does | Default | Values |
| --------- | -------------- | --------- | -------- |
| Tags on add | **Extra** tags applied to what Whisparr adds, on top of the `cove-sync` origin tag that every Cove-initiated add always carries (that one is automatic and is what reconciliation recognises its own adds by). | The settings page prefills `cove`, but the stored default is **empty** — until you open the page and save, adds carry the origin tag only | Any tags |
| Monitor new items by default | Whether a scene Cove adds is set monitored. **Off by default:** Whisparr treats a monitored scene with no file as *wanted* and grabs it on its next search, and every scene Cove adds is one you already have — so monitoring before the file is attached asks Whisparr to download a duplicate. Turn it on once you have reflected your owned files into Whisparr, which makes those scenes eligible for upgrades instead of acquisition. | off | on / off |
| Allow quality upgrades | Let Whisparr replace a grabbed release with a better one, up to the profile cutoff. Applies on Whisparr v3 (Eros) only — Whisparr v2 has no cutoff-upgrade search, so the toggle is shown disabled there and your setting is kept for when you connect a v3 instance. | on | on / off |

### Quality profile

There is no quality-profile setting to pick. Whisparr requires one on anything it creates, so the
extension reads it from your instance at the moment it adds:

- Adding a studio's scenes uses **that studio's own quality profile** in Whisparr — the one its editor
  labels *Quality for newly added scenes*, and the one Whisparr's own studio sync would give them.
- Every other add uses the **first profile your instance offers**.

If your instance offers no quality profile at all, the add stops before anything is sent and reports
that it could not be completed — add a profile in Whisparr and try again.

:::note Monitoring reuses these — no per-entity prompt (advanced)
When you [monitor a studio or performer](./monitoring), the extension creates it in Whisparr (if it
isn't there already) using a quality profile and a root folder it reads from **your instance's own
lists** at that moment — Cove stores neither. There is no per-entity root/profile picker: monitoring
never prompts, so the control stays a single click.

These defaults apply **only to what the extension creates**. If Whisparr already has the studio or
performer, monitoring flips it to monitored and leaves the quality profile and tags you set in
Whisparr alone — it never overwrites them.
:::

## Whisparr file settings

Whisparr's own naming and folder settings, shown here because sync is **in-place**: Whisparr acts on
Cove's real files, so turning any of these on lets Whisparr rename or remove files in the library Cove
points at. The section reads the live values from Whisparr; edits save through the page's **Save** bar
(the server changes only the toggle you flipped and preserves the rest of Whisparr's config).

| Setting | Wire field | Whisparr endpoint | What turning it on does | Default |
| --------- | ----------- | ------------------ | ------------------------ | --------- |
| Rename movie files | `RenameMovies` | Naming | Whisparr renames files in the shared library. | off |
| Replace illegal characters | `ReplaceIllegalCharacters` | Naming | Whisparr rewrites filenames. | off |
| Auto-rename folders | `AutoRenameFolders` | Media management | Whisparr renames folders Cove points at. | off |
| Delete empty folders | `DeleteEmptyFolders` | Media management | Whisparr removes folders in the shared tree. | off |

When any of the four is on, an amber **"Whisparr may change files in your library"** warning names the
on-settings and the in-place risk. Before you've connected, the section prompts you to test the
connection to load the settings; if a saved connection is temporarily unreachable it says so and
points you at **Test connection** to retry (rather than implying setup isn't done). Editing is
available on **Whisparr v3 (Eros)** only — on v2 the section shows a version note (v2's config field
names diverge).

## Sync my library to Whisparr

A one-click way to tell Whisparr about everything Cove already owns, so a first-time setup doesn't
mean hand-selecting every studio one at a time. It runs across your whole library as a single
background job and **never downloads anything** — it registers what you have as present/owned, the
same non-grabbing registration the per-entity [monitor](./monitoring) and push actions use.

Before you run it, the section shows a **preview**: how many studios, performers, and (on Whisparr v3)
owned scenes will sync, and how many are **skipped** for carrying no metadata id yet. Only entities
Cove has identified — with a StashDB id on v3 or a ThePornDB id on v2 — can be registered in Whisparr;
anything unidentified is counted as skipped and left untouched until you identify it. Click **Refresh**
to recount after identifying more of your library.

The feature is two decoupled steps:

- **Add what we have** — the primary **Sync my library to Whisparr** button registers the studios and
  performers (and, on v3, the owned scenes) that carry a metadata id into Whisparr as **present /
  owned**. It tells Whisparr you already have these; it never starts a download.
- **Also monitor what I sync** — a **separate, off-by-default** toggle. Turn it on only if you also
  want Whisparr to keep watching these entities. Adding and monitoring are not fused: you can register
  your library as owned without arming any monitoring.

| Control | What it does | Default | Values |
| --------- | -------------- | --------- | -------- |
| Sync my library to Whisparr | Registers every identified studio/performer (and v3 owned scene) in Whisparr as present, non-grabbing. | — | Button |
| Also monitor what I sync | Whether the sync also sets the synced entities monitored. | off | on / off |
| Monitor scope | When monitoring is on, how much Whisparr watches. | New releases only | New releases only / All releases |

If **Also monitor what I sync** is on, pick a **scope**. **New releases only** (the default) monitors
each entity for genuinely new future scenes while leaving its existing back-catalogue visible but
unarmed, so a sync can't silently turn into "grab everything." **All releases** monitors the whole
back-catalogue as well. The scope maps to Whisparr's own monitor modes and matches the per-entity
[monitoring](./monitoring) choice.

The sync runs as a **background job**: click the button and track it in the **Job Drawer** — it is not
a blocking dialog, so you can keep working while it pages through the library. Re-running is safe:
every registration is idempotent, so an already-present entity is left as-is and no duplicates or new
downloads result.

While the sync runs, the drawer shows how far through your scenes it is — *"Scene 1500 of 6000"* — and
the bar advances steadily from the start. When it finishes, the drawer's last line counts **batches of
scenes**, not scenes: *"12 of 12 units succeeded"* means all twelve batches finished, not that twelve
scenes were registered. Your scene totals are in the running line above it and in Cove's logs. Scenes are
processed in batches so that a sync of a very large library stays fast and the progress bar stays
honest; that wording is the cost of it.

On **Whisparr v2** the sync is **studio (site) level only** — v2 has no per-scene add and no performer
entity, so the preview and the sync cover studios (sites) alone. On **v3** it also registers owned
scenes and performers.

:::note No auto-sync, no schedule, no banner (advanced)
This is a **user-invoked** action only. There is **no** automatic sync when you add something in Cove,
**no** scheduler or recurring sync, and **no** nag banner prompting you to run it — the sync happens
only when you click the button. As with every outward action, adding registers without grabbing; only
an explicit **Search** ever starts a download.
:::

## Advanced settings not shown in the UI

These are persisted in the extension's stored options but have **no control in the settings page**.
All of them are safe to leave at their defaults; changing one means editing the stored options directly.

| Setting | What it does | Default |
| --- | --- | --- |
| Path translation | A table of `covePrefix` → `whisparrPrefix` rewrites, applied when Cove and Whisparr see the same file at different paths (separate containers, different mounts). The **first** rule whose `covePrefix` contains the file's path at a segment boundary wins, and the rewritten path is what the root lookup matches against. An empty table means the two see identical paths. | empty |
| Default monitor scope | Which scope a "Monitor in Whisparr" toggle uses when the caller does not specify one: `NewReleases` (future scenes only) or `AllScenes`. Both keep the add non-grabbing. | `NewReleases` |
| StashDB endpoint | The Cove metadata-server GraphQL URL whose remote ids are the StashDB match key on Whisparr v3. Matched case-insensitively against each video's stored remote ids. Change it only if your Cove metadata server is configured at a different URL. | `https://stashdb.org/graphql` |
| ThePornDB endpoint | The same idea for Whisparr v2, which addresses a site by its ThePornDB id rather than a StashDB id. | `https://theporndb.net/graphql` |
| Webhook host | The scheme, host and port the webhook URL falls back to before a Whisparr connector exists, so an edit you make survives a settings refresh. Not typed directly — it is stored from the webhook URL you edit in the **Import webhook** section. Once the connector is registered, Whisparr's own connector URL is authoritative and this is used only as the first-run default. | empty (derived from the request host) |

## Whisparr status (view option, not a settings-page setting) (advanced)

:::note Library Whisparr status is a view toggle, off by default
Whisparr status for your library is **not** a setting on this page — it is a **view toggle** in the
videos, studios, and performers library toolbars, off by default. Turning it on paints a status badge
on each card and reveals a compact count summary for the current view. Per-scene status also appears
in the scene detail Whisparr tab. All status reads reuse the reconciliation movie set and a single
exclusion read — they make **no StashDB calls** and change nothing. On a Whisparr **v2** connection only
the studios toggle appears: a v2 scene carries no scene-level id, so scene status is not reported there at
all rather than reported wrongly. See the
[Whisparr status reference](./status) for the full picture.
:::
