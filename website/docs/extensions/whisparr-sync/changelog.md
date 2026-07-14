---
id: changelog
title: Changelog
---

User-facing changes, newest first.

## Unreleased (0.1.0)

- **Whisparr v2 is supported.** Pick **v2** in the version selector, connect, and Cove imports what
  Whisparr v2 acquires and reconciles your library against it — the same connect, dropdowns, webhook,
  and import experience as v3. One thing differs by design: v2 matches scenes by **file path and
  fuzzy title/year**, not by StashDB id. Whisparr v2 carries no StashDB id on any scene, so the
  authoritative StashDB-id match that v3 leads with cannot apply — expect more v2 scenes in unmatched
  / needs-review than on v3.
- **Read-only reconciliation view.** Compare what Whisparr tracks against your Cove library —
  matched, unmatched, and needs-review — with counts, search, and sortable columns. Click **Refresh
  reconciliation** to run it; nothing is changed in Cove or Whisparr.
- **Confirm or reject low-confidence matches.** Fuzzy title-and-year guesses wait in a needs-review
  queue instead of being applied automatically. Confirm or reject each one; both write only to the
  extension's own match store and are reversible on the next refresh.
- **Identity matching on the StashDB id first.** Scenes match on their StashDB id, then an exact file
  path, then — only as a suggestion — a fuzzy title + year. Unresolved scenes stay unmatched.
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
  test auto-selects the detected version, and an instance whose version this build can't manage is
  refused with a clear advisory.
- **Root folder and quality profile dropdowns.** After a successful test, pick both from lists read
  live from your instance — no hand-typed paths or ids. Selections are saved and restored on reload.
- **Ready-to-use webhook URL.** Copy the URL (secret embedded) into Whisparr → Settings →
  Connections, or click **Register in Whisparr** to add it for you; the copy-paste URL always works
  if auto-register isn't accepted.
- **Requires Cove `0.9.0`** or newer.
