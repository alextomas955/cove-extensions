# Changelog

User-facing changes, newest first.

## Unreleased

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
  successful test auto-selects the detected version. Connecting to a non-v3 instance is refused with
  a clear advisory rather than silently connecting with the wrong behavior.
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
- **Requires Cove `0.9.0`** or newer (for the page-layout settings tab).
