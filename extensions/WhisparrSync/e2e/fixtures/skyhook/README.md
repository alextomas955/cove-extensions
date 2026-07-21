# Whisparr SkyHook metadata contract (captured live)

**Captured:** 2026-07-15, against the pinned E2E containers
`ghcr.io/hotio/whisparr:v3` (**3.3.4.794**, branch `eros`) and
`ghcr.io/hotio/whisparr:v2` (**2.2.0.108**, branch `v2`), using each container's own
`X-Api-Key` (read from `/config/config.xml` at capture time only — **never** written into this doc,
the fixtures, or any committed artifact).

This is the durable captured-contract note the offline replay tier is built on. It resolves the four
load-bearing unknowns about how Whisparr resolves metadata, answered in the sections below. Every route
and shape below was observed on the live instances through a recording HTTP proxy — no field is guessed. The scrubbed
responses live next to this file and are keyed by `index.json`; a later replay stub serves them by
request key so a lookup resolves with no network and no secret.

## The override mechanism

Whisparr does **not** call StashDB / ThePornDB directly. Every studio/site/scene lookup routes through
**Whisparr's own hosted metadata service** at `api.whisparr.com`. That base URL is a single overridable
config element:

- Config file: `/config/config.xml`, element **`<WhisparrMetadata>`**, value is a URL **template**
  containing the literal token `{route}` that Whisparr substitutes with the actual metadata route.
- Defaults (pinned builds):
  - v3 (eros): `<WhisparrMetadata>https://api.whisparr.com/v4/{route}</WhisparrMetadata>`
  - v2:        `<WhisparrMetadata>https://api.whisparr.com/v3/{route}</WhisparrMetadata>`
- **To override:** rewrite the element to point at a local server, keeping `{route}`, e.g.
  `http://host.docker.internal:9797/{route}` (host) or `http://skyhook-stub:9797/{route}` (a container
  alias on the shared Docker network). Whisparr reads `config.xml` **at startup only**, so a
  **restart is required** for the override to take effect.
- There is **no env var** for this in the pinned builds; the `config.xml` element is the mechanism for
  both versions. (`whisparr-container.mjs` wires this override; this note documents it.)

Because Whisparr reaches the container network host `host.docker.internal` (Docker Desktop) or a network
alias (CI), no DNS/hosts hack is needed — the config element is a clean app-level override.

## The wire contract

Whisparr issues these metadata routes (the `{route}` substitution). The proxy forwarded them to the real
`api.whisparr.com` upstream and recorded the responses; the replay stub serves them back by the same key.

### v3 (eros → `api.whisparr.com/v4`)

| Whisparr API call | Metadata route Whisparr issues | Recording | Returns |
|-------------------|--------------------------------|-----------|---------|
| `GET /api/v3/movie/lookup?term={text}` | `GET /movie/search?q={text}&year=` | `v3-movie-search-tushy.json` | array of scene rows |
| `GET /api/v3/movie/lookup?term=stash:{uuid}` | `GET /scene/{stashUuid}` | (scene-by-id; not committed — see note) | one scene object |
| `POST /api/v3/movie {tmdbId:{id}}` | `GET /movie/{tmdbId}` | `v3-movie-{tmdbId}.json` | one movie object |
| `POST /api/v3/studio {foreignId:{stashUuid}}` | `GET /site/{stashUuid}` | `v3-site-tushy-raw.json` | one studio (site) object |
| `GET /api/v3/studio?stashId={uuid}` | *(no metadata call — returns already-added studios only)* | — | added studios |

- v3 studio/site identity is a **StashDB UUID** carried in `ForeignIds.StashId`; scenes carry a numeric
  `ForeignIds.TmdbId` and (for StashDB-sourced scenes) a `StashId`. The Tushy catalog's scenes in the v4
  metadata are TMDB-sourced (`StashId: null`), so the committed v3 scene-search recording is keyed by the
  numeric TmdbId; the **studio** entry is the StashDB-keyed identity. A scene-by-StashId (`/scene/{uuid}`)
  recording is not committed because no SFW Tushy scene in the v4 metadata carries a StashId; add one only
  if a downstream test needs per-scene v3 add by StashDB id.
- Adding a movie (`POST /api/v3/movie {tmdbId}`) makes Whisparr re-fetch the full movie by id
  (`SkyHookProxy.GetMovieInfo(tmdbId)` → `GET /movie/{tmdbId}`), which is a **different** route from the
  search above — so a movie add cannot be served by the search recording alone. The webhook round-trip needs
  a real added movie to import into, so the three search rows are re-served by id as `v3-movie-{tmdbId}.json`
  (`/movie/573064`, `/movie/680283`, `/movie/1069805`). They carry no new content — each is the identical
  already-scrubbed search row served on its by-id route.

### v2 (`api.whisparr.com/v3`, Sonarr-shaped: site = series, scene = episode)

| Whisparr API call | Metadata route Whisparr issues | Recording | Returns |
|-------------------|--------------------------------|-----------|---------|
| `GET /api/v3/series/lookup?term={text}` | `GET /site/search?q={text}` | `v2-site-search-tushy.json` | array of site rows |
| `GET /api/v3/series/lookup?term=tpdb:{id}` | `GET /site/{tpdbId}` | `v2-site-3417.json` | one site object with embedded `Episodes[]` |

- v2 site identity is a **ThePornDB id** carried in Sonarr's `tvdbId` slot (`ForeignId` in the metadata
  response). Scenes are embedded as `Episodes[]` on the site, each with its own numeric `ForeignId` and a
  `ForeignGuid`.
- **v2 caches id lookups in its DB:** a `term=tpdb:{id}` lookup that resolved once returns from cache and
  does **not** re-hit the metadata URL, even across a container restart. A **fresh** container (CI) has no
  such cache, so the first `tpdb:{id}` lookup hits `GET /site/{tpdbId}` and the stub serves it. The offline
  proof below was therefore demonstrated on v2 via the (uncached) text-search route.

## Does v3 need a StashDB API key? (No)

The v3 container's `config.xml` has **no StashDB API key** configured, and both the scene lookup and the
studio add resolved and created a real row. Because `api.whisparr.com/v4` performs the StashDB call
server-side, Whisparr itself needs no StashDB key. With the metadata URL pointed at the offline stub there
is likewise **no key requirement** — the stub is the metadata source.

## Webhook custom-header capability

`GET /api/v3/notification/schema`, Webhook implementation fields:

| Version | Webhook fields | Verdict |
|---------|----------------|---------|
| v3 (eros) | `url`, `method`, `username`, `password`, **`headers`** | **Custom headers supported** — Cove's `X-Cove-Token` can be sent directly. |
| v2 | `url`, `method`, `username`, `password` | **Basic-Auth only, no custom-header field** — a **token-injecting shim** is required for `X-Cove-Token`. |

## Offline-resolution proof

With the metadata URL overridden to a local `node:http` stub serving **only** the committed scrubbed
recordings (the stub has no upstream/forwarding code, so a miss returns `[]` — never a call to
`api.whisparr.com`; that structural absence is the offline guarantee), and the container restarted to drop
cache:

- **v3** `GET /api/v3/movie/lookup?term=Tushy` → **3 monitorable rows**, titles `Tushy Raw 1/2/3` — the
  scrubbed values, proving the data came from the stub, not upstream (which returns real titles).
- **v3** `POST /api/v3/studio {foreignId: be4be46f-…}` → **HTTP 201**, created studio `Tushy Raw` with
  `foreignId` = the StashDB UUID, resolving via the stub's `GET /site/{uuid}`.
- **v2** `GET /api/v3/series/lookup?term=Tushy` → **6 monitorable site rows** including `Tushy` (tvdbId
  3417), resolving via the stub's `GET /site/search?q=tushy`.

All resolved with no outbound egress and no secret.

## Scrub rules (applied to every committed recording)

The recording proxy strips request auth from what it saves; the responses are metadata only and
carry no credential. Each raw response was then scrubbed before commit:

- **Images / any media URL** → emptied (`Images: []`, `remoteUrl`/`url`/`Homepage`/`Trailer`/`remotePoster`
  → `null`). No real media URL is committed.
- **Explicit text** → every `Overview` emptied (the one exception is the v2 site's SFW network blurb,
  set to a known-clean string); every scene/episode `Title`, `Slug`, and `ExternalId` replaced with a
  neutral placeholder (`Tushy scene N` / `Tushy Raw N`). No explicit text is committed.
- **Real-person identities** → `Credits`, `Directors`, `Genres`, `Tags`, `Aliases`, `SubStudios` emptied.
- **Kept:** the real metadata-source ids (`StashId` / `TmdbId` / `TpdbId` / `ForeignId` / `ForeignGuid`)
  and the real wire structure — that is what makes the fixture resolvable. Large scene/episode arrays are
  trimmed to 3 rows.

## Content-safety exception

The suite's rule is **synthetic media + metadata everywhere** (see `../README.md`), **except** the small
committed allowlist of real, SFW-sounding metadata-source **ids / names / descriptions** — never images,
never explicit text — captured here. The exception exists because Whisparr validates every add/monitor/
search id against its metadata source, so fully-synthetic ids resolve to zero rows and cannot prove real
sync behavior. The allowlist and its reviewer note live in `../allowlist/README.md`.

## Re-capture / drift

`api.whisparr.com` is a moving external service. Re-verify this contract against the live service (the
local/manual live-service check, `WHISPARR_E2E=1`) on a cadence and re-capture when it diverges. Re-capture procedure:
override `<WhisparrMetadata>` to a recording proxy, restart, drive the lookups above, scrub, and update the
recordings + `index.json`.
