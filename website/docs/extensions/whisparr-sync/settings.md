---
id: settings
title: Settings reference
---

Every setting on the Whisparr Sync page (Settings → Extensions → Whisparr Sync), in the order it
appears.

## Unidentified-scenes advisory

Whisparr can only reconcile a Cove scene that carries the connected version's provider id (StashDB on
v3, ThePornDB on v2). When some of your scenes have no such id, an **amber advisory** appears at the
top of the settings page — *"Some scenes can't be reconciled"* — naming how many scenes are affected
and the provider they need. It is a guided prompt, not an error: **Identify these scenes** opens your
Cove library so you can identify them, and **Recheck** re-reads the count once you have. When every
scene is identified the advisory shows **nothing** (it is not a persistent "all good" banner), and you
can dismiss it for the current session. It appears only once a connection is configured.

## Connection

| Setting | What it does | Default | Values |
| --------- | -------------- | --------- | -------- |
| Whisparr URL | The address of your Whisparr instance the extension calls. | empty | A URL, e.g. `http://localhost:6969` |
| API key | Authenticates the outbound call to Whisparr. Stored server-side; never shown back to you. | empty | Your Whisparr API key (from Whisparr → Settings → General → API Key) |
| Whisparr version | Which Whisparr API generation to use. A successful test auto-selects the detected version. | v3 (Eros) | v3 (Eros) / v2 |

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
**Active** and the other offers **Switch →**. Each version's connection — its URL, API key, root
folder, and quality profile — is **remembered separately**, so switching to the other version and
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

## Storage requirement

Whisparr and Cove must see the media library at the **same path**. When Whisparr finishes a download
it tells Cove the file's path, and Cove imports the file **in place** at that exact path — so Cove's
scanner has to be able to read it there. In practice that means both mount the same storage at the
same location (for example both at `/data/media`). There is no path-mapping setting; if the two see
the library at different paths, point Whisparr's root folder at the path Cove uses.

If they *don't* line up, the settings page shows a red **"Sync problem — Cove can't find imported
files"** banner naming the offending path, so you're never left guessing why imports aren't appearing.
It clears itself as soon as one import succeeds.

## Import webhook

| Setting | What it does | Default | Values |
| --------- | -------------- | --------- | -------- |
| Webhook URL | A ready-to-use URL with an embedded secret that Whisparr posts events to. Editable so you can correct the host to one Whisparr can reach. | generated on first connect | Editable |

**Copy URL** copies it to your clipboard; **Register in Whisparr** best-effort adds the connection to
Whisparr for you, using the URL as shown (edits included). The embedded secret is a high-entropy token
generated once and reused so the URL is stable — when you edit the URL only its host is used; the token
is always the stored secret. Cove receives events at this URL and ingests the imported file in place;
see the [Connect guide](./guide) for the host-reachability note and how auto-import behaves.

Below the buttons a **status line** tells you honestly where the webhook stands: *registered, last
event `{time}` ago* once events have arrived, *registered, no events received yet* after you register
but before the first import, or an amber *not registered yet* prompt otherwise. A muted helper line is
always shown: the URL must be reachable **by Whisparr, not from your browser** — if Whisparr runs on
another host or in a container, use an address it can reach (for example
`http://host.docker.internal:5073`), not `localhost`.

![The Import webhook section of the Whisparr Sync settings page, showing the read-only webhook URL field, the Copy URL and Register in Whisparr buttons, and a "Registered · last event" status line.](/img/whisparr-sync/settings-import-webhook.png)

*The Import webhook section: the read-only webhook URL, its Copy URL and Register in Whisparr buttons, and the registration status line.*

:::note Correcting the webhook host
The URL is first derived from the address you open Cove at, which is not always the address Whisparr
can reach (for example if you browse Cove at `localhost` but Whisparr runs in a container or on another
host). The field is editable, so you can set the host Whisparr can reach — then **Register in Whisparr**
or copy it. Only the host you set is used; the secret token is always Cove's own. The registration
status line ("no events received yet" until the first import arrives) is the real confirmation it works.
:::

### Root-overlap advisory

The extension also checks whether a Cove library root overlaps a Whisparr root — if they are the same
directory (or one contains the other), an import-in-place can look to Whisparr like a new file and be
re-grabbed. This is surfaced as a **best-effort advisory only**, never a block: cross-mount or
containerized setups legitimately see the same library at different paths, so treat it as a prompt to
check your layout rather than an error. (Auto-import never moves or deletes files inside a Whisparr
root — it only imports them in place.)

## Add defaults

How Whisparr adds a scene when you send it from Cove. The two dropdowns are disabled until a
successful test, then populate from your instance.

| Setting | What it does | Default | Values |
| --------- | -------------- | --------- | -------- |
| Root folder | The Whisparr root folder this library maps to. | none | One of your instance's root folders (loaded live) |
| Quality profile | The quality profile new items are added with. | none | One of your instance's quality profiles (loaded live) |
| Tags on add | Tags applied to what Whisparr adds. Keep `cove` so reconciliation can recognise its own adds. | `cove` | Any tags |
| Monitor new items by default | Whether a scene Cove adds is set monitored (Whisparr keeps looking to grab and upgrade it). | on | on / off |
| Allow quality upgrades | Let Whisparr replace a grabbed release with a better one, up to the profile cutoff. Applies on Whisparr v3 (Eros) only — Whisparr v2 has no cutoff-upgrade search, so the toggle is shown disabled there and your setting is kept for when you connect a v3 instance. | on | on / off |
| Search on add | Locked off. Cove keeps adds search-free so an add can never start a grab loop; only an explicit **Search now** grabs. | off (fixed) | off (not editable) |

![The Add defaults section of the Whisparr Sync settings page, showing the root folder and quality profile dropdowns, the tags-on-add field with a "cove" tag, and the monitor-new-items, allow-upgrades, and search-on-add toggles.](/img/whisparr-sync/settings-add-defaults.png)

*The Add defaults section: root folder, quality profile, tags on add, and the monitor / upgrade / search-on-add toggles.*

:::note Monitoring reuses these — no per-entity prompt (advanced)
When you [monitor a studio or performer](./monitoring), the extension creates it in Whisparr (if it
isn't there already) using **this connection's root folder and quality profile**. There is no
per-entity root/profile picker: monitoring applies your one configured pair and never prompts, so the
control stays a single click.
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

## Import activity

A read-only section listing every auto-import, below Reconciliation. It has no editable settings — it
is a record of what the webhook and the periodic reconcile did.

| Control | What it does |
| --------- | -------------- |
| Refresh activity | Loads the current audit log from the server (a pure read; nothing is changed). |
| Filter chips (All / Imported / Skipped / Flagged) | Narrows the list to one result; a zero-count chip is disabled. |
| Search | Case-insensitive match over the file path, kind, source, event type, and reason. |
| Column headers (When / Scene · file / Source / Result) | Click to sort; click again to reverse. |

Each row shows **When** the import happened (relative time), the **file**, the **Source** (Webhook or
Reconcile), the **Result** (Imported, Skipped — duplicate, or Flagged for manual scan), and a link to
the **Cove item** when one was created. The reconcile interval is fixed at 15 minutes and is not a
configurable setting in this release.

![The Import activity section of the Whisparr Sync settings page, filtered to imported events, showing the summary line, the All / Imported / Skipped / Flagged chips, the search box, and a table of synthetic file names with their source, result, and Cove item.](/img/whisparr-sync/settings-import-activity.png)

*The Import activity log, filtered to imported events against a synthetic fixture library — no real media.*

## Whisparr status (view option, not a settings-page setting) (advanced)

:::note Library Whisparr status is a view toggle, off by default
Whisparr status for your library is **not** a setting on this page — it is a **view toggle** in the
videos, studios, and performers library toolbars, off by default. Turning it on paints a status badge
on each card and reveals a compact count summary for the current view. Per-scene status also appears
in the scene detail Whisparr tab and in the reconciliation table's Whisparr column. All status reads
reuse the reconciliation movie set and a single exclusion read — they make **no StashDB calls** and
change nothing. See the [Whisparr status reference](./status) for the full picture.
:::
