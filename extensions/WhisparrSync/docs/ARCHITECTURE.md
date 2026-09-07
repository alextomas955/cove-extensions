---
slug: architecture
---

# Architecture

Whisparr Sync connects Cove to a self-hosted [Whisparr](https://whisparr.com) instance (v3 (Eros) or
v2). This page traces how the extension turns a URL + API key into a verified connection and a
registered webhook — for a contributor reading the code for the first time.

The extension is in two halves:

- **Backend** — a .NET 10 C# class library (`src/WhisparrSync/`, built to `WhisparrSync.dll`) that
  implements Cove's `IExtension` contract (deriving `FullExtensionBase` from `Cove.Plugins` /
  `Cove.Sdk`). It owns the only outbound HTTP and all credentials.
- **Frontend** — a React 19 + TypeScript bundle (`src/WhisparrSync.Ui/`, built to `dist/index.mjs`)
  that renders the connection settings page inside Cove's own UI.

## The connect path at a glance

```text
  ┌───────────────┐  request()   ┌──────────────┐  select   ┌──────────────┐
  │  Settings UI  │ ───────────▶ │  Api handlers │ ────────▶ │ IWhisparrAdapter│
  │ (React panel) │              │ (minimal API) │           │  → V3Adapter    │
  └───────────────┘              └──────┬────────┘           └──────┬─────────┘
        ▲                               │ load/save                 │ transport
        │                               ▼                           ▼
        │                        ┌──────────────┐            ┌──────────────┐
        │                        │ OptionsStore  │            │ WhisparrClient│
        │                        │ IExtensionStore│           │ (typed HTTP)  │
        │                        └──────────────┘            └──────┬────────┘
        │                                                            │ /api/v3/*
        └──────────── hasApiKey / lists / webhook URL ───────────────┘
```

## Layers

- **`Client/WhisparrClient`** — transport only. Attaches the `X-Api-Key` header, applies a per-call
  timeout, and — before deserializing — guards the status code and `Content-Type`, classifying the
  outcome into a typed `WhisparrResult<T>` instead of throwing (bad key / unreachable / not-Whisparr
  / ok). Idempotent GETs retry a bounded number of times; the non-idempotent notification POST is
  single-shot. A read whose caller-supplied value would be interpolated into a query filter or a path
  segment refuses a blank one **before the request is built**, classified `notAsked` — a class of its own,
  because Whisparr was neither reached nor asked, so it is neither an outage nor an answer about the data.
  Blank is not a narrow filter: Whisparr reads an empty query value as no filter at all and returns the
  entire set, and an empty path segment collapses the URL onto the collection route. Both would read the
  whole library for a value this side never set.
- **`Adapters/IWhisparrAdapter` + `V3Adapter` / `V2Adapter`** — the version-adapter boundary.
  Each adapter owns its version's wire knowledge (endpoint paths, the content-enumeration shape, the
  webhook notification payload); the handlers never hold it. `AdapterSelector` picks the adapter from
  the detected major version — `3 → V3Adapter`, `2 → V2Adapter` — and **refuses** any other version —
  never a silent wrong-adapter call. The v2 mapping is detailed in
  [Whisparr v2 adapter](#whisparr-v2-adapter) below.
- **`Options/WhisparrOptions` + `OptionsStore`** — a single JSON blob persisted over Cove's
  `IExtensionStore` under the `"options"` key (URL, API key, selected version, detected version + its
  tick stamp, the per-version saved connections, the webhook secret and host, the add defaults — tags,
  monitor-new, default monitor scope, allow-upgrades — and the StashDB / ThePornDB endpoints plus the
  path-translation table). A corrupt or absent blob loads as safe defaults. Every write is a gated
  read-modify-write serialized per store instance — not per key, because both extensions persist under
  `"options"` — so a settings save racing a connection test or a webhook registration cannot revert the
  saved connection. Cove's own `PUT /api/extensions/{id}/data/{key}` route reaches the blob without passing
  through that gate; nothing in this extension's UI uses it.
- **`Ingest/WebhookUrlBuilder`** — mints the webhook secret and builds the copy-paste URL.
- **`Activity/`** — the read-only worklist slice. Location: `Activity/ActivityEndpoints.cs` plus
  `ActivityProjector`. It holds the three activity reads — history, queue and wanted — each projected per
  request from a Whisparr page. It depends downward on the connection role for the fetch,
  on `Contracts/` for the row shape, and on the library port for the ownership leg; nothing depends on
  it but the activity sub-page. It mutates nothing on either side, and an outage answers 502 with a
  discriminator rather than an empty list, because an empty worklist means *nothing is stuck* and a
  failed read must not be able to say that.
- **`Discovery/`** — the missing-scene slice. Location: `Discovery/DiscoveryEndpoints.cs` (the two
  reads), `DiscoveryActionEndpoints.cs` (the two mutations), `DiscoveryService` (the pure diff),
  `DiscoveryQueryGuard` (normalises the untrusted query), and the two provider sources behind the
  `IDiscoverySource` seam. It answers *what does this entity's catalogue hold that Cove does not?* by
  diffing the configured metadata server's catalogue against the entity's owned Cove scenes. It depends
  on the metadata-source seam, the library port and `Caching/`; the Missing tabs depend on it. Its
  mutation half funnels the shipped per-scene push rather than opening a second spine, and it
  re-derives the missing set server-side before acting, so an action can only ever target a scene Cove
  does not own — see [The discovery-source contract](#the-discovery-source-contract).
- **`WhisparrSync.Api`** — the minimal-API endpoints and the settings-tab manifest.

## Endpoints

All are mounted under `/api/extensions/com.alextomas955.whisparrsync/`. Each route **declares its access
tier at registration** and Cove enforces it in host middleware, before the extension's request scope
exists — so a denied call never reaches extension code, and every denial is audited by the host. No
handler re-checks the principal; there is exactly one gate per route, and it is not inside the handler.

The side-effect-free read projections declare `extensions.read`; every route that reaches the stored
credentials or makes an outbound call declares `extensions.configure` (the host confirms
`extensions.configure` *implies* `extensions.read`, so a route that must exclude read-only users has to
require `configure`). The one deliberate exception is the inbound `/webhook` route, which carries no Cove
principal at all — a shared-secret token is its auth, so it declares anonymous access explicitly rather
than being left undeclared. An undeclared extension route is anonymous for backward compatibility, which
is why the tier is asserted by mechanism: `RouteAccessTierTests` reads the routes the extension actually
registered and fails when any one of them lacks a declaration, when the set drifts from the expected
table, or when a route declares a tier its capability does not match.

An unauthenticated caller receives `401 UNAUTHORIZED` from the host; a caller authenticated but lacking
the tier receives `403 FORBIDDEN` with the missing permissions named.

The table below is one row per **method + path**, not one per path: `/options` and `/file-settings` each
carry two methods at different permission tiers, and collapsing either would hide that difference. The
**Available on** column is the other reason the table is shaped this way — a good number of routes answer
on one generation only, and a table without that column lets a v3-only route read as shipped on both.

| Route | Method | Permission | Available on | Purpose |
| ------- | -------- | ----------- | ------------- | --------- |
| `/activity/history` | GET | configure | v3 + v2 | Recently grabbed, imported and failed scenes, projected from Whisparr's own paged history |
| `/activity/queue` | GET | configure | v3 + v2 | The live Whisparr download queue |
| `/activity/wanted` | GET | configure | v3 + v2 | The live wanted set — monitored with no file — derived from Whisparr rather than stored |
| `/bulk-add-missing` | POST | configure | v3 only | Register every Cove scene not yet in Whisparr for the entity, as a local diff (no StashDB GraphQL), all non-grabbing; kind + Cove entity id |
| `/bulk-search-monitored` | POST | configure | v3 + v2 (studio) · v3 only (performer) | Trigger a Whisparr search across the entity's monitored scenes; kind + the entity's remote ids |
| `/discovery/action` | POST | configure | v3 only | One op over a single non-owned catalogue scene. The server re-derives the entity's missing set and rejects a source id absent from it, so the catalogue-minus-owned diff is the loop-safety boundary |
| `/discovery/action-all` | POST | configure | v3 only | The same op over the whole re-derived missing set, or over a validated selection subset, as a Job-Drawer job |
| `/discovery/count` | GET | configure | v3 + v2 | The missing count the host tab badge reads — the same diff reduced to a number |
| `/discovery/entity` | POST | configure | v3 + v2 | The missing-scene list for a studio or performer: the metadata catalogue minus what Cove owns. Body carries the Cove entity id and kind only; the server resolves the remote id |
| `/entities-batch` | POST | configure | v3 + v2, per op | One batch op over a studios/performers selection, each op capability-gated: monitor and reflect-owned answer on both, performer monitor and add-missing on v3 only |
| `/entity-library-summary` | GET | configure | v3 + v2 (studio) · v3 only (performer) | The library-level monitored and present counts the entity list row paints |
| `/entity-status-batch` | POST | configure | v3 + v2 (studio) · v3 only (performer) | The per-page entity states for a studios or performers grid — two Whisparr fetches, not one per card |
| `/file-settings` | GET | configure | v3 only | The four file-affecting Whisparr toggles, read |
| `/file-settings` | POST | configure | v3 only | The same four toggles, written read-modify-write, honouring only the whitelisted booleans and never a client-supplied config object |
| `/folder-overlap` | GET | configure | v3 + v2 | A best-effort advisory over two findings: root-vs-root containment between Whisparr and Cove (**both generations**), and whether a Whisparr root's trailing segment doubles the Scene Folder Format's leading literal (v3/Eros only — on v2 that kind reports as `notApplicable`). A leg that cannot compare answers `checked:false` plus the reason, never an empty finding set |
| `/import-log` | GET | read | v3 + v2 | The auto-import audit log: every attempt with its result, source, time, path, and Cove item + counts. Also carries `pipelineHealth` — the per-dependency health array (acquisition / metadata / import) beside the existing `syncHealth` and `lastEventTicks`, so the settings page reads both from one request and no route exists for it separately |
| `/monitor` | POST | configure | v3 + v2 (studio) · v3 only (performer) | Toggle a studio or performer's monitored state in Whisparr (add-then-flip); body carries the entity's remote ids only |
| `/monitor-status` | POST | configure | v3 + v2 (studio) · v3 only (performer) | The quiet-status projection for a studio or performer: added / monitored / scenesPresent / scenesTotal (Whisparr's own present-in-library and catalog counts) |
| `/options` | GET | read | v3 + v2 | The persisted options as a redaction-safe view (no key, only `hasApiKey`) |
| `/options` | POST | configure | v3 + v2 | Persist URL / version (write-only key). Neither a root folder nor a quality profile is stored — an add resolves both from the instance's own lists |
| `/reflect-owned` | POST | configure | v3 + v2 | Attach files Cove already owns to their matching fileless Whisparr rows, without moving or deleting Cove's file and without grabbing: v3 adopts in place (re-points the movie path and rescans, falling back to a copy import for a flat layout), v2 registers its episode in place |
| `/register-webhook` | POST | configure | v3 + v2 | Idempotent update-or-create of the Cove webhook connection (find → PUT-update else POST-create; a unique-name 400 / 409 is success), persisting the resolved host |
| `/scene-add` | POST | configure | v3 only | Register one scene in Whisparr as non-grabbing (`searchForMovie:false`, origin-tagged); body carries the Cove id only |
| `/scene-detail` | POST | configure | v3 + v2 | Whisparr-owned facts for one scene — state, monitored, quality, cutoff — resolved server-side from the Cove id. On v2 the exclusion leg defers and the actions flag reports unsupported. A scene with no provider id is a handled 200 (`NO_STASHDB_IDENTITY`) with zero outbound calls |
| `/scene-exclusion` | POST | configure | v3 only | Add or remove a Whisparr import-list exclusion for one scene. Never searches |
| `/scene-grab-release` | POST | configure | v3 only | Grab one specific indexer release for a scene — the sole interactive grab |
| `/scene-monitor` | POST | configure | v3 only | Set one scene's monitored state (add-then-flip when not-added); Cove id + target flag |
| `/scene-releases-list` | POST | configure | v3 only | The indexer release list for one scene, fetched only on an explicit expand and never in the summary or detail path. A pure read |
| `/scene-search` | POST | configure | v3 + v2 | Trigger a Whisparr search for one already-added scene — `MoviesSearch` on v3, `EpisodeSearch` on v2. The only per-scene route that may grab |
| `/scene-search-upgrades` | POST | configure | v3 only | Search a scene's Whisparr movie for a quality upgrade. With the upgrade setting off this issues no command on either generation |
| `/scene-status-batch` | POST | configure | v3 only | The per-page scene states for a visible videos grid — one DB read and one Whisparr fetch, not one call per card. A v2 connection has no scene-level id, so it answers `VERSION_UNSUPPORTED` |
| `/scene-status-summary` | GET | configure | v3 only | The library-wide four-state counts the videos toolbar paints. A v2 connection carries no scene-level id, so no row of its synthesized set keys and the partition would read `notAdded` for every scene: it answers `VERSION_UNSUPPORTED` before contacting Whisparr instead |
| `/status` | GET | read | v3 + v2 | Whether the extension is configured (no key), plus the pre-click configuration fact every control consults |
| `/sync-library` | POST | configure | v3 + v2, per leg | Enqueue the exclusive `whisparr-sync-library` job that registers the whole library as owned (+ optional monitor). Each leg is capability-gated, so a v2 run performs the legs v2 can honour and skips the rest |
| `/sync-preview` | GET | configure | v3 + v2 | Whole-library counts of what will register versus be skipped (no metadata id), read under `CovePrincipal.System()` |
| `/test-connection` | POST | configure | v3 + v2 | Classify the connection; return version + instance name on success |
| `/videos-batch` | POST | configure | v3 only | One batch op — add / monitor / unmonitor / search / searchUpgrades / exclude — over a capped selection of Cove video ids from the videos-list multi-selection; stored creds only, each id resolved to a scene server-side, run as a Job-Drawer job |
| `/webhook` | POST | anonymous (token) | v3 + v2 | Inbound Whisparr On-Import receiver — ingests the imported file |
| `/webhook-url` | GET | configure | v3 + v2 | The webhook URL + `registered` status, read from Whisparr's own "Cove Whisparr Sync" connection (`GET /api/v3/notification`, find-by-name); a derived default + `registered:false` when the connection is absent or Whisparr is unreachable |

A route that answers on one generation only refuses with a `VERSION_UNSUPPORTED` 400 **before** any
outbound call, so a deferred action never leaves a stray origin tag behind. The refusal is a capability
answer rather than a version branch: an adapter simply does not implement the role a route needs, and the
handler reads that absence.

The four routes that act on one already-added scene — `/scene-search`, `/scene-search-upgrades`,
`/scene-grab-release` and `/scene-releases-list` — resolve that scene's Whisparr row by asking Whisparr
for the id, rather than by reading the whole movie set and indexing it. The per-id filter selects on a
row's own `foreignId`, so **one row shape is invisible to it**: a row whose StashDB id matches the scene
but whose `foreignId` is something else — a movie added from another source that merely carries the
StashDB id. Such a scene resolves as not-added, and each of the four then takes its declining branch
(`searched:false`, `NOT_ADDED`, an empty release list). Nothing acts on the wrong movie: every row the
filter does answer is re-checked against the same keying rule the whole-set index uses, so the resolve
can only ever be narrower than the set-wide one, never different.

`/monitor` and `/monitor-status` are `configure`-gated for the same reason as the list routes:
they reach the stored credentials to call Whisparr. Both are POST because each carries a `RemoteIds`
body (a complex payload that cannot ride a GET under minimal-API binding), and neither ever accepts a
url or key from the caller — the handler pairs the stored key with the stored host only.

## The reconciliation match model

Reconciliation answers one question for every Whisparr scene: *which Cove item, if any, is the same
thing?* It never mutates Cove or Whisparr: the matching engine reads the Cove library `AsNoTracking` and
correlates it against the Whisparr movie list. The correlation is derived **per read** by the surfaces
that need it (the per-scene status views) — no production path
writes a match blob, so no surface can present a stale verdict as a current one.

`IdentityMatcher` matches on the **one remote id both systems already key on** — the StashDB UUID for
a v3 scene, the ThePornDB id for a v2 scene. Cove owns content identification: its own Identify
pipeline is what attaches a StashDB/TPDB id to a scene. Whisparr's job is acquisition/inventory
tracking — it can only add/search a movie it already carries a remote id for, not identify content.
So WhisparrSync correlates the two systems by the id they already share rather than inventing a
weaker identification system of its own.

An exact, case-insensitive id match applies. A movie-typed id is never compared against a Cove scene
UUID. If *two* Cove videos share the same remote id (Cove does not enforce cross-video uniqueness),
**every** one of them is considered — a surface that picked one arbitrarily could report a scene Cove
demonstrably owns as missing. Anything with no id match at all is **unmatched** — the safe default,
never a silent guess. An unidentified file simply stays unmatched until Cove's own scraper attaches an
id; the very next read then picks it up cleanly via the id match.

Whisparr exposes no comparable file hash, and Cove carries no per-video studio field, so neither a
content-hash comparison nor a title/year similarity check could disambiguate reliably (a same-titled,
same-year scene from a different studio would score a perfect similarity score with no way to tell
them apart). That is why the design stays id-only rather than adding either back.

**Nothing is persisted.** There is no match store: `IdentityMatcher`'s two predicates are the whole
engine, and each surface derives what it needs per read. A durable Whisparr-movie-id → Cove-id map is
the wrong shape here — it would carry one entry per movie, which becomes one per library scene as soon
as a library sync runs, and the library is unbounded (see *Bounded by design* below). Where a bounded
movie → Cove handle is genuinely needed, the shape is a remote-id lookup against Cove at event time.

## Bounded by design

A Cove library here reaches **millions** of files, so library size is treated as unbounded input in
every direction: storage, memory, wire payload, and browser state.

- **Nothing persisted grows with the library.** All five `IExtensionStore` keys are bounded by
  construction: `options`, `reconcile-checkpoint` and `health` are single records (`health` keyed by
  dependency, so a repeat overwrites), `importlog` is a pre-reduced status record plus a 16-entry sample
  ring, and `eventledger` caps its cross-channel keys at 512 while
  pruning `hist:` keys at the checkpoint. This matters because Cove's bulk
  `GET /api/extensions/{id}/data` serialises every value an extension owns — one oversized value 500s
  the whole settings page, survives reinstall, and needs SQL to remove.
- **No library-wide read holds the library.** `ICoveLibraryPort.StreamAllVideosAsync` is the seam every
  library-wide aggregate folds through — keyset-paged on the ascending id, so peak memory is one page.
  It is the ONLY read that answers the whole library at all — the materializing sibling it replaced is gone,
  so no caller can hold the library by mistake. Entity-bounded reads have their own seams.
- **A question about a page is answered for that page.** The acquisition worklist keys the Whisparr side
  (which the connected instance bounds) and streams the Cove side past it; the parent-studio and tag
  discovery diffs resolve owned ids for the ids on the current catalogue page rather than reading the
  owned library.
- **No response row count grows with the library.** Batch status reads reject over 1000 ids, activity
  and discovery are server-paged with a clamped page size, and the toolbar summary returns counts.
- **The Whisparr-side ceiling differs per generation, and the newer one's is a constant factor rather
  than an absence.** See *What the Whisparr read costs, per generation* below.
- **The library sync's scene fan-out is capped, and its entity fan-out is not.** A sync used to plan one
  background job unit per library scene. The scene half is now at most **64** units, each a half-open
  Cove-video-id range covering at least **500** scenes — one keyset page, so the smallest slice is a
  single round trip, and 64 steps move the progress bar in increments finer than it can be read. The
  **count** is capped rather than the slice size fixed; a fixed size would leave the unit count growing
  with the library, divided by a constant but still growing. The entity half is **not** capped: one unit
  per studio and per performer stays, because each is a distinct outbound operation with its own outcome.
  That term is O(entities), not O(library scenes) — at a million scenes with ~5,000 studios and ~20,000
  performers it is ~25,000 units rather than a million. This is a much lower ceiling, not the absence of
  one.
- **Browser state is capped too.** The card-status coalescer keeps an LRU-bounded 2000 entries and the
  stores' per-resource maps prune idle entries, so scrolling cannot grow a tab's memory without limit.

A row cap is deliberately *not* used as a remedy anywhere: it converts a hard failure into a silently
truncated answer. Where a pass must visit the whole library (a count, a reconcile), it may be O(library)
in *time*, but its output stays O(1) in size. Choosing the slice boundaries is exactly that shape: one
pass over the video-id column, retaining at most 63 integers.

### What the Whisparr read costs, per generation

The two generations do not have the same ceiling, and stating one number for both would be false for
whichever it did not describe.

**Whisparr v2 (Sonarr-shaped) asks O(library) in REQUESTS, and that part no reshaping on this side
changes.** That generation has
no `/movie` entity at all: its scene set is synthesized by walking `series` → `episode` → `episodefile`,
at **1 + 2N** requests for N sites, and there is no paged movie endpoint to page and no narrow movie
projection to bind — so every one of those requests still happens, in that order.
`ListMoviesAsync` stays for the three callers that reach it and genuinely need the materialized
array — the owned-reflection branch for entity kinds this generation cannot enumerate, the memoised list
read behind the per-card batch classify and the discovery status index, and the videos batch planner's
search ops — and both it and the per-site index fold are folds over the *same* walk, so their request
sequences cannot diverge. A fourth whole-set movie read, the attributed-id collector behind the grab-capable
search surfaces, reads `WhisparrClient.ListMoviesAsync` directly rather than through this role.

**The videos-toolbar summary does not run on this generation at all, so it has no memory figure here.**
Under the keying this generation ships, a synthesized scene carries no stash id and an item type that is not
the scene literal, so it indexes under no key and the status index it can build is **empty for a library of
any size**. The four states partition a library, so the row that empty index produced read `notAdded` for
every scene — a confident answer that was uniformly wrong, and one whose own definition ("Whisparr does not
have the scene") is the single conclusion it could not support. `IWhisparrStatusIndexSource` is therefore
**v3-only**, `SceneStatusSummaryAsync` narrows to it, and a v2 connection is answered `VERSION_UNSUPPORTED`
before the transport. Keying v2 rows on the ThePornDB id they DO carry would let v2 answer and would move the
counts a user sees, so it is a capability decision with its own verification rather than a cleanup — a TPDB id
compared against a StashDB id is the cross-match the keying rule exists to prevent.

The row the ledger below still carries for this generation is therefore about the **per-site index fold**, not
about a shipped summary. That fold survives, unreached by any shipped path here and labelled as such, because
what it measures — a per-site walk hands each site's scenes over as it reads them and keeps one site at a time
rather than the library — is a property of the SHARED walk that `ListMoviesAsync`'s three callers do reach, and
because it is the shape an index read would take if this generation's rows ever keyed. Two further things that
row does **not** say: it is not a per-index-entry cost (the index is empty), and it is not a wire figure — what
a v2 scene row costs on the wire is not claimed here at all.

**Whisparr v3 (Eros) serves an unpaged `/movie` too, but its rows can be narrowed.** The toolbar summary
now reads it with headers-only completion and folds each row into its status index as the row arrives,
binding **five** members instead of thirty-six and asking the instance to leave out the locally-cached
cover paths. The movie-row array is never materialized. Measured against 3.3.4.794: **≈1 528 wire bytes
per row** with the cover parameter against ≈1 578 without, agreeing to within 0.42% across a tenfold
corpus-size change; and **≈650–790 retained bytes per index entry** against **≈3 230–3 390** for the
materialized rows.

**That is a constant factor of about five, not an order, and the difference matters.** The summary's peak
is still **O(Whisparr movie set)** — one index entry per movie. The bounded Cove-side read that would be
needed to remove the term does exist (`ICoveLibraryPort.LoadOwnedRemoteIdsAsync`), but it answers **id-set
membership** while the summary counts **Cove videos**, and the two diverge whenever the id↔video relation
is not one-to-one — one video carrying two ids, one id carried by two videos, both of which this library
already sees. Making page-wise counts exact would need a cross-page distinct-video set that is itself
O(matched videos), so the join relocates the term while changing the number the toolbar shows. A much
lower ceiling, not the absence of one.

**Four whole-set movie reads remain on the newer generation**, each with the narrowing that would move it
and the reason it has not:

| Site | What would narrow it | Why it has not |
| --- | --- | --- |
| The memoised list read (per-card batch classify + discovery status index) | the same narrow projection the summary folds | both consumers read members the projection does not carry — the nested quality name, the cover urls |
| The videos batch planner's search / search-upgrades ops | a by-id read per selected scene | it resolves a movie id per selected scene, so the narrow form is `⌈k/1000⌉` by-id requests rather than a projection |
| The attributed-id collector behind the grab-capable search surfaces | the per-entity catalogue routes, which do yield the file flag | its consumers are the only grab-capable surfaces, so repointing it is a change to their loop-safety with its own verification |
| The owned-reflection branch for entity kinds the older generation cannot enumerate | nothing | that branch exists precisely because there is no per-entity catalogue to ask for |

The short-TTL movie memo is what amortises the first of those across its two consumers, which is why it
survives: removing it before both are narrowed turns one read per window into one read per page.

#### The ledger

The claim this table supports, with its qualifications inside it rather than under it: **no response, store
value or in-memory collection on the Whisparr read path grows with the Cove library — except that the
toolbar summary is a constant-factor win of about five and not an O(1) one (both its columns still grow with
the Whisparr movie set); the older generation has no narrow movie endpoint at all, so its REQUEST ceiling is
stated separately and stays 1 + 2N — and it answers no toolbar summary at all, so the memory bound recorded
for its per-site fold describes that fold rather than a shipped read;
the library sync's SCENE fan-out is capped while its ENTITY fan-out still
scales with entity count; and the bulk add path re-resolves its context once per scene, which is a fixed
number of extra requests PER SCENE and so is O(library) in requests across a full sync.**

Every figure names what produced it. A column with neither a measurement nor a citation is written `—` with
its reason, never a number.

| Path | Generation | Response bytes / row | Retained bytes / entry | Requests | Measured by |
| --- | --- | --- | --- | --- | --- |
| Videos-toolbar summary | v3 | **1 528** at 120 rows (1 522 at 12; 0.38% apart) | **≈650–790** against ≈3 230–3 390 materialized | **2**, at any corpus size | bytes: `bound-08-ledger.json` · retained: `SummaryReadCostTests` · requests: `SummaryIndexEquivalenceTests` |
| Site-walk index fold — NOT the toolbar summary, which is v3-only | v2 | — no movie entity to read | totals held, not a per-entry figure: **≈0.3–3.3 MB whatever the site count** against ≈7.1–8.1 MB at 20 sites rising to ≈53.4–56.5 MB at 200 (300 scenes per site, fixed) | **1 + 2N** for N sites | retained: `V2SummaryReadCostTests` · requests: `V2SummaryReadCostTests` and `V2StatusIndexWalkTests` |
| Per-entity catalogue | v3 | **1 578** at 120 rows (1 572 at 12) | — no in-process harness samples this path mid-read | **1 + ⌈k/1000⌉** for k sibling ids | bytes: `bound-08-ledger.json` · requests: `BoundedReadLedgerTests`, at and one past a chunk boundary |
| Per-entity catalogue | v2 | — route absent | — route absent | — the role is not implemented | capability is presence of the role interface |
| Single-scene push resolve | v3 | — at most one row | — at most one row | **0** whole-set, **1** narrow per scene | `BoundedReadLedgerTests`, at 3 and 30 scenes |
| Bulk mark-wanted add | v3 | — writes, no row set | — one scene held at a time | **2 per scene** + 1 once, on top of each add | `BoundedReadLedgerTests`: 7 reads at 3 scenes, 61 at 30, slope 2.00 |
| Library-sync fan-out | both | — issues no read of its own | **≈556** per parked unit | **66** units at 1e6 and at 1e7 scenes | `SyncFanOutCostTests` |

Read the wire figures for their SHAPE, not their magnitude. A corpus of tens or hundreds of rows can show
that a per-row constant agrees across a tenfold size change — which is what proves the figure carries no
fixed term and that the larger read was not truncated — and it says nothing about absolute cost at a real
library's size.

Two of those rows deserve their own sentence.

**The per-entity catalogue is narrow in its ROW SET, not in its payload.** Its hydration answers the same
full 36-member rows the whole-set read does, at 1 578 bytes each; what it saves is every row the entity does
not own. The sibling read that precedes it answers bare integer ids at 5–6 bytes each — a figure recorded
rather than asserted, because a bare id's width is the id's own decimal length and grows with the id space
rather than being a constant.

**The bulk add path is the next ceiling, and it is a request term rather than a memory one.** Marking k
scenes wanted re-derives the root folder and the tag list for every one of them: 7 context reads at 3 scenes
and 61 at 30, a slope of exactly 2.00 per scene. Nothing in the read narrowing touched it. What would close
it is memoising the root, tag and profile resolve across a batch — the sibling batch add already does
exactly that, and its context-read count does not move with the scene count at all, which is what makes the
per-scene slope a measurement of the path rather than of the harness.

### What a fan-out unit costs, and why the cap exists

`IJobService.RunBatchAsync` materialises its unit sequence and starts every unit's async state machine at
once; they then park on the single in-flight slot and stay live until the last one finishes. Measured
with the slot genuinely held — the measurement only means anything in that state, and a first attempt
that let each unit finish before the next was created reported a small number that looked like an answer
— the retained cost is **≈556 bytes per parked unit** (10,000 units retaining 8.3 MB, 100,000 retaining
58.3 MB, on .NET 10 / arm64). At one unit per scene a million-scene library would hold roughly half a
gigabyte for the whole run; at 64 slices the scene half of that is gone.

The harder constraint is not the memory. The host recomputes a job's aggregate progress under its job
lock on every unit registration, every per-unit report and every unit completion, and each recomputation
makes four full passes over that job's whole unit dictionary. That cost is quadratic in the unit count
and it is held under a lock every other job on the instance shares. The extension's only levers on it are
emitting fewer units and reporting less often, which is why a slice reports at most once per streamed
page rather than once per scene.

### The accepted trade: the completion line counts slices

The Job Drawer's end-of-run line for a library sync now reads *"12 of 12 units succeeded"* — **units are
slices of scenes, not scenes**. The host composes that line from its own unit counts and copies it into
the finished job's subtask, and there is no extension-side seam to override it.

What it was traded for, all three observed on a 6000-scene library rather than argued:

- a run that retained hundreds of megabytes of parked fan-out for its whole duration;
- a lock-held recomputation whose cost grew with the library;
- a progress bar that read **99.2%** one second into a 36-second run — the denominator was the units
  registered *so far*, and at one unit in flight numerator and denominator advanced together — and a
  time-remaining estimate that read **0 seconds** for 32 of those 36 seconds, because "remaining" is the
  same total minus the same completed count. After the change the denominator is correct and unchanging
  from the first report, and the bar climbs a twelfth at a time.

The real per-scene tally has not been lost, only moved. It is in the run's log line, which is fed the
accumulated per-scene counts rather than the batch's unit counts; in each drawer row's label, which names
the slice by its scene range; and in the live subtask, which reads *"Scene 1500 of 6000"* as the run
proceeds.

One residual, recorded because it is real: with twelve coarse units instead of thousands of fine ones,
the completion events the host's time-remaining estimate is computed from are ~2.4 seconds apart, and
between them the estimate climbs before dropping again. The estimate is far more accurate than it was —
18 of 20 post-warm-up samples within a factor of two of the truth, against 0 of 33 before — but it
visibly saw-tooths. The estimator is the host's, and the only extension-side alternative is to give up
the correct denominator, which is worse.

### Asking about one entity instead of about the library

Registering an entity's missing scenes and importing the files Cove already owns both need the set of
scenes Whisparr tracks for **that** studio or performer. Both used to get it by reading Whisparr's entire
movie set and filtering client-side — once per entity. A library sync plans one unit per studio and per
performer, so a library with ~5,000 studios and ~20,000 performers re-read and re-parsed the whole movie
set ~25,000 times in one run.

Both now ask Whisparr for that one entity. The ceiling differs **per generation**, and the weaker one is
written as weaker rather than implied to be equivalent:

- **Whisparr v3 (Eros) — bounded by the entity.** `GET /movie/listbystudioforeignid` (or
  `…listbyperformerforeignid`) answers the entity's movie **ids**, which are then hydrated in chunks
  through `POST /movie/bulk`. One entity costs `1 + ⌈k/1000⌉` requests for `k` scenes, plus a single
  existence read when the id list comes back empty. The chunk grain of **1,000 ids** bounds one call's
  payload — measured at ≈1,387 bytes per row against a live 3.3.4.794 instance, so ≈1.7 MB per call at
  the grain. It does **not** bound the entity's catalogue, which is bounded by the entity itself; the
  loop runs until the id list is exhausted, never to a chunk count.
- **Whisparr v2 — bounded by the connected instance's site count, which is weaker.** v2 has no
  per-entity movie route. A studio resolves to its site through `GET /series`, whose response grows with
  how many sites that Whisparr tracks (not with Cove's library), then costs two further reads for that
  site's episodes and files. A **performer** has no v2 entity at all, so an owned-file import for one
  still walks the synthesized set. That is a real, stated limit, not an equivalent bound.

A v3 build whose own API description does not declare the two routes is handed an adapter that
structurally lacks the capability, and every caller declines with a classified refusal. Nothing falls
back to reading the whole set — a failure never widens a read here.

### Two bounded reads that carry a stated ceiling

Both of these page to a hard limit rather than growing without one, so each states what happens at the
limit instead of leaving the caller to assume exhaustion.

- **The direct-metadata catalogue read** (`StashDbGraphQlClient`, `TpdbClient`) stops at 60 pages ×
  100 = **6,000 scenes**. Reaching the ceiling is reported as a truncation discriminator that rides the
  catalogue record, the wire (`catalogueTruncated`) and the Missing tab, because the Missing diff is
  `catalogue − owned − excluded`: a capped read that reads as exhaustive under-reports missing scenes
  for exactly the largest entities. The remedy is the signal, not a bigger window — raising the ceiling
  only moves the point at which the same silence occurs.
- **`IWhisparrReconcileSource.ListMoviesAsync`** materializes the connected instance's whole movie set
  and indexes it. That is the diff counterpart chosen precisely to avoid StashDB egress, and it is a
  good trade at today's scale — but it is bounded by *Whisparr's* size, not by a constant, and once a
  library is fully synced the Whisparr movie count converges on the Cove scene count. So "O(Whisparr)"
  becomes "O(library)" by construction. **Recorded ceiling: it is unmeasured above ~10k movies and must
  not be assumed safe at 100k.** It is the one remaining whole-set in-memory read on the push path; the
  measurement, and a paged replacement if that measurement calls for one, are deferred rather than
  assumed unnecessary.

## The studio/performer monitor flow

Monitoring is a two-control feature that shares one server-side spine. The UI rides Cove's native
slots: a single **action-row button** (`WhisparrMonitorButton` on
`studio-detail-actions` / `performer-detail-actions`) is the *only* monitor control, and a
display-only **status line** (`WhisparrStatusLine` on `*-detail-bottom`) shows the quiet count. Both
slot components read their entity from **top-level props** (`props.studio` / `props.performer`) per
Cove's slot contract — never `props.context.*` — and forward the entity's own Cove remote ids to the
endpoints; the server resolves the Whisparr id by the **connected version's** endpoint (`StashDbEndpoint`
on v3, `TpdbEndpoint` on v2 — `WhisparrOptions.IdentityEndpoint`, the same endpoint-match rule
`CoveLibraryPort` uses) so no caller ever supplies a bare id. A v2 studio resolves by its ThePornDB id
and monitors as a site (see [Whisparr v2 adapter](#whisparr-v2-adapter)); a performer is v3-only.

`EntityMonitor.SetMonitorAsync` is an **add-then-flip**: it reads the entity in Whisparr, creates it
`monitored: false` if absent (applying a quality profile and a root folder both resolved from
Whisparr's own root-folder list at call time, and a read-or-create `cove-sync` origin tag), then PUTs
the target monitored state. Creating an entity that
already exists is treated as success (a 409/exists is not a duplicate). **Turning monitoring on never
triggers a Whisparr search** — but on v3 it does fire a targeted metadata *refresh* Cove owns, then
reconciles the discovered catalogue to your chosen scope (see
[Monitor-acquire semantics](#monitor-acquire-semantics-catalogue-population) below); a refresh discovers
scenes, it never grabs.

`GetStatusAsync` reads the count straight off the Whisparr studio/performer resource (no StashDB call,
no movie-set scan): `ScenesTotal` is the entity's full StashDB catalog and `ScenesPresent` the scenes
already in Whisparr's library — exactly what Whisparr's own studio/performer view shows (e.g. `1 of 147`).
When Whisparr reports no catalog for the entity, `HasCounts` is false and the status line degrades to a
bare "Monitored in Whisparr" rather than a misleading "0 of 0".

**Loop-safety.** Monitoring adds no new ingest path. When Whisparr grabs a monitored entity's scene it
imports into Cove through the **existing v1 auto-import** (the webhook + polling-reconcile backstop
below) — the same duplicate-claimed-once ingest, the same never-move-or-delete-inside-a-Whisparr-root
rule. There is no second receiver, and the monitor toggle issues no search, so enabling monitoring
cannot start a grab/import feedback loop on its own.

To keep the two components from each firing a full `/monitor-status` read on every page view, the
frontend shares one deduped call through a small `monitorStatusStore` keyed by the entity; a toggle
refreshes that shared state so the button and status line stay in step.

### Monitor-acquire semantics (catalogue population)

Whisparr populates a studio's scene catalogue only while the studio is `monitored: true`, and when that
population runs it **hardcodes every discovered scene `monitored: true`**. On v3 that is a problem for
the **New releases only** scope: leaving the studio monitored and relying on Whisparr's own scheduled
refresh does not avoid an all-monitored back-catalogue — it only defers it, so within a day "New releases
only" silently becomes "All scenes" and the whole back-catalogue is RSS-grab-eligible.

So on a v3 monitor-on Cove **owns the population moment**. After the flip sticks it fires a *targeted*
metadata refresh scoped to that one entity (`RefreshStudios {studioIds:[id]}` /
`RefreshPerformers {performerIds:[id]}`), waits for the queued command to report completed, then
reconciles the discovered catalogue's `monitored` flag to your chosen scope with one bulk
`PUT /movie/editor`:

- **New releases only** (default) — the discovered, un-owned back-catalogue (attributed **and** file-less)
  is left **visible but unmonitored**, so RSS auto-grab skips it, while the container studio/performer
  stays monitored so genuinely new future scenes still arrive monitored. Scenes you already own
  (file-present) are never touched.
- **All scenes** — the back-catalogue is left **monitored**: the deliberate acquire-everything choice.

Which rows count as a studio's is decided by the studio's own **StashDB id**, exactly as a performer's are
— never by comparing a studio's name against the one carried on the movie row. Names are display text and
belong to whichever side wrote them; the id is the same value in both systems. One consequence is
user-visible: a studio whose title in Whisparr differs from Cove's still attributes its scenes, where a
name comparison would have reported it as holding none. A studio with no title at all is likewise
attributable. On this build the two travel together — no row carries a studio title without also carrying
the studio's id — so the id-based rule reaches every row the name-based one did.

v2 (Sonarr) reaches the same **New releases only** result a different way and needs **no** refresh: the
site (series) add carries `addOptions.monitor:"none"` (with `monitorNewItems:"all"` for future episodes),
and Sonarr **honors** that lever rather than hardcoding the discovered episodes monitored — it has no v3
refresh-flood analog, so adding a speculative v2 refresh would only risk an unverified grab.

The loop-safety invariant behind all of this: a metadata **refresh discovers, it never grabs** (it carries
no search intent); the **`monitored` flag is the RSS-grab pivot** (Whisparr's `MonitoredMovieSpecification`
rejects an unmonitored movie), so reconciling that flag is exactly what expresses the scope; the refresh is
**always scoped to a single entity id** — Cove never issues a global (empty-id) refresh that would rebuild
every entity and hammer StashDB; and a bulk **monitor-many** paces one entity at a time (sequential, one
in flight) so many heavier per-entity refreshes never storm Whisparr's command queue.

## The scene Whisparr-status surfaces

Scene status answers, for every scene, *what is its Whisparr state?* — one of **notAdded /
excluded / unmonitored / monitored**. It is read-only and opt-in; nothing is mutated and
no StashDB call is made. The whole surface is **v3-only**, for one reason that covers every endpoint below:
the state is keyed on a scene-level id, and a v2 scene carries none.

`SceneStatusProjector` derives the state from data the extension already has: the reconciliation movie
set (the same batch Whisparr movie lookup reconciliation uses) plus one read of Whisparr's exclusion
list (`GET /api/v3/exclusions`, v3 only).
`SceneStatusProjector.Classify` is **exclusion-first, then movie-centric**: an excluded id wins over a
matching row, and otherwise the matched movie's `monitored` flag alone decides — so a filed movie is
`monitored` or `unmonitored`, never a state of its own (`hasFile` is a separate secondary signal
reported beside the state), and a row that matches is never `notAdded`. Exclusion
is checked from the exclusion set keyed on the scene's StashDB id. **No StashDB call is made** — the
whole derivation is Whisparr-and-Cove-only.

Four read-only, `configure`-gated, stored-creds-only endpoints back the surfaces:

- `GET /scene-status-summary` — the library-wide 4-state counts (the toolbar summary). v3-only, by narrowing
  to `IWhisparrStatusIndexSource`: no synthesized v2 row keys, so the index a v2 connection could build is
  empty for a library of any size and the partition over it read `notAdded` for every scene. It answers
  `VERSION_UNSUPPORTED` before the transport rather than answering a partition that is uniformly wrong.
- `POST /scene-status-batch {coveIds}` — the states for one visible grid page, as one DB read plus one
  Whisparr fetch. v3-only: a v2 connection carries no scene-level id.
- `POST /scene-detail {coveId}` — Whisparr-owned facts for one scene (state, monitored, quality,
  cutoff), resolved server-side from the Cove id. A scene with no provider id is a handled 200
  (`NO_STASHDB_IDENTITY`) with zero outbound calls.
- `POST /scene-releases-list {coveId}` — the indexer release list, fetched **only** on explicit UI
  expand (one scene at a time), never in the summary/detail path.

There is no composed stage projection over these reads, and no separate `read`-gated tier beneath them.
A widget wanting a scene's whole arc composes it from the reads above, and the honest consequence is that
a monitored-but-fileless scene reports exactly what Whisparr holds — `monitored` with no quality or
cutoff — rather than an invented stage. In-flight state is not part of this surface at all; the download
queue is its own read, `GET /activity/queue`.

Every `NO_STASHDB_IDENTITY` response also carries a `provider` field derived from the connected
version (`StashDB` on v3, `ThePornDB` on v2) so the UI names the provider without hardcoding it.

The `SceneWhisparrState` enum's wire casing is **pinned to camelCase** by its type-level
`[JsonConverter]`. `Contracts/WireSerializers.cs` keeps two response options objects — a plain-Web one
and one that adds camelCase enum strings — and both agree with the type-level converter, so no
per-property stamp is needed. The frontend logic (`sceneStatusLogic.ts`) keys its label maps on those
exact camelCase strings, so any drift fails the offline gate rather than silently blanking rows;
`WireEnumCasingTests` asserts each enum member's literal wire value.

`MonitorScope` never reaches a response at all. It arrives inbound as a request string (parsed
case-insensitively, falling back to the stored default) and is persisted PascalCase, since the options
blob is a stored value rather than the wire. That stored spelling is the only casing pinned for it.

### Where status shows (cards, tab)

Whisparr status paints **directly on library cards**, gated by an off-by-default toolbar pill so the
cards stay clean until the user opts in. The host exposes the card slots this rides on and contains
each one, so a misbehaving extension cannot break a card. `OverrideComponent("video.card")` remains a
silent no-op, so the badge renders in the card CONTENT area rather than by replacing the card.

1. **Per-card badges — native, pill-gated.** `WhisparrLibraryToggle` (on the `*-list-toolbar-end`
   slot) reveals the badges and a library-level count row (on the `*-list-row` slot) in one toggle,
   sharing on/off state through `libraryToggleStore`. The scene badge rides `video-card-content`
   (`WhisparrCardBadge`); the studio/performer "Monitored · present/catalog" badge rides
   `studio-card-footer` / `performer-card-footer` (`WhisparrEntityCardBadge`). Each per-page batch
   costs one DB read + one Whisparr fetch, not one call per card.
2. **Scene detail Whisparr tab — the native per-scene surface.** The `AddTab("video", …)` detail-rail
   tab (`WhisparrScenePanel`). It shows the status badge + Whisparr-only facts, and its Monitor /
   Search / Add controls are live (see
   [The outward scene & bulk mutation surface](#the-outward-scene--bulk-mutation-surface)).

**Version / entity gating.** The studio badge + row register on **both** v2 and v3; the performer and
per-scene STATUS surfaces are **v3-only** (v2 has no performer entity to monitor and no scene-level id).
A v2 studio with a ThePornDB id resolves and badges normally; the v3-only endpoints return
`VERSION_UNSUPPORTED` on a v2 connection and their slots are simply not registered there.

This gating is about the *status* surfaces, not discovery. The Missing tabs are a metadata-source feature
and register on both versions for all three kinds — see
[The discovery-source contract](#the-discovery-source-contract).

## The discovery-source contract

The Missing tab enumerates an entity's full metadata catalogue and diffs it against what Cove owns.
That catalogue reaches the diff through one `IDiscoverySource` seam served by a **direct** source that
reads Cove's own configured metadata server — StashDB on v3, ThePornDB on v2. It is the only catalogue
source: the same direct source serves both monitored and unmonitored entities, and Whisparr is never
read for the catalogue. The source feeds a pure difference engine that keys on the source id, so the
diff stays provider-agnostic.

**All three axes are served on both versions** — studio, performer and tag — because the catalogue comes
from the metadata source rather than from Whisparr. This is why discovery reaches more entities than the
*outward* surface does: performer monitoring is still v3-only (Whisparr v2 has no performer entity to
monitor), but ThePornDB lists a performer's scenes and filters scenes by tag, so both tabs work on v2.
The upstream criterion differs per kind: StashDB filters its GraphQL `queryScenes` by each id, while
ThePornDB gives a site and a performer their own scene sub-resource and filters `/scenes` by a tag's
**numeric** id. The projection carries the source's own total — a real count, whatever the source reports
for that entity (a site of a few hundred, a performer of a few thousand, say) — but ThePornDB SATURATES
that count at 10,000, so a broader set is flagged as a lower bound and
renders "10,000+". End-of-catalogue is still inferred from a short page, never from the count.

A card is only useful when it carries the full set a reader expects: a landscape cover, the title,
release date, studio, performer names **and** avatars, tags, and an overview. The risk with a
pluggable source is a new one silently shipping under-filled cards — mapping only the title and cover,
leaving every richer field null, with nothing to flag the gap. The contract closes that:

- `DiscoveryField` names the required card fields every source must account for.
- A source either **populates** each field on the `WhisparrMovie` it maps, or **declares** it in
  `UnavailableFields` — an honest, capability-by-presence gap list. The default is empty, so a
  full-capability source declares nothing and supplies everything.
- A cross-source conformance test reflects over every `IDiscoverySource` implementation in the
  assembly and asserts each required field is either populated on a registered sample or declared
  unavailable. A source that under-maps a field without declaring it fails the suite, and a newly
  added source with no registered sample fails too — so an under-filled card fails the build rather than
  slipping past review.
- The samples are registered per **(source, kind)**, not merely per source, because one source serves
  several entity kinds through different upstream criteria and a kind whose payload is thinner than its
  siblings' would slip past a single-kind sample. The set of kinds a source actually serves is
  **probed** — nothing declares it — so opening a new kind without registering its sample fails the
  gate by mechanism rather than by review.

A second optional capability sits on the same seam. Discovery needs the SOURCE's id for an entity, and a
Cove tag is free-form — it usually stores none. So `IDiscoverySource.ResolveIdByNameAsync` lets a provider
recover an id from the entity's **name**: StashDB answers it with `findTagOrAlias` (exact name or alias),
ThePornDB with `GET /tags?q=` filtered to a single exact match. The default is "no name lookup", so a
provider that cannot do it simply does not override — capability-by-presence again, no probe.

The endpoint asks the **provider**, never the version, so v2 and v3 behave identically. It is strictly a
fallback: a stored `RemoteId` always wins, and a name the source cannot match confidently leaves the id
unresolved so the honest `noSourceId` state shows rather than another entity's catalogue. ThePornDB's
`q=` is a fuzzy relevance search, which is why only an exact match counts — taking the first row would
silently query the wrong tag.

With only direct sources, every source supplies the whole rich field set, so `UnavailableFields` is
empty across the board and the conformance guard has no gap to exempt — it simply guarantees richness.
A user gets rich cards on **both** v3 (StashDB) and v2 (ThePornDB): the direct ThePornDB source maps a
landscape cover, index-aligned performer names and avatars, tag names, and an overview, matching the
StashDB source field-for-field. Whisparr status stays deliberately outside the contract — the status
pill is derived after the source from the reconciliation movie index, never carried by the catalogue.

## The outward scene & bulk mutation surface

The scene panel's controls are live and there are bulk actions, all through the
`configure`-gated, stored-creds-only endpoints above. The frontend holds no wire or loop-safety
knowledge: it shapes the request bodies in the import-free `sceneActionsLogic.ts` (offline-gated) and
POSTs them; each handler resolves the Whisparr identity server-side and delegates to `SceneActions`.

- **Per-scene, from the scene Whisparr tab.** "Add to Whisparr" (`/scene-add`, shown only when the
  scene is not-added), "Monitor this scene" (`/scene-monitor` — the server does add-then-monitor when
  the scene is not yet in Whisparr), and "Search for this scene" (`/scene-search`, enabled once added).
- **Bulk, from the extension's own Whisparr menu on studio/performer.** Cove's built-in "⋮" Actions menu
  on a studio/performer exposes **no** extension hook — only the video page's menu does. So the
  extension does **not** inject into Cove's ⋮; instead the
  action-row Whisparr button (the `*-detail-actions` slot it already owns) becomes a menu trigger that
  opens `WhisparrMenu` — a small branded popover rendered through a React portal to `document.body`,
  fixed-positioned from the trigger's bounding rect so the action row's overflow cannot clip it. It
  holds "Monitor in Whisparr" (the studio/performer monitor toggle — `/monitor`) above a monitor-scope
  radio group, then three bulk items: "Add all missing" (`/bulk-add-missing`, a local Cove-vs-Whisparr
  diff with no StashDB GraphQL, rendered only where the connected version offers a per-scene add),
  "Reflect owned in Whisparr" (`/reflect-owned`, rendered only where the version offers owned import),
  and "Search all monitored" (`/bulk-search-monitored`). The whole bulk block is shown only when the
  entity is monitored (quiet by default).
- **Bulk, from the videos-list selection bar.** Multi-selecting scenes and picking the selection
  bar's Whisparr action opens a chooser with six ordered items — Add to Whisparr · Monitor ·
  Unmonitor · Search now · Search for upgrades · Exclude — each running one `/videos-batch` op as a
  Job-Drawer job with an outcome per selected id. Monitor and Unmonitor dispatch per scene through
  the same `SceneActions.SetSceneMonitorAsync` add-then-monitor spine the detail-rail toggle uses:
  Monitor on a not-added scene registers it non-grabbing first, then flips `monitored`; Unmonitor on
  a not-added scene is reported Skipped rather than adding it. There is no scope prompt at scene
  level — MonitorScope (New releases only / All scenes) is an entity concept, and a scene is one
  Whisparr movie with a boolean `monitored`, so there is nothing to scope.

**Loop-safety.** Every *add* — per-scene add, add-all-missing, bulk Monitor's register-first leg,
and owned-scene availability registration — issues `searchForMovie:false`, so it registers the
movie in Whisparr **without grabbing**; a monitor flip itself is a PUT, never a `/command`. Only the
explicit Search actions — "Search for this scene", "Search all monitored", and the bulk Search now /
Search for upgrades — ever issue a `MoviesSearch`, so only a deliberate user click can start a grab.
Every mutation is origin-tagged (`cove-sync`) and idempotent (a 409/exists is treated as success).
When a grab does result from an
explicit search, it imports into Cove through the **same** On-Import webhook + polling reconcile as any
other Whisparr grab — there is no second ingest path, and that path is already idempotent
(`EventLedger`), so a Cove-initiated add can never feed a re-ingest loop.

## Bulk library sync ("Sync my library to Whisparr")

The settings page offers a one-click "Sync my library to Whisparr" that applies the same
reflect-owned / (v3) scene-add / optional-monitor primitives across the **whole** Cove library in one
paced background job — so a first-time user need not hand-select every studio. It adds **no** new
mutation spine and **no** grab path; it enumerates the library and drives the existing per-entity
runners. Two `configure`-gated, stored-creds-only endpoints back it:

- `GET /sync-preview` — a pre-run count of how many studios / performers / (v3) owned scenes carry a
  connected-version id and will register, versus how many are **skipped** for carrying none. The count
  is computed from a whole-library read taken under `CovePrincipal.System()` (set on a fresh scope and
  restored in a `finally`), because Cove's per-principal authz filters would otherwise undercount a
  background read. `SyncPreviewCore` is pure and gates each bucket on which role interfaces the
  adapter implements, so a v2 preview reports **studios only** (no performer, no per-scene add).
- `POST /sync-library` — enqueues one **exclusive** `whisparr-sync-library` job and returns its
  `{ jobId, description }`. A library-wide sync is job-only; it never runs inline.

`RunSyncLibraryJobAsync` opens a fresh scope, sets `CovePrincipal.System()` around the whole
enumerate-and-fan-out (restoring the prior principal in a `finally`), and builds the unit list with the
pure `BuildSyncUnits`. On **v2**, where there is no per-scene add, the fan-out first plans a
non-grabbing **register-site** unit per identified studio — gated on the v2/site role shape
(`adapterCaps is not IWhisparrScenePush and IWhisparrOwnedImport`), ordered **before** the reflect-owned units, and
**independent of the monitor toggle** — because v2 reflect-owned can only attach owned files to a site
that already exists, so a clean v2 needs its sites registered first (units run in list order at
`maxInFlight:1`, so emitting register ahead of reflect is what makes register precede reflect for the
same studio). The rest of the list is a reflect-owned unit per identified studio (both versions) and
per performer (v3 only), an add unit per identified v3 scene, and — only when the user opted into
monitoring — a monitor unit per entity carrying the chosen scope (New releases only [default] / All
releases). It then dispatches every unit through the **existing** `RunEntityOpAsync` / `RunVideoOpAsync`
batch runners via a single `maxInFlight:1` `RunBatchAsync`, so the sync inherits the batch surface's
pacing, capability gating (unsupported `(kind, op, version)` combos are skipped cleanly, never a false
success), and job-unit outcome mapping. No id-less entity ever makes an outbound call.

Registering a v2 site does not give it episodes at once: Whisparr fetches the site's episodes
asynchronously (a Sonarr-style refresh), so a just-registered site can still be fileless when the same
run's reflect-owned pass reaches it. The sync does not treat that as a failure — a reflect-owned over an
episode-less site returns a clean Empty/Ok, and the owned files attach on a later idempotent
reflect-owned pass or via the webhook / reconcile backstop. Registering the site present is the success.

**Loop-safety.** The job reuses only the non-grabbing verbs: every registration issues
`searchForMovie:false` (v2 `searchForMissingEpisodes:false`), is origin-tagged `cove-sync`, and treats
a 409/exists as success — so a re-run is idempotent and adding never grabs. The v2 register-site verb
holds the same line: it adds the site `monitored:false` with `addOptions.monitor:"none"`,
`monitorNewItems:"none"`, and `searchForMissingEpisodes:false`, and issues no `/command` — creating the
site present-yet-inert, never arming a future grab and never flipping monitoring. `SyncOp` has no grab
member, and a structural source guard (alongside `NoMutationTests`) asserts the dispatch reaches only
the non-grabbing runner verbs, never a `Search` path. There is no auto-sync on Cove-add, no scheduler,
and no nag banner: the job runs only when the user clicks Sync.

## Whisparr v2 adapter

Whisparr v2 is a **Sonarr fork**: content is modeled as **series (a studio/site) → episodes (scenes)**,
sourced from **ThePornDB (TPDB)**, and served under the *same* `/api/v3` path prefix as v3. `V2Adapter`
sits behind the same `IWhisparrAdapter` port as `V3Adapter` and reuses the whole pipeline — the
transport client, options, identity matcher, ingest coordinator, webhook receiver, and reconcile job
are all version-agnostic. Only two things are genuinely v2-shaped.

### Scene enumeration (series → episode → episodefile)

v2 has no `/movie` entity, so `V2Adapter.ListMoviesAsync` is the one substantive method: it reads
`GET /series`, then per series `GET /episode?seriesId=N` and `GET /episodefile?seriesId=N`, and
synthesizes one normalized `WhisparrMovie` per episode (joining `episode.episodeFileId → episodefile.path`
for the on-disk path). The five connect-level calls (status, root folders, quality profiles, history,
webhook register) are byte-identical envelopes on v2, so they delegate to `WhisparrClient` unchanged. A
non-Ok read at any level propagates as the same-state result rather than a partial scene list.

### Why the StashDB match no-ops for v2, and how v2 matches instead

Every synthesized v2 row is built with `StashId = null` and `ItemType = "v2scene"` (never `"scene"`).
That is deliberate: `IdentityMatcher` only reads a StashDB-comparable id on `"scene"`-typed rows, so
the StashDB check **no-ops by design** for v2 — but that is not a limitation, because v2 matches
instead on the id it actually carries: the ThePornDB id (in Sonarr's `tvdbId` field). This is the same
id-only rule as v3, just keyed on a different id.

Whisparr v2 scenes carry a **TPDB scene id** and no StashDB id anywhere (verified: 0 StashDB ids
across 627 scenes of a real studio; a TPDB scene's own links expose no scene-level StashDB id either —
only *performers* carry StashDB links). So there is no `v2 scene → StashDB scene` join to make. The
adapter carries the TPDB id in `ForeignId`, and the `"v2scene"` sentinel keeps it out of the StashDB
check so a TPDB integer id can never be compared to — and falsely matched against — a Cove StashDB
UUID; instead it is compared against the video's TPDB ids. A v2 scene with no ThePornDB id yet (not
identified in Cove) shows as unmatched until Cove's Identify feature attaches one, exactly like an
unidentified v3 scene.

### The outward surface on v2 (studio-monitor GO via TPDB; capability-specific defers)

The *outward* surface works on v2, keyed on the identity v2 actually carries. Where v3 resolves its
Whisparr target by the StashDB id, v2 resolves by the **ThePornDB (TPDB)** id in Sonarr's `tvdbId`
slot; `WhisparrOptions.IdentityEndpoint` picks the endpoint per connected version
(`StashDbEndpoint` → v3, `TpdbEndpoint` → v2) and `Api.ResolveRemoteId` returns the entity's matching
remote id or `null` (a handled no-identity outcome), always from the entity's own forwarded remote ids.

A Cove **studio** maps to a v2 **site (series)**: `V2Adapter.SetStudioMonitorAsync` delegates to its
private `SetSiteMonitorAsync`, an add-then-flip mirroring `V3Adapter.SetStudioMonitorAsync` over v2's
site model — resolve the site by its TPDB id
(`GET /series` matched on `tvdbId`), add the addable row (`series/lookup?term=tpdb:{id}`) **non-grabbing**
(`addOptions.monitor:"none"`, `searchForMissingEpisodes:false`) with the caller's root / profile /
`cove-sync` origin tag, then PUT the requested `monitored` state. A `400 SeriesExistsValidator`
duplicate is idempotent success. Status is grabbed-of-total over the site's episodes, "search all
monitored" posts `EpisodeSearch` (the one grab-capable v2 verb), and only that explicit search grabs.
`V2OutwardParityTests` proves both the GO flows and every defer.

The site-add spine — resolve by TPDB id → add the lookup row → treat a `SeriesExistsValidator`
duplicate as an idempotent re-read — is factored into one `EnsureSiteAddedAsync` helper shared by two
callers: this monitor add-then-flip, and the monitor-independent **register-site** verb the bulk sync
uses to seed a clean v2 (`monitored:false`, grabbing disarmed). Only the wire add-body differs; the
lookup / create / 409-reread is identical, so both paths add a site the same way.

The capabilities with no v2 analog DEFER on v2 — a classified `VersionMismatch("v2")` **before the
transport** (zero wire calls, no stray `cove-sync` tag). Capability is expressed by **presence of a role
interface**, never a probe: `IWhisparrAdapter` composes only the seven roles both generations honor, the
five v3-only roles are declared by `V3Adapter` alone, and each seam narrows before resolving
root/origin-tag — `SceneActions` on `SelectAdapter() is not IWhisparrScenePush`, `EntityMonitor` on a
non-studio kind whose adapter `is not IWhisparrPerformerMonitor`:

| # | Capability | v2 mechanism | Verdict | Reason |
| --- | --- | --- | :---: | --- |
| 1 | studio-monitor / status | `series` add-then-flip + episode counts, keyed on `tvdbId` (TPDB) | **GO** | a site (series) is monitorable and resolvable by the TPDB id a Cove studio carries |
| 2 | search-all-monitored | `EpisodeSearch` cmd over the site's episodes | **GO** | the site's episodes are the search input; the one grab-capable v2 verb, non-grab add stays search-free |
| 3 | performer-monitor | none (`/performer`, `/credit` 404) | DEFER | no performer entity at all — performers are embedded `episode.actors` metadata, nothing monitorable |
| 4 | scene-add / scene-monitor | none (no `POST /episode`) | DEFER | episodes are not independently addable (they arrive with a series import); a scene is acquired by adding its site and searching the episode |
| 5 | search-for-upgrades | no cutoff-upgrade-only variant | DEFER | Sonarr has no cutoff-unmet-only search; v2 keeps a single grab verb (the episode search) by design |
| 6 | bulk-add-missing | none | DEFER | builds on the per-scene add, which v2 lacks |
| 7 | exclusions / interactive release grab / per-scene status views | `/importlistexclusion` + `/release?episodeId=` exist | DEFER | endpoints exist but return TPDB-keyed rows that cannot be tied back to a Cove scene without a scene-level id |

Loop-safety holds identically to v3: the v2 add is non-grabbing (`searchForMissingEpisodes:false`) and
origin-tagged, the monitor flip carries no `addOptions`, and only the explicit episode search issues a
`/command`. The UI enables a v2 studio with a TPDB id and disables a no-analog capability reading
**"Currently available on Whisparr v3 (Eros)"** (single-sourced) — never migration-implying wording.

> This reverses the v1.1 "0 GO / 9 DEFER" verdict, which assumed v2 had no outward path. Empirical
> live verification against Whisparr 2.2.0.108 showed v2 sites are monitorable by TPDB id; the captured
> wire contract lives in the tests' `V2Fixtures`.

### Version-blind import (episodeFile fallback)

The webhook receiver parses the raw POST body directly — it is **not** behind the adapter seam and has
no idea which Whisparr version posted. A v2 On-Download body carries `series` + `episodes[]` +
`episodeFile` where v3 carries `movie` + `movieFile`, so `WebhookPayload` gained additive nullable
`EpisodeFile` / `Series` / `Episodes` fields and `WebhookReceiver` resolves the path version-blind:
`payload.MovieFile?.Path ?? payload.EpisodeFile?.Path` (and the upgrade id via
`Movie?.Id ?? Episodes?[0].Id`). Everything else — the token gate, ledger idempotency, audit log, root
guard, and coordinator — is reused verbatim. A malformed or path-less v2 body degrades to a 200 no-op,
never a silent half-ingest. The polling reconcile reads the same `downloadFolderImported` history rows
(`importedPath` / `droppedPath` / `downloadId`) on v2 as on v3.

## The API-key secret model

Cove's `IExtensionStore` is **plaintext at rest** — there is no encryption. The extension treats the
API key as a credential that stays server-side:

- The key is written to the store and used only to make the outbound call to the user's own Whisparr.
- **No response ever contains the key.** The options / status responses project it out through
  `OptionsView`, which exposes a `hasApiKey` boolean instead. The `OptionsView` type has no `ApiKey`
  property at all, so a key cannot leak by accident. On the client, `optionsFromServer` reads only
  the known-safe fields, dropping anything else.
- **The UI never pre-fills the key.** The field renders empty with a "Key is set" pill when a key is
  stored; a blank field on save preserves the stored key (write-only semantics — a blank submission
  means "unchanged", never "clear it").
- **No log line takes the key or a URL-with-key.** The source-generated `[LoggerMessage]` templates
  accept only the version, instance name, an unreachable reason, and the webhook outcome flag.

## Network posture and residual SSRF

The base URL is user-supplied and the extension manifest requests `network: ["*"]`, because Whisparr
is typically self-hosted on the LAN (`http://localhost:6969`, a Docker bridge address, a private
`192.168.*` / `10.*` host). The extension therefore makes an authenticated outbound request to an
address the operator chooses, which is an inherent server-side request forgery (SSRF) surface: a
`configure` user can point it at an internal host and read reachability / timing from the classified
result (`unreachable` vs `notWhisparr` vs `badKey`).

This is bounded rather than eliminated, because a full private-range block would break the intended
LAN use case:

- **The stored API key never leaves with a caller-chosen host.** `ResolveCredsAsync` only
  ever pairs the stored key with the stored host; a request that overrides the base URL must carry
  its own key. So the SSRF probe cannot also exfiltrate the stored credential.
- **Only `configure` users reach the outbound routes.** The list, test-connection, webhook-url, and
  register routes all require `extensions.configure` — a read-only user cannot drive an outbound call.
- **The transport edge validates the URL.** `WhisparrClient` rejects a relative, malformed,
  or non-`http(s)` base URL as `Unreachable` before dispatching, so `file://` and similar schemes
  never reach the socket.

Blocking specific link-local / metadata addresses (e.g. `169.254.169.254`) or adding an opt-in
"allow private targets" posture is a possible future hardening; it is deliberately not done here so
the common LAN-Whisparr configuration keeps working out of the box.

## The webhook

The webhook secret is a 256-bit token minted with `System.Security.Cryptography.RandomNumberGenerator`
(never `System.Random`) and persisted once in the options, so the URL is stable across reads. The
copy-paste URL is
`{coveBase}/api/extensions/com.alextomas955.whisparrsync/webhook?token={secret}`.

**Whisparr's own connection is the source of truth.** The connection the extension manages is named
**"Cove Whisparr Sync"**, single-sourced across the read and the write so the two agree. `GET
/webhook-url` lists Whisparr's notifications (`GET /api/v3/notification`, matched by that name) and
returns `{ url, registered }`: when the connection exists, its own `url` field and `registered:true` are
authoritative; when it is absent the URL falls back to the derived default (the persisted
`WhisparrOptions.WebhookHost`, else the request host) with `registered:false`. A not-found result — or a
Whisparr that is unreachable — degrades to that same success response (a 200, `registered:false`), never
a 500, so the settings page always loads.

Register is an **idempotent update-or-create**: `GET /notification` to find the row by name, then `PUT
/notification/{id}` when it exists, else `POST /notification` to create it (`implementation: "Webhook"`,
`configContract: "WebhookSettings"`). A unique-name `400` / a `409` resolves to success, so re-clicking
Register never errors and never leaves a duplicate. The token is always re-minted from the stored secret
onto the resolved URL, never read back from the existing row, and `/register-webhook` persists the
resolved host to `WhisparrOptions.WebhookHost` (preserve-on-blank) so a pre-registration host edit
survives a refresh. Both the read and the register work on **v3 and v2** — v2's notification endpoint is
the same `/api/v3/notification` in the Sonarr-shaped API. The copy-paste URL remains the guaranteed path
when auto-register cannot reach Whisparr.

The inbound `/webhook` receiver and its `X-Cove-Token` token auth are **unchanged**: this behavior
touches only how the URL is read back and how the connection is created or updated, not how events are
received or authenticated.

The notification also carries the secret as an **`X-Cove-Token` request header** (a `headers`
key-value entry in the payload), not only in the URL query. This is what makes Whisparr's **Test**
button succeed: the Test ping POSTs to the configured URL with the configured headers, so the
receiver sees the token in the header it validates and answers 200 instead of the 401 an unheadered
ping would get. The bare URL keeps its `?token=` for the copy-paste path, so both channels
authenticate the same secret.

**The header is the preferred channel.** The receiver checks `X-Cove-Token` first and only
falls back to the `?token=` query when the header is absent. A secret in a URL query string is
routinely captured by Kestrel/reverse-proxy/access logs outside this extension's control, so the
query fallback exists only for hand-pasted webhooks — when it is used, the extension logs a one-time
warning. **Register in Whisparr** (auto-register) always configures the header, so the recommended
setup never relies on the query token.

## Auto-import: webhook + polling-reconcile backstop

The webhook **receiver** consumes the URL above and turns a Whisparr On-Import into a Cove item. Two
independent channels drive it, and they are idempotent with each other so an import is ingested exactly
once no matter how it arrives:

- **Webhook (primary).** `POST /webhook` is the one anonymous route: the shared-secret token is validated
  FIRST, in constant time (`CryptographicOperations.FixedTimeEquals`), BEFORE the body is parsed — there
  is no Cove principal on a Whisparr request, so the usual `Forbidden(principal,…)` gate is deliberately
  omitted. A valid `Test` ping answers 200 with no ingest; a valid `Download` routes to the coordinator;
  an unknown event is a 200 no-op; a missing/wrong token (or an unconfigured secret) is a fail-closed 401.
- **Polling reconcile (backstop).** A self-scheduled `PeriodicTimer` loop (started in `InitializeAsync`,
  cancelled in `ShutdownAsync`) enqueues an EXCLUSIVE `IJobService` reconcile job every 15 minutes. The
  job pages `GET /api/v3/history` newest-first since a stored **checkpoint**, feeds each new
  `downloadFolderImported` record through the SAME coordinator, and advances the checkpoint so a re-run is
  incremental. On the first run it seeds the checkpoint at the newest existing record and ingests nothing,
  so the whole prior history is never retro-ingested. This is the guarantee the extension is never
  webhook-only — an On-Import the webhook dropped is still caught here.

Both channels converge on:

- **`IngestCoordinator`** — resolves the scoped host `IScanService` from a fresh `CreateAsyncScope()` and
  imports the file **in place** via the kind-appropriate `ImportDownloaded*` (never moved or
  deleted). A `WhisparrRootGuard` runs FIRST: a path that does not canonicalize inside a known Whisparr
  root (or an unavailable root set) is rejected fail-closed. A gone/kind-unresolvable in-root
  path falls back to a scoped `StartScan` and is flagged rather than failing silently.
- **`EventLedger`** — the ONE cross-channel idempotency key, `SHA-256(downloadId | NormalizePath(path))`.
  Both channels derive the identical key from the fields they both carry (download id + the imported
  path), so a webhook-then-poll overlap of the same import is a Skipped no-op. The reconcile
  additionally records a `hist:{record.id}` self-key so a re-poll of the same page is cheap — but the
  cross-channel dedup is always the shared key.
- **`ImportLog`** — a single-blob audit journal over `IExtensionStore`. Every attempt (Imported / Skipped
  / Flagged) appends exactly one entry with server UTC ticks, source (`webhook` / `poll`), event type,
  path, kind, Cove id, result, reason, and the ledger key. `GET /import-log` reads it back (read-gated)
  to feed the settings-page sync-health failure banner, which surfaces unresolved path-mismatch import
  failures and stays silent when imports are healthy.
- **`HealthStore`** — the `"health"` store key: one bounded record per dependency (acquisition /
  metadata / import) holding the last classified outcome, both timestamps, the consecutive-failure
  count, and the retained error. Keyed by dependency, so a repeat overwrites rather than appends and
  the blob cannot grow with failure volume. The retained error is **sticky but bounded** — a later
  success zeroes the count without clearing the error, which is what lets the page still show a problem
  that has already recovered, and an hour after the failure the error text alone expires
  (`DependencyHealth.RecoveredErrorRetentionTicks`) while both timestamps and the outcome remain. Expiry
  is applied in `Normalize`, which every load and every write already calls, and only when the
  consecutive count is zero, so a dependency that is still failing keeps its error indefinitely. A read
  normalises but never writes, so `GET /import-log` stays side-effect-free.

The secret is never logged; the raw webhook body is never logged; the audit log stores paths/ids/results
but never the API key or the webhook token.

## The safety model

Auto-import means Cove and Whisparr both act on the same files, so the extension is built around three
guarantees that keep the two systems from fighting each other.

### Never move or delete inside a Whisparr root

`IngestCoordinator` **imports in place**: it hands the imported path to the host `IScanService`
(`ImportDownloaded*`) or, on a gone/kind-unresolvable in-root path, a scoped `StartScan`. It holds no
filesystem-relocation primitive at all — Cove records the path, and the bytes stay exactly where
Whisparr put them. This is a structural property, not a runtime check, so it is enforced by a
**contract test** (`NoMutationTests`): driving a full webhook Download and a fallback records only
`IScanService` import/scan calls on the recording fake, and a source-level guard asserts the
coordinator source contains no `File.Move`/`File.Delete`/`Directory.*` API. If someone ever adds one,
that test fails.

### Warn on a re-grab feedback loop

If a Cove library root and a Whisparr root are the same directory (or one contains the other), an
import-in-place can look to Whisparr like a brand-new file and be re-grabbed — a feedback loop.
`WhisparrRootGuard` is what actually prevents it, and it is **fail-closed**. `RootOverlapDetector` owns
the containment rule the guard, the root derivation and this comparison all share: separator-normalized,
case-sensitive and segment-bounded, so `/data/media-evil` is never read as living beneath `/data/media`.
`Detect` pairs every Whisparr root against every Cove root in **both** directions, because either can be
the outer path. The root endpoint is byte-identical on v3 and v2, so this comparison answers on both
generations; the roots come from `WhisparrRootsPort`, the one seam every root read goes through, and the
Cove side from the host's configured library paths (falling back to the distinct parent folders of the
library's own files). `GET /folder-overlap` surfaces it, and the settings page draws it.

It stays an **advisory, never a gate**: cross-mount and containerized deployments legitimately see the
same library at different paths, and the extension cannot tell that apart from an accident, so it reports
the pair and leaves the judgement to the operator.

### Warn on a root × Scene Folder Format overlap

Whisparr Eros builds each scene's movie folder as `<root folder>` + the **Scene Folder Format**
output, and that format always begins with a literal path segment (the default is `scenes/…`). So when
the configured root folder's trailing segment equals the format's leading literal, the segment is
doubled — root `/data/media/scenes` + format `scenes/…` writes to `/data/media/scenes/scenes/…`, an
unfindable path where every scene reports "Missing". `SceneFolderOverlapDetector` — a pure, host-free
sibling of `RootOverlapDetector` — reads the
connected instance's format via `GetNamingConfigAsync` (`GET /api/v3/config/naming` →
`sceneFolderFormat`), extracts its leading LITERAL segment with `LeadingLiteralSegment` (the first
segment when it is a pure literal, else null — a token-leading or empty format yields nothing), and
`Detect` flags any root (`ListRootFoldersAsync`) whose trailing segment equals it, using the same
separator-normalized, case-sensitive, segment-bounded comparison as the ingest guard. Each hit carries
the offending root, the doubled `prefix`, and a `suggestedRoot` (the parent — the root with the
overlapping segment stripped). `GET /folder-overlap` carries it beside the containment finding, each
tagged with its `kind` so one array holds both.

This finding is **v3/Eros-only**, because the Scene Folder Format is an Eros concept and a v2 naming
config carries none at all. On v2 the response therefore lists the kind in `notApplicable` and issues no
naming read — a positive statement that this connection cannot answer it, never an empty set the reader
would take for an all-clear. The same rule covers a leg that cannot compare either finding: the response
is `checked:false` plus a reason naming the real cause (no host saved, the read did not answer, Cove's
own roots unresolvable, or a stored version this build does not manage), always as a **200** — a 4xx
would be swallowed by the client's read catch into exactly the silence this shape removes. Like the
containment finding it is a **best-effort advisory, never a hard gate**: the extension cannot change
Whisparr's config, so it names the fix (set the root to the level above) and leaves the correction to the
user.

### An honest manifest

Cove does not enforce the manifest's `permissions.network` — it is a declaration for the operator and
reviewer. The manifest therefore states the real surface honestly: the **outbound** authenticated
calls to the configured Whisparr host, and the **inbound** token-gated `/webhook` receiver that accepts
On-Import events. The API key and webhook secret are described as server-side-only and never logged.

### Pitfall: an auth-disabled Cove and the first remote webhook

Cove has a security failsafe that can lock down when it first sees a request from an unexpected remote
address with authentication disabled. Because the webhook is exactly such a request (Whisparr calls in
from another host/container), a Cove deployment running with **authentication disabled** can trip that
lockdown on the first webhook. This is outside the extension's control — the recommendation is to run
Cove **auth-enabled** in any deployment that receives remote webhooks (the token still authenticates
the webhook itself). It is an advisory, not something the extension can prevent.
