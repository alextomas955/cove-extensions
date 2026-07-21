---
id: changelog
title: Changelog
---

User-facing changes, newest first.

## v0.1.0 — Initial release

Whisparr Sync keeps a Cove library and a Whisparr instance in agreement, in both directions, with
near-zero setup. It works with both Whisparr **v3 ("Eros")** and **v2** — the version is detected
automatically and the extension keys on the id each carries (StashDB on v3, ThePornDB on v2).

- **Connect** with a guided setup — enter the URL and API key, test the connection, and pick a
  quality profile from auto-populated lists. The connection panel reports exactly what happened
  (connected, wrong key, unreachable, or not-Whisparr), names the version it detected, and stays
  honest when a saved connection is only temporarily unreachable. A guided advisory flags scenes
  Whisparr can't reconcile (no StashDB/ThePornDB id) with a one-click path to identify them.
- **Automatic import** — when Whisparr finishes a grab, Cove ingests the new file automatically via a
  webhook, with a periodic reconcile against Whisparr's history as a backstop so nothing is missed.
  Imported scenes are auto-identified by the StashDB/ThePornDB id Whisparr already carries — title,
  date, studio, performers, tags, and cover, creating the studio and performers when missing and
  generating covers/previews/phashes — so you never land a blank item. Enrichment runs once per scene
  and never overwrites your edits. Every import is recorded in an auditable log.
- **Reconciliation** — see what Whisparr has versus what Cove already holds (matched / unmatched /
  needs-review), matched purely on the StashDB (v3) or ThePornDB (v2) id the two already share. A file
  with no id yet shows as unmatched until you identify it; an id shared by two Cove videos waits in
  needs-review for you to confirm or reject.
- **Monitor from Cove** — turn Whisparr monitoring on for a studio or performer from its Cove page, or
  in bulk across the studios/performers list, with a quiet "Monitored · present / catalogue" status
  line. Choose the scope — **All scenes** or **New releases only**, mapped to Whisparr's own modes;
  "New releases only" leaves the existing back-catalogue visible but unarmed, so it can't silently turn
  into "grab everything." On Whisparr v2 a studio monitors as its site (series) by ThePornDB id, and
  "Search all monitored" runs the episode search. Adding never grabs.
- **Push, search & exclude** — from a scene's Whisparr panel or in bulk (across a studio/performer, or
  a multi-selection on the videos list): add a scene, search for it, grab quality upgrades, run an
  interactive release search, or exclude / un-exclude. Adding never downloads — only an explicit search
  does — so pushing your library to Whisparr can't start a download loop. Every action reports a
  plain-English reason if it fails.
- **Edit Whisparr's file settings from Cove** — because sync is in-place, the settings page surfaces
  Whisparr's own file-affecting toggles (rename movie files, replace illegal characters, auto-rename
  folders, delete empty folders) with a warning that they act on Cove's real files. Available on
  Whisparr v3; saving preserves the rest of Whisparr's config.
- **In-library status** — an opt-in library view shows each scene's Whisparr state (downloaded /
  monitored / not added / excluded); off by default, so nothing changes until you turn it on.

**Safety.** Every outward action is idempotent and tagged as Cove-originated, adding never triggers a
download (only an explicit search does), the inbound webhook is authenticated with a generated secret,
and the extension never moves or deletes files inside a Whisparr-managed folder. It warns if a Cove
library root overlaps a Whisparr root.
