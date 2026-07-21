# Architecture

Whisparr Sync connects Cove to a self-hosted [Whisparr](https://whisparr.com) instance (v3 (Eros) or
v2). This page traces how the extension turns a URL + API key into a verified connection,
auto-populated root-folder / quality-profile lists, and a registered webhook — for a contributor
reading the code for the first time.

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
  single-shot.
- **`Adapters/IWhisparrAdapter` + `V3Adapter` / `V2Adapter`** — the version-adapter boundary.
  Each adapter owns its version's wire knowledge (endpoint paths, the content-enumeration shape, the
  webhook notification payload); the handlers never hold it. `AdapterSelector` picks the adapter from
  the detected major version — `3 → V3Adapter`, `2 → V2Adapter` — and **refuses** any other version —
  never a silent wrong-adapter call. The v2 mapping is detailed in
  [Whisparr v2 adapter](#whisparr-v2-adapter) below.
- **`Options/WhisparrOptions` + `OptionsStore`** — a single JSON blob persisted over Cove's
  `IExtensionStore` under the `"options"` key (URL, API key, selected/detected version, root-folder
  id, quality-profile id, webhook secret). A corrupt or absent blob loads as safe defaults.
- **`Webhook/WebhookUrlBuilder`** — mints the webhook secret and builds the copy-paste URL.
- **`WhisparrSync.Api`** — the minimal-API endpoints and the settings-tab manifest.

## Endpoints

All are mounted under `/api/extensions/com.alextomas955.whisparrsync/` and are permission-checked
**in the handler** (the host's `[RequiresPermission]` filter is inert on minimal-API endpoints). The
side-effect-free read projections gate on `extensions.read`; every route that reaches the stored
credentials or makes an outbound call gates on `extensions.configure` (the host confirms
`extensions.configure` *implies* `extensions.read`, so a route that must exclude read-only users has to
require `configure`). The one deliberate exception is the inbound `/webhook` route, which carries no Cove
principal at all — a shared-secret token is its auth, so it omits the principal gate entirely.

| Route | Method | Permission | Purpose |
| ------- | -------- | ----------- | --------- |
| `/test-connection` | POST | configure | Classify the connection; return version + instance name on success |
| `/status` | GET | read | Whether the extension is configured (no key) |
| `/options` | GET | read | The persisted options as a redaction-safe view (no key, only `hasApiKey`) |
| `/options` | POST | configure | Persist URL / version / root folder / quality profile (write-only key) |
| `/rootfolders` | POST | configure | The instance's root folders (creds in the body) |
| `/qualityprofiles` | POST | configure | The instance's quality profiles (creds in the body) |
| `/webhook-url` | GET | configure | The ready-to-use webhook URL with the embedded secret |
| `/register-webhook` | POST | configure | Best-effort auto-register of the Cove webhook in Whisparr |
| `/preview-sync` | POST | configure | The zero-mutation reconciliation diff (matched / needs-review / unmatched rows + counts) |
| `/reconciliation` | GET | read | The last persisted match map + status counts (a pure store read) |
| `/match/confirm` | POST | configure | Confirm a needs-review suggestion (writes only the match store) |
| `/match/reject` | POST | configure | Reject a needs-review suggestion (writes only the match store) |
| `/webhook` | POST | anonymous (token) | Inbound Whisparr On-Import receiver — ingests the imported file |
| `/import-log` | GET | read | The auto-import audit log: every attempt with its result, source, time, path, and Cove item + counts |
| `/root-overlap` | GET | read | A best-effort advisory: whether a Cove library root overlaps a Whisparr root (a re-grab-loop risk) |
| `/monitor` | POST | configure | Toggle a studio/performer's monitored state in Whisparr (add-then-flip); body carries the entity's remote ids only |
| `/monitor-status` | POST | configure | The quiet-status projection for a studio/performer: added / monitored / scenesPresent / scenesTotal (Whisparr's own present-in-library / catalog counts) |
| `/scene-add` | POST | configure | Register one scene in Whisparr as non-grabbing (`searchForMovie:false`, origin-tagged); body carries the Cove id only |
| `/scene-search` | POST | configure | Trigger a Whisparr search for one already-added scene (`MoviesSearch`) — the only per-scene route that may grab |
| `/scene-monitor` | POST | configure | Set one scene's monitored state (add-then-flip when not-added); Cove id + target flag |
| `/bulk-add-missing` | POST | configure | Register every Cove scene not yet in Whisparr for the entity, as a local diff (no StashDB GraphQL), all non-grabbing; kind + Cove entity id |
| `/bulk-search-monitored` | POST | configure | Trigger a Whisparr search across the entity's monitored scenes; kind + the entity's remote ids |

`/monitor` and `/monitor-status` are `configure`-gated for the same reason as the list routes:
they reach the stored credentials to call Whisparr. Both are POST because each carries a `RemoteIds`
body (a complex payload that cannot ride a GET under minimal-API binding), and neither ever accepts a
url or key from the caller — the handler pairs the stored key with the stored host only.

`/preview-sync` is `configure`-gated even though it only *reads*: it reaches the stored credentials to
call Whisparr, so a read-only user must not be able to trigger it (the same rule as the list
routes). `/reconciliation` is the only reconciliation route that is `read`-gated, because it reads the
extension's own match store and never touches the credentials.

## The reconciliation match model

Reconciliation answers one question for every Whisparr scene: *which Cove item, if any, is the same
thing?* It never mutates Cove or Whisparr — `/preview-sync` opens an `AsNoTracking` read over the Cove
library, fetches the Whisparr movie list, and composes a diff. The only writes in the whole feature
are Confirm/Reject, and they land solely in the extension's own match store.

`IdentityMatcher` matches on the **one remote id both systems already key on** — the StashDB UUID for
a v3 scene, the ThePornDB id for a v2 scene. Cove owns content identification: its own Identify
pipeline is what attaches a StashDB/TPDB id to a scene. Whisparr's job is acquisition/inventory
tracking — it can only add/search a movie it already carries a remote id for, not identify content.
So WhisparrSync correlates the two systems by the id they already share rather than inventing a
weaker identification system of its own.

An exact, case-insensitive id match auto-applies. A movie-typed id is never compared against a Cove
scene UUID. If *two* Cove videos share the same remote id (Cove does not enforce cross-video
uniqueness), the scene is sent to **needs-review** instead of being matched to an arbitrary one.
Anything with no id match at all is **unmatched** — the safe default, never a silent guess. An
unidentified file simply stays unmatched until Cove's own scraper attaches an id; the very next
reconciliation then picks it up cleanly via the id match.

Whisparr exposes no comparable file hash, and Cove carries no per-video studio field, so neither a
content-hash comparison nor a title/year similarity check could disambiguate reliably (a same-titled,
same-year scene from a different studio would score a perfect similarity score with no way to tell
them apart). That is why the design stays id-only rather than adding either back.

The match store is a single JSON blob over `IExtensionStore`, keyed on the **Whisparr movie id** (a
ThePornDB-matched row also carries no StashDB UUID, so the movie id is the one durable handle every
row shares).
Confirm upserts a `Confirmed` entry that is honored on the next reconcile; Reject records a `Rejected`
entry that suppresses the suggestion on re-run. A fresh `/preview-sync` recomputes the whole diff from
the current library and Whisparr state, but a persisted decision is **one-way in this release**: a
confirmed pair becomes `Matched` and a rejected one is suppressed to `Unmatched`, so neither returns to
`NeedsReview` and there is no un-confirm / un-reject endpoint yet (a clear/reset path is a possible
future addition).

Confirm/Reject validate the submitted `{coveId, whisparrMovieId}` pair against the freshly computed
diff before writing: a forged pair that is not a current needs-review suggestion is refused with
`MATCH_NOT_IN_DIFF`, so a caller cannot write an arbitrary link into the store.

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
`monitored: false` if absent (applying the configured root folder and quality profile plus a
read-or-create `cove-sync` origin tag), then PUTs the target monitored state. Creating an entity that
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

Scene status answers, for every scene, *what is its Whisparr state?* — one of **downloaded /
monitored / unmonitored / notAdded / excluded**. It is read-only and opt-in; nothing is mutated and
no StashDB call is made.

`SceneStatusProjector` derives the state from data the extension already has: the reconciliation movie
set (the same batch Whisparr movie lookup reconciliation uses) plus one read of Whisparr's exclusion
list (`GET /api/v3/exclusions`, v3 only — v2 defers exclusions and degrades to `excluded = 0`).
`ClassifyMovie` is **movie-centric**: a reconciliation row is always a present Whisparr movie, so a
filed movie is `downloaded` even without a queryable StashDB id, and it is never `notAdded`. Exclusion
is checked from the exclusion set keyed on the scene's StashDB id. **No StashDB call is made** — the
whole derivation is Whisparr-and-Cove-only.

Three read-only, `configure`-gated, stored-creds-only endpoints back the surfaces:

- `GET /scene-status-summary` — the library-wide 4-state counts (the toolbar summary).
- `POST /scene-detail {coveId}` — Whisparr-owned facts for one scene (state, monitored, quality,
  cutoff), resolved server-side from the Cove id. A scene with no StashDB id is a handled 200
  (`NO_STASHDB_IDENTITY`) with zero outbound calls.
- `POST /scene-releases {coveId}` — the indexer release count, fetched **only** on explicit UI expand
  (one scene at a time), never in the summary/detail path.

Two further reconciliation-clarity reads are `read`-gated (`extensions.read`) rather than
`configure`-gated, because each composes inputs already computed and never grabs:

- `POST /scene-lifecycle {coveId}` — the tri-state Lifecycle projection (Catalog → Whisparr → Cove
  Library) for the scene detail-rail widget. It composes the scene's provider-id presence, its
  reconciliation match, and the Whisparr `SceneStatusProjector.Detail` in one call — **no new StashDB
  call**. A monitored-but-fileless scene degrades honestly to a bare `Monitored` stage with no invented
  quality/cutoff. The `Downloading` in-flight stage is deferred (no Whisparr queue read this release).
- `GET /identity-health` — the library-wide `{ totalScenes, unidentifiedScenes }` count for the
  guided-setup banner, where unidentified means no provider id on the connected version's endpoint. A
  pure Cove read run under `CovePrincipal.System()` so the total is the whole library (a non-System
  read would undercount — an Anonymous principal returns zero rows).

Every `NO_STASHDB_IDENTITY` response now also carries a `provider` field derived from the connected
version (`StashDB` on v3, `ThePornDB` on v2) so the UI names the provider without hardcoding it.

The `SceneWhisparrState` enum's wire casing is **pinned to camelCase** by a property-level
`[JsonConverter]` on `SceneDetail.State` and `ReconRow.WhisparrState` — a property attribute out-ranks
the plain `JsonStringEnumConverter` the response options register, which would otherwise emit
PascalCase. The frontend logic (`sceneStatusLogic.ts`, `reconciliationLogic.ts`) keys its label maps on
those exact camelCase strings, so any drift fails the offline gate rather than silently blanking rows.

### Where status shows (cards, tab, reconciliation)

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
3. **Reconciliation Whisparr column — the settings-tab per-scene list.** The reconciliation
   table lines every Whisparr scene up against the Cove library, so its Whisparr column is the full
   per-scene 4-state (incl. excluded) status list.

**Version / entity gating.** The studio badge + row register on **both** v2 and v3; the performer and
per-scene surfaces are **v3-only** (v2 has no performer entity and no scene-level id). A v2 studio
with a ThePornDB id resolves and badges normally; the v3-only endpoints return `VERSION_UNSUPPORTED`
on a v2 connection and their slots are simply not registered there.

## The outward scene & bulk mutation surface

The scene panel's controls are live and there are bulk actions, all through the five
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
  holds "Monitor in Whisparr" (the studio/performer monitor toggle — `/monitor`), "Add all missing"
  (`/bulk-add-missing`, a local Cove-vs-Whisparr diff with no StashDB GraphQL), and "Search all
  monitored" (`/bulk-search-monitored`), with the two bulk items shown only when the entity is
  monitored (quiet by default).

**Loop-safety.** Every *add* — per-scene add, add-all-missing, and owned-scene availability
registration — issues `searchForMovie:false`, so it registers the movie in Whisparr **without
grabbing**. Only the explicit "Search for this scene" / "Search all monitored" actions ever issue a
`MoviesSearch`, so only a deliberate user click can start a grab. Every mutation is origin-tagged
(`cove-sync`) and idempotent (a 409/exists is treated as success). When a grab does result from an
explicit search, it imports into Cove through the **same** On-Import webhook + polling reconcile as any
other Whisparr grab — there is no second ingest path, and that path is already idempotent
(`EventLedger`), so a Cove-initiated add can never feed a re-ingest loop.

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

A Cove **studio** maps to a v2 **site (series)**: `V2Adapter.SetEntityMonitorAsync(Studio)` is an
add-then-flip that mirrors `V3Adapter.SetStudioMonitorAsync` — resolve the site by its TPDB id
(`GET /series` matched on `tvdbId`), add the addable row (`series/lookup?term=tpdb:{id}`) **non-grabbing**
(`addOptions.monitor:"none"`, `searchForMissingEpisodes:false`) with the caller's root / profile /
`cove-sync` origin tag, then PUT the requested `monitored` state. A `400 SeriesExistsValidator`
duplicate is idempotent success. Status is grabbed-of-total over the site's episodes, "search all
monitored" posts `EpisodeSearch` (the one grab-capable v2 verb), and only that explicit search grabs.
`V2OutwardParityTests` proves both the GO flows and every defer.

The capabilities with no v2 analog DEFER on v2 — a classified `VersionMismatch("v2")` **before the
transport** (zero wire calls, no stray `cove-sync` tag; the seam reads the adapter's `SupportsSceneAdd`
/ `SupportsEntityMonitor(kind)` capability flags to defer before resolving root/origin-tag):

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

Auto-register posts a v3 Notification (`implementation: "Webhook"`, `configContract:
"WebhookSettings"`) to `POST /api/v3/notification`. It is **best-effort**: a non-2xx response (or a
refused version) returns `registered: false` and the UI falls back to copy-paste — the connect flow
never fails on it. The exact `fields` contract is confirmed against a live instance; the copy-paste
URL is the guaranteed path regardless.

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
  for the settings-page **Import activity** section.

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
`RootOverlapDetector` compares the Whisparr roots (`GET /api/v3/rootfolder`) against the Cove library
roots (`CoveConfiguration.CovePaths` when the host injects it, otherwise the distinct folders of the
library's own files) using the same separator-normalized, case-sensitive, segment-bounded containment as
the ingest guard, in **both** directions. `GET /root-overlap` surfaces the result. It is a
**best-effort advisory, never a hard gate**: cross-mount or containerized deployments legitimately see
the same library at different paths, so a non-overlap is not a guarantee and an overlap is not an
error — the warning says as much.

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
