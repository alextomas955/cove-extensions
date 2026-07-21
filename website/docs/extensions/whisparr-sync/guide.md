---
id: guide
title: Connect to Whisparr
---

This guide connects Cove to your Whisparr instance and finishes setup: a verified connection, a
chosen root folder and quality profile, and a webhook Whisparr can call.

Both Whisparr **v3 (Eros)** and **v2** are supported. Setup differs only where the two Whisparr
generations differ, so follow the section for your version — [Configure Whisparr v3
(Eros)](#configure-whisparr-v3-eros) or [Configure Whisparr v2](#configure-whisparr-v2) — then the
shared steps that follow. On either version the API key lives at **Whisparr → Settings → General →
API Key**.

## Open the settings page

Go to **Settings → Extensions → Whisparr Sync**.

:::note Open Settings first
Open **Settings**, then click **Whisparr Sync** in the sidebar. Deep-linking straight to the page
can render it empty.
:::

## Configure Whisparr v3 (Eros)

Whisparr v3 is built on Radarr: each scene is a **movie**, and the extension matches a Cove scene to
a Whisparr movie by the **StashDB id** they share.

### Set up Whisparr v3

Do this in Whisparr's own web UI before you connect Cove:

1. **Add a root folder.** Go to **Settings → Media Management → Root Folders** and add the folder
   Whisparr stores scenes in. For import and reflect to work later, this must be storage Cove can
   also see at the same path — see [Make sure both see the same paths](#make-sure-both-see-the-same-paths).
2. **Add an indexer.** Under **Settings → Indexers**, add at least one indexer so Whisparr has
   somewhere to search. Whisparr v3 identifies scenes against **StashDB**, so a Cove scene needs a
   StashDB id (Cove's Identify feature attaches one) to line up with Whisparr.
3. **Copy the API key** from **Settings → General → API Key**.

![Whisparr v3 Media Management settings showing Movie Naming, File Management, and the Root Folders list.](/img/whisparr-sync/whisparr-v3-media-management.png)

*Whisparr v3's Media Management page — the movie-shaped model (Movie Naming) and the root folders
Cove imports into.*

![Whisparr v3 Indexers settings — an empty Indexers list with an add button, above the shared indexer Options.](/img/whisparr-sync/whisparr-v3-indexers.png)

*Settings → Indexers on Whisparr v3 — add at least one indexer here so Whisparr has somewhere to
search.*

![Whisparr v3 General settings Security section, with the API Key field and its copy control.](/img/whisparr-sync/whisparr-v3-general-apikey.png)

*Settings → General on Whisparr v3 — the API key you paste into Cove lives in the Security section.*

### Connect Cove to Whisparr v3

On the Whisparr Sync settings page:

1. In **Whisparr URL**, enter the address Cove reaches Whisparr at, for example
   `http://host.docker.internal:6969`.
2. In **API key**, paste the key you copied.
3. Click **Test connection**. On success the page shows the instance version and auto-selects **v3
   (Eros)** as the detected version.
4. Under **Add defaults**, pick a **Root folder** and **Quality profile** from the lists that load
   after the test.

![The Whisparr Sync Connection section connected to a Whisparr v3 instance, showing the detected version v3 (Eros) and a green "Connected to Whisparr" result with the instance version.](/img/whisparr-sync/settings-connection.png)

*The Connection section after a successful v3 test: the key stored server-side ("Key is set"), the
detected version, and the reported instance version.*

## Configure Whisparr v2

Whisparr v2 is built on Sonarr: a **site is a series** and a **scene is an episode**, and the
extension matches a Cove scene to a Whisparr episode by the **ThePornDB id** they share.

### Set up Whisparr v2

In Whisparr's own web UI:

1. **Add a root folder** at **Settings → Media Management → Root Folders** — again, storage Cove can
   also see.
2. **Add an indexer** under **Settings → Indexers**. Whisparr v2 identifies content against
   **ThePornDB** (configured under **Settings → Metadata Source**), so a Cove scene needs a
   ThePornDB id to line up.
3. **Copy the API key** from **Settings → General → API Key**.

![Whisparr v2 Media Management settings showing Episode Naming, File Management, and the Root Folders list.](/img/whisparr-sync/whisparr-v2-media-management.png)

*Whisparr v2's Media Management page — the series-shaped model (Episode Naming, Multi Episode Style)
that follows from a site being a series.*

![Whisparr v2 Indexers settings — an empty Indexers list with an add button, above the shared indexer Options.](/img/whisparr-sync/whisparr-v2-indexers.png)

*Settings → Indexers on Whisparr v2 — add at least one indexer so Whisparr has somewhere to search.*

![Whisparr v2 General settings Security section, with the API Key field and its copy control.](/img/whisparr-sync/whisparr-v2-general-apikey.png)

*Settings → General on Whisparr v2 — the API key lives in the Security section, the same place as
v3.*

### Connect Cove to Whisparr v2

The Cove side is the same page and the same steps as v3 — only the address, key, and detected
version change:

1. In **Whisparr URL**, enter your v2 instance address, for example
   `http://host.docker.internal:6970`.
2. Paste its **API key**.
3. Click **Test connection**. The result auto-selects **v2** as the detected version. If the
   detection is ever wrong, set it by hand with the **v3 (Eros) / v2** toggle.
4. Under **Add defaults**, pick a **Root folder** and **Quality profile**.

![The Whisparr Sync Connection section connected to a Whisparr v2 instance, showing the detected version v2 and a green "Connected to Whisparr" result with the instance version.](/img/whisparr-sync/settings-connection-v2.png)

*The same Connection section pointed at a v2 instance: the detected version is v2, and the reported
instance version confirms it.*

Some controls have no equivalent on Whisparr v2 and read **"Currently available on Whisparr v3
(Eros)"** there — you never have to migrate; v2 and v3 are both fully supported. On v2 a Cove studio
monitors as its site (series) and its episodes are searchable, but performer monitoring, per-scene
add/monitor, quality-upgrade search, and exclusions are v3-only. See [Monitor a studio or performer →
On Whisparr v2](./monitoring#on-whisparr-v2) and [Reconciliation → Matching on Whisparr
v2](./reconciliation#matching-on-whisparr-v2) for the details.

## When the connection test fails

Test connection tells you which thing is wrong:

- **Whisparr rejected the API key** — check the key in Whisparr → Settings → General and paste it
  again.
- **Couldn't reach Whisparr** — check the URL and that Whisparr is running.
- **Got a web page instead of the Whisparr API** — the URL points at a proxy landing page, not
  Whisparr.
- **This looks like Whisparr `{version}`, which this version can't manage yet** — connect a Whisparr
  v3 (Eros) or v2 instance; other versions aren't supported.

## Make sure both see the same paths

For auto-import and reflect to work, Whisparr and Cove must see the media library at the **same path**.
When Whisparr finishes a download it reports the file's path, and Cove imports it in place at that
exact path — so Cove's scanner has to be able to read it there. In practice both mount the same
storage at the same location (for example both at `/data/media`). There's nothing to configure here;
if the two differ, point Whisparr's root folder at the path Cove uses. See the
[Settings reference → Storage requirement](./settings#storage-requirement).

## Add the webhook

The **Webhook URL** field shows a ready-to-use URL with an embedded secret. Either:

- Click **Register in Whisparr** to have the extension add the connection for you, or
- Click **Copy URL** and paste it into Whisparr → Settings → Connections → Webhook (On Import).

If auto-register isn't accepted by your build, the copy-paste URL always works.

**Prefer Register in Whisparr.** It configures the secret as an `X-Cove-Token` request header, which
is not recorded by proxy or access logs. The copy-paste URL carries the same secret in the `?token=`
query, which some intermediaries can log; it authenticates fine, but Cove logs a one-time warning when
a webhook arrives that way, so the header (auto-register) is the recommended setup.

:::warning This URL must be reachable by Whisparr, not from your browser
Whisparr calls this URL from wherever it runs. If Whisparr is on another host or in a container,
`localhost` points at Whisparr's own machine, not Cove. Use the address Whisparr can reach — for
example `http://host.docker.internal:5073` from a Docker container — not `http://localhost:5073`.
:::

:::note Run Cove with authentication enabled
If Cove is running with authentication disabled, the first inbound webhook from a remote host trips
Cove's outside-IP failsafe and is rejected while Cove auto-enables auth. Run Cove auth-enabled (the
recommended production posture) so the webhook reaches the extension normally.
:::

## Save

Click **Save** in the bar at the bottom. Your URL, version, root folder, and quality profile are
stored. Your API key is kept server-side — the field stays empty on reload and shows a "Key is set"
pill; leaving it blank on a later save keeps the stored key.

## What happens on an import

Once the webhook is registered, auto-import runs on its own:

- **When Whisparr finishes a grab**, it calls the webhook and Cove ingests the new file **in place** —
  no manual scan or import. The file stays exactly where Whisparr put it; Cove only records it.
- **The scene arrives identified, not blank.** Whisparr already knows the scene's StashDB (v3) or
  ThePornDB (v2) id, so Cove stamps that id on the new item and runs an identify by it — pulling the
  title, date, studio, performers, tags, and cover, and creating the studio and performers if they
  aren't in your library yet — then generates covers, previews, and phashes. You get a fully-formed
  scene without touching it. (If you haven't configured a matching metadata source in Cove, the id is
  still stamped so the scene links correctly; one **Identify** click then fills in the rest.)
- **Enrichment happens once per scene.** A scene that's already identified — because an earlier import
  handled it, or you identified it yourself in Cove — is left untouched: a redelivery, an upgrade, or
  the reconcile pass never re-fetches metadata or overwrites edits you've made.
- **A periodic reconcile is the safety net.** Every 15 minutes the extension also checks Whisparr's
  import history and ingests anything the webhook missed (a dropped delivery, Cove briefly down). An
  import that arrives on both channels is still ingested only once, and existing history from before you
  set this up is not bulk-imported.

## Review what was imported

Scroll to the **Import activity** section on the same page and click **Refresh activity**. It lists
every auto-import with its result, source, time, file, and Cove item:

- **Imported** — the file was added to Cove.
- **Skipped — duplicate** — the same import already arrived (a harmless no-op).
- **Flagged for manual scan** — Cove couldn't import the file directly (it was gone, an unrecognised
  type, or outside a known Whisparr root) and fell back to a library scan. These are worth a look.

Use the filter chips and search to narrow the list. The section is read-only — it changes nothing in
your library.

## Add, search, or monitor a scene

Open a scene in Cove and select the **Whisparr** tab in the detail rail. The tab shows the scene's
Whisparr status and its live controls:

- **Add to Whisparr** appears when the scene isn't in Whisparr yet. Click it to register the scene.
  Whisparr starts tracking it but does not download anything — adding never grabs.
- **Monitor this scene** tells Whisparr to watch the scene and grab quality upgrades. If the scene
  isn't in Whisparr yet, turning monitoring on adds it first, then monitors it.
- **Search for this scene** asks Whisparr to search your indexers and grab the scene now. It is
  available once the scene has been added, and it is the only per-scene control that downloads anything.

These per-scene controls are Whisparr v3 features. On Whisparr v2 a scene comes in when you monitor
its site and search for it, so the scene tab's Add / Monitor / Search read "Currently available on
Whisparr v3 (Eros)" — see [Monitor a studio or performer → On Whisparr v2](./monitoring#on-whisparr-v2).

Whatever Whisparr grabs imports back into Cove through the same auto-import (see [What happens on an
import](#what-happens-on-an-import)) — there is no second import path.

## Run bulk actions on a studio or performer

You drive a whole studio or performer from the extension's Whisparr menu.

1. Open a studio or performer page.
2. In the top-right action row, click the **Whisparr** button to open the Whisparr menu.
3. Choose an action:
   - **Monitor in Whisparr** — monitor (or unmonitor) the entity.
   - **Add all missing** — register every scene Cove holds for this entity that isn't in Whisparr yet.
     They are added as non-grabbing, so nothing downloads.
   - **Reflect owned in Whisparr** — for scenes you already own that match a Whisparr scene, attach your
     existing file so Whisparr shows it as present (no download, and your Cove file is never moved). This needs
     Cove and Whisparr to see the file at the same path (see [Storage requirement](./settings#storage-requirement)).
     On Whisparr v2 the file is registered
     in place; on Whisparr v3 (Eros) it is copied into the movie's folder (a hardlink, so no extra disk, when
     Whisparr's *Use Hard Links* setting is on). Register the scenes first with **Add all missing**.
   - **Search all monitored** — ask Whisparr to search and grab across the entity's monitored scenes.

**Add all missing** and **Search all monitored** appear only once the entity is monitored — the menu
stays quiet until there is something to act on.

### Do it in bulk from the studios or performers list

To act on several studios or performers at once, select them on the **Studios** or **Performers** list
(the same multi-select Cove uses for its own bulk edits), then choose **Whisparr** in the selection bar.
A small chooser offers the same actions over the whole selection: **Monitor** (new releases only, or all
scenes), **Unmonitor**, **Add all missing**, **Search all monitored**, and **Reflect owned in Whisparr**.
Only the actions the connected Whisparr version supports are offered (for example, **Add all missing** and
performer monitoring are Whisparr v3 (Eros) only). Requires a Cove version whose selection bar lets
extensions add bulk actions for studios and performers.

:::note These actions live in the extension's own Whisparr menu, not Cove's ⋮ menu
Cove's built-in ⋮ Actions menu on a studio or performer has no place for an extension to add items
(only the video page's menu does). So the Whisparr actions live in the extension's own menu, opened
from the Whisparr button in the action row — you won't find them under Cove's ⋮.
:::
