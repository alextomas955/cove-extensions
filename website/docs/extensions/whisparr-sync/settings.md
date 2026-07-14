---
id: settings
title: Settings reference
---

Every setting on the Whisparr Sync page (Settings → Extensions → Whisparr Sync), in the order it
appears.

## Connection

| Setting | What it does | Default | Values |
|---------|--------------|---------|--------|
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
Connect, import, and reconciliation work on both. On **v2**, reconciliation matches scenes by **file
path and fuzzy title/year** — Whisparr v2 carries no StashDB id, so the authoritative StashDB-id match
does not apply (see [Reconciliation](./reconciliation#matching-on-whisparr-v2)).

## Library placement

Both dropdowns are disabled until a successful test, then populate from your instance.

| Setting | What it does | Default | Values |
|---------|--------------|---------|--------|
| Root folder | The Whisparr root folder this library maps to. | none | One of your instance's root folders (loaded live) |
| Quality profile | The quality profile new items are added with. | none | One of your instance's quality profiles (loaded live) |

## Webhook

| Setting | What it does | Default | Values |
|---------|--------------|---------|--------|
| Webhook URL | A read-only, ready-to-use URL with an embedded secret that Whisparr posts events to. | generated on first connect | Read-only |

**Copy webhook URL** copies it to your clipboard; **Register in Whisparr** best-effort adds the
connection to Whisparr for you. The embedded secret is a high-entropy token generated once and reused
so the URL is stable. Cove receives events at this URL and ingests the imported file in place; see the
[Connect guide](./guide) for the host-reachability note and how auto-import behaves.

Below the buttons a **status line** tells you honestly where the webhook stands: *registered, last
event {time} ago* once events have arrived, *registered, no events received yet* after you register
but before the first import, or an amber *not registered yet* prompt otherwise. A muted helper line is
always shown: the URL must be reachable **by Whisparr, not from your browser** — if Whisparr runs on
another host or in a container, use an address it can reach (for example
`http://host.docker.internal:5073`), not `localhost`.

:::note Webhook host is not overridable this release
There is no separate "webhook host" setting. The URL is derived from the address you open Cove at,
plus the host-reachability guidance above; a dedicated override is intentionally deferred. If the
derived host is not the one Whisparr can reach, edit the copied URL's host before pasting it into
Whisparr.
:::

### Root-overlap advisory

The extension also checks whether a Cove library root overlaps a Whisparr root — if they are the same
directory (or one contains the other), an import-in-place can look to Whisparr like a new file and be
re-grabbed. This is surfaced as a **best-effort advisory only**, never a block: cross-mount or
containerized setups legitimately see the same library at different paths, so treat it as a prompt to
check your layout rather than an error. (Auto-import never moves or deletes files inside a Whisparr
root — it only imports them in place.)

## Import activity

A read-only section listing every auto-import, below Reconciliation. It has no editable settings — it
is a record of what the webhook and the periodic reconcile did.

| Control | What it does |
|---------|--------------|
| Refresh activity | Loads the current audit log from the server (a pure read; nothing is changed). |
| Filter chips (All / Imported / Skipped / Flagged) | Narrows the list to one result; a zero-count chip is disabled. |
| Search | Case-insensitive match over the file path, kind, source, event type, and reason. |
| Column headers (When / Scene · file / Source / Result) | Click to sort; click again to reverse. |

Each row shows **When** the import happened (relative time), the **file**, the **Source** (Webhook or
Reconcile), the **Result** (Imported, Skipped — duplicate, or Flagged for manual scan), and a link to
the **Cove item** when one was created. The reconcile interval is fixed at 15 minutes and is not a
configurable setting in this release.
