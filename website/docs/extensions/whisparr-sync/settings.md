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
