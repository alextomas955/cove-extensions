---
id: guide
title: Connect to Whisparr
---

This guide connects Cove to your Whisparr v3 instance and finishes the setup: a verified connection,
a chosen root folder and quality profile, and a webhook Whisparr can call.

You need a running Whisparr **v3 (Eros)** instance and its API key (Whisparr → Settings → General →
API Key).

## Open the settings page

Go to **Settings → Extensions → Whisparr Sync**.

## Test the connection

1. In **Whisparr URL**, enter the address of your instance, for example `http://localhost:6969`.
2. In **API key**, paste your Whisparr API key.
3. Click **Test connection**.

On success the page shows the detected instance name and Whisparr version. If something is wrong,
the result tells you which:

- **Whisparr rejected the API key** — check the key in Whisparr → Settings → General and paste it
  again.
- **Couldn't reach Whisparr** — check the URL and that Whisparr is running.
- **Got a web page instead of the Whisparr API** — the URL points at a proxy landing page, not
  Whisparr.
- **This looks like Whisparr {version}, which this version can't manage yet** — connect a v3 (Eros)
  instance (v2 support comes later).

## Pick a root folder and quality profile

After a successful test, the **Root folder** and **Quality profile** dropdowns load from your
instance. Pick one of each. Before you test, they show "Test the connection to load this".

## Add the webhook

The **Webhook URL** field shows a ready-to-use URL with an embedded secret. Either:

- Click **Register in Whisparr** to have the extension add the connection for you, or
- Click **Copy webhook URL** and paste it into Whisparr → Settings → Connections → Webhook (On
  Import).

If auto-register isn't accepted by your build, the copy-paste URL always works.

## Save

Click **Save** in the bar at the bottom. Your URL, version, root folder, and quality profile are
stored. Your API key is kept server-side — the field stays empty on reload and shows a "Key is set"
pill; leaving it blank on a later save keeps the stored key.
