# Real-identity allowlist

A **small, committed set of real metadata-source ids** — StashDB (Whisparr v3 / eros) and ThePornDB
(Whisparr v2) — that Whisparr's own add/monitor/search calls can resolve for real, so the E2E suite can
assert genuine Whisparr-side state (a real studio/series row, a `monitored` flag, a `cove-sync` tag, a
queue count) instead of the weak "status < 500 / HANDLED" checks it used before.

## Content-safety exception — read this first

The rest of this suite uses **synthetic media + metadata only** (see `../README.md`). This allowlist is
the one deliberate, narrowed exception:

> Synthetic media + metadata everywhere, **except** this small committed allowlist of real, SFW-sounding
> metadata-source **ids / names / descriptions** — **never images, never explicit text**.

It exists because Whisparr validates every id against its metadata source
(`api.whisparr.com`); a fully-synthetic id resolves to zero rows, so the outward call can only no-op to an
attribution-only result and there is nothing real to assert. Only the identity metadata is real:

- **Real:** the metadata-source id, a SFW-sounding name, and a minimal description needed for resolution.
- **Never real / never committed:** images (stay synthetic, seeded from `../thumbnails/`) and any
  explicit text — the recorded SkyHook responses in `../skyhook/` are scrubbed of both.

Entries are chosen so the real name reads innocuous at a glance (the studio/site brand word "Tushy" and
"Tushy Raw"); the ThePornDB site "Tushy" resolves on Whisparr v2 with `tvdbId 3417`, confirmed by live
capture (see `../skyhook/README.md`).

## Files

| File | Version | Identity key | Entries |
|------|---------|--------------|---------|
| `v3-stashdb.json` | v3 (eros) | StashDB UUID (`ForeignIds.StashId`) | `Tushy Raw` studio + a scene text-search + a single `Tushy Raw` scene (per-scene add by StashDB id) |
| `v2-tpdb.json` | v2 | ThePornDB id (Sonarr `tvdbId` slot) | `Tushy` site (`tvdbId 3417`) + a site text-search |

Each entry carries `lookupPath` = the exact SkyHook route Whisparr issues for that id, and `recording` =
the committed scrubbed response that serves it. Those `lookupPath`s are the keys in
`../skyhook/index.json`, so the offline replay stub resolves each id to at least one monitorable row with
no network and no secret. The full captured wire contract and override mechanism are in
`../skyhook/README.md`.

## Maintaining the allowlist

Keep it small and hand-reviewed. To add or change an entry:

1. Pick a real id whose **name/description reads SFW/innocuous** at a glance.
2. Capture its SkyHook lookup response live (see `../skyhook/README.md` re-capture procedure), scrub images
   and explicit text, and commit it under `../skyhook/` with an `index.json` key.
3. Add the entry here with its `stashId`/`tpdbId`, SFW name, minimal description, `lookupPath`, and
   `recording`.
4. **Never** commit a real image or any explicit text — a reviewer must be able to confirm SFW at a glance.
