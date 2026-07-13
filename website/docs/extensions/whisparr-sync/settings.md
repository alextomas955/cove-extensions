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
so the URL is stable. The endpoint that *receives* these events arrives in a later phase.
