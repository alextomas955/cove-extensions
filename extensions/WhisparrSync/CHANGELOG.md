# Changelog

User-facing changes, newest first.

## Unreleased

- **Imported scenes are now auto-identified.** When Whisparr imports a grab, Cove no longer lands a
  blank item you have to identify by hand. The extension stamps the scene's StashDB (v3) or ThePornDB
  (v2) id — the one Whisparr already carries — and runs an identify by it: title, date, studio,
  performers, tags, and cover, creating the studio and performers when they're missing, then generates
  covers/previews/phashes. If no matching metadata source is configured in Cove the id is still stamped
  (so the scene links and reconciles correctly) and a single **Identify** click completes it. Enrichment
  runs **once per scene** — a scene already identified by an earlier import, or by you in Cove, is left
  untouched, so a redelivery, an upgrade, or the reconcile pass never re-fetches or overwrites your edits.
- **More reliable status after a connection change.** Changing the Whisparr URL, API key, or version
  on the settings page now refreshes the status shown on studio, performer, scene, and library
  surfaces instead of leaving the previous connection's values in place until a full reload. The
  connection helper also names the version it actually detected (v3 or v2) rather than always
  reading "v3 (Eros)", and opening the studio/performer Whisparr menu no longer loses your place in
  the menu when a status refresh happens.
- **Reconciliation matching simplified to id-only.** Matching a Whisparr movie to a Cove video now
  relies solely on the StashDB id (v3) or ThePornDB id (v2) they already share — the earlier file-path
  and title/year similarity fallback checks have been removed. Cove's own Identify feature is what attaches
  a StashDB/TPDB id to a scene; a file with no id yet simply shows as **Unmatched** in reconciliation
  until it's identified, then it matches cleanly on the next refresh. An id shared by two Cove videos
  still lands in **Needs review** rather than auto-matching an arbitrary one.
- **Whisparr action failures now show an error.** The studio/performer Whisparr menu (Monitor, monitor
  scope, Add all missing, Reflect owned in Whisparr, Search all monitored) and a scene's Whisparr tab
  (Add, Monitor, Grab quality upgrades, per-release grab, Search, Exclude) used to fail silently on a
  network or Whisparr-side error; each now shows what went wrong inline. "Search all monitored" also
  gained the loading spinner its sibling actions already had.
- **Clearer Whisparr connection-trouble messaging.** A failed Whisparr action now shows a
  plain-English reason — a rejected API key, an unreachable Whisparr, a URL that isn't Whisparr, or an
  item with no linkable metadata id — instead of a raw HTTP status and JSON body. A studio/performer
  page's Whisparr control now also shows a visible warning line when Whisparr can't be reached at all,
  not only a hover tooltip. And the "not configured" wording shown across the monitor button, status
  line, library toolbar summary, and scene panel now honestly covers both cases it was collapsing
  together — never configured, or configured but currently unreachable — instead of always assuming
  the former.

## v0.1.0 — Initial release

Whisparr Sync keeps a Cove library and a Whisparr instance in agreement, in both directions, with
near-zero setup.

- **Connect** to Whisparr with a guided setup — enter the URL and API key, test the connection, and
  pick a root folder and quality profile from auto-populated lists. Works with both Whisparr **v3
  ("Eros")** and **v2**; the version is detected automatically and the extension keys on the id each
  version carries (StashDB on v3, ThePornDB on v2).
- **Automatic import** — when Whisparr finishes a grab, Cove ingests the new file automatically via a
  webhook, with a periodic reconcile against Whisparr's history as a backstop so nothing is missed.
  Every import is recorded in an auditable log.
- **Reconciliation** — see what Whisparr has versus what Cove already holds (matched / unmatched /
  needs-review), backed by confidence-ordered identity matching; low-confidence matches wait for you
  to confirm or reject.
- **Monitor from Cove** — turn Whisparr monitoring on for a studio or performer from its Cove page,
  with a quiet "Monitored in Whisparr · X of Y grabbed" status line. On Whisparr v2 a studio monitors
  as its site (series), resolved by ThePornDB id, and "Search all monitored" runs the episode search —
  loop-safe (adding never grabs). Performer monitoring and the per-scene push controls read
  "Currently available on Whisparr v3 (Eros)" on v2, where v2's series model has no counterpart.
- **Push, search & exclude** — from a scene's Whisparr panel or in bulk (across a studio/performer, or
  a multi-selection on the videos list): add a scene to Whisparr, search for it, grab quality
  upgrades, run an interactive release search, or exclude / un-exclude it. Adding never downloads —
  only an explicit search does — so pushing your library to Whisparr can't start a download loop.
- **In-library status** — an opt-in library view shows each scene's Whisparr state (downloaded /
  monitored / not added / excluded); off by default, so nothing changes until you turn it on.

**Safety.** Every outward action is idempotent and tagged as Cove-originated, the inbound webhook is
authenticated with a generated secret, and the extension never moves or deletes files inside a
Whisparr-managed folder. It warns if a Cove library root overlaps a Whisparr root.
