---
id: changelog
title: Changelog
---

User-facing changes, newest first.

## Unreleased (0.1.0)

- **Connect to Whisparr.** A settings page under Settings → Extensions → Whisparr Sync where you
  enter your Whisparr instance URL and API key and click **Test connection**. On success it shows
  the detected instance name and Whisparr version.
- **Your API key stays server-side.** The key is stored by Cove and used only for the outbound call;
  it is never returned to the browser (the field stays empty on reload, showing a "Key is set" pill)
  and never written to logs.
- **Clear, distinct connection results.** A rejected API key, an unreachable instance, a proxy
  landing page where the API should be, or an unsupported version each get their own message rather
  than one generic failure.
- **Version selector with auto-detect.** Pick your Whisparr version (v3 (Eros) / v2); a successful
  test auto-selects the detected version, and a non-v3 instance is refused with a clear advisory.
- **Root folder and quality profile dropdowns.** After a successful test, pick both from lists read
  live from your instance — no hand-typed paths or ids. Selections are saved and restored on reload.
- **Ready-to-use webhook URL.** Copy the URL (secret embedded) into Whisparr → Settings →
  Connections, or click **Register in Whisparr** to add it for you; the copy-paste URL always works
  if auto-register isn't accepted.
- **Requires Cove `0.9.0`** or newer.
