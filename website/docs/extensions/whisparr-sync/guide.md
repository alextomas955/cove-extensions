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
