# Changelog

User-facing changes, newest first.

## Unreleased

- **Whisparr v2 is supported.** Pick **v2** in the version selector, connect, and Cove imports what
  Whisparr v2 acquires (webhook and polling reconcile) and reconciles your library against it — the
  same connect, root-folder / quality-profile, webhook, and import experience as v3. One thing works
  differently by design: v2 matches scenes by **file path and fuzzy title/year**, not by StashDB id.
  Whisparr v2 is ThePornDB-native and carries no StashDB id on any scene, so the authoritative
  StashDB-id match that v3 leads with cannot apply to v2. This is a permanent property of v2's data
  model, not a missing feature — expect more v2 scenes in unmatched / needs-review than on v3.
- **Auto-import hardening.** Fixed three correctness bugs and three security issues found in review:
  a concurrent webhook and poll for the same import could create two Cove items (now claimed atomically,
  so an import is ingested exactly once); a first run while Whisparr was unreachable could later
  bulk-import your entire existing library (the reconcile now seeds only after a successful history read);
  and a dropped page during a large catch-up could permanently skip older imports (the checkpoint now
  advances only after a complete pass). Path handling is now case-sensitive to match Linux/Docker, so
  differently-cased files are no longer treated as the same file, and a symlink pointing outside a
  Whisparr root is rejected rather than followed.
- **The webhook prefers the `X-Cove-Token` header.** The header (which **Register in Whisparr**
  configures automatically) is the recommended way to authenticate the webhook, because it is not
  captured by proxy or access logs. The `?token=` URL still works for hand-pasted setups, but since a
  secret in a URL can be recorded by intermediaries, the extension now logs a one-time warning when the
  webhook authenticates that way — prefer **Register in Whisparr**.
- **Whisparr's Test button now succeeds.** Auto-register sends the webhook secret as an `X-Cove-Token`
  request header, so Whisparr's **Test** ping reaches Cove authenticated and returns success instead of
  being rejected. The copy-paste URL still carries the token too, so either way of adding the webhook
  works.
- **A heads-up if your libraries share a folder.** The page now warns when a Cove library root overlaps
  a Whisparr root — a setup that can cause Whisparr to re-grab a file Cove just imported. It is advisory
  only (containerized setups can legitimately see the same files at different paths), and auto-import
  never moves or deletes files inside a Whisparr root — it only imports them in place.
- **Honest webhook status and reachability help.** Under the webhook URL, a status line shows whether the
  webhook is registered and when the last event arrived, plus a reminder that the URL must be reachable
  by Whisparr (for example `http://host.docker.internal:5073`), not from your browser.
- **Import activity log on the settings page.** The Whisparr Sync page now has a read-only **Import
  activity** section listing every file that was auto-imported — with its result (imported,
  skipped-duplicate, or flagged for manual scan), source (webhook or reconcile), time, file, and Cove
  item — plus counts, a search box, and sortable columns. Click **Refresh activity** to load it. It is
  strictly a record: nothing here changes your library.
- **Polling reconcile catches imports the webhook missed.** In addition to the webhook, the extension
  now polls Whisparr's import history every 15 minutes and ingests anything the webhook didn't — so a
  dropped or missed webhook no longer means a missing import. The reconcile keeps a checkpoint so it
  never re-imports the whole history, and it shares the webhook's duplicate detection, so an import that
  arrives on both channels is still ingested exactly once. On first run it starts from "now", so your
  existing Whisparr history is not bulk-imported.
- **Automatic import when Whisparr finishes a download.** When Whisparr imports a finished grab it
  now calls Cove's webhook and Cove ingests the new file in place — no manual scan or import. The
  webhook is authenticated by the shared secret in your webhook URL (an unsigned or wrong-token
  request is rejected), Whisparr's **Test** button succeeds, and the same import delivered twice is
  never ingested twice. If the file can't be read, its type isn't recognised, or its path falls
  outside a known Whisparr root, Cove falls back to a scoped library scan and flags the attempt
  rather than failing silently. Every attempt — imported, skipped-duplicate, or flagged — is recorded
  in an audit log you can review in the Import activity section above.

- **Read-only reconciliation view.** The Whisparr Sync page now has a reconciliation section that
  compares what Whisparr tracks against your Cove library and shows every scene as **matched**,
  **unmatched**, or **needs review** — with counts, a search box, and sortable columns. Click
  **Refresh reconciliation** to run it. Nothing is changed in Cove or Whisparr; it is a comparison
  you can run any time.
- **Confirm or reject low-confidence matches.** Fuzzy title-and-year guesses land in a **needs-review**
  queue instead of being applied automatically. **Confirm** accepts a suggestion; **Reject** declines
  it so it isn't offered again. Both write only to the extension's own match store — never to your Cove
  library or to Whisparr. Note that in this release a decision is one-way: once you confirm or reject a
  scene it leaves the needs-review queue, and there is not yet an in-app way to undo it.
- **Identity matching on the StashDB id first.** Scenes match on their StashDB id (which survives
  renames and moves), then on an exact file path, then — only as a suggestion — on a fuzzy title +
  year. Anything unresolved stays unmatched rather than being guessed. When two library items share
  the same StashDB id or file path, the scene goes to **needs review** instead of being matched to an
  arbitrary one.

- **New extension: Whisparr Sync (`com.alextomas955.whisparrsync`), 0.1.0.** Adds a settings page
  under Settings → Extensions → Whisparr Sync where you enter your Whisparr instance URL and API
  key and click **Test connection**. On success the page shows the detected instance name and
  Whisparr version, read from a live call to Whisparr's `GET /api/v3/system/status`.
- **Your API key stays server-side.** The key is stored by Cove and used only to make the outbound
  call; it is never returned to the browser (the field stays empty on reload) and never written to
  logs.
- **Clear, distinct connection results.** Test connection now tells you exactly what went wrong
  instead of a single generic failure: a rejected API key, an unreachable instance (with the URL),
  a web page where the Whisparr API should be (a reverse-proxy landing page / 502), or a version
  this build can't manage yet.
- **Whisparr version selector with auto-detect.** Pick your Whisparr version (v3 (Eros) / v2); a
  successful test auto-selects the detected version. An instance whose major version this build
  cannot manage is refused with a clear advisory rather than silently connecting with the wrong
  behavior.
- **Pick a root folder and quality profile from lists.** After a successful test, the page loads
  your instance's root folders and quality profiles into dropdowns — no hand-typed paths or numeric
  ids. Your selections are saved and restored on reload.
- **Save your configuration.** A save bar persists your URL, version, root folder, and quality
  profile. Your API key is write-only — leaving the field blank on save keeps the stored key, and
  the key is never shown back to you.
- **Ready-to-use webhook URL.** The page generates a webhook URL with an embedded secret you can
  copy into Whisparr → Settings → Connections, or click **Register in Whisparr** to add it for you.
  If auto-register isn't accepted, the copy-paste URL always works. (The extension acting on webhook
  events arrives in a later phase.)
- **Configuration actions require the manage-extensions permission.** Loading the root-folder /
  quality-profile lists and generating the webhook URL now require `extensions.configure` (the same
  permission as saving), not read-only access — these steps reach your stored Whisparr credentials,
  so they are part of configuring the connection. A read-only user can still see whether the
  extension is configured, but cannot trigger an outbound call with the stored API key.
- **Requires Cove `0.9.0`** or newer (for the page-layout settings tab).
