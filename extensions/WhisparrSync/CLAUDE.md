## Project

A Cove extension (**Whisparr Sync**, `com.alextomas955.whisparrsync`) — a C# class library that
connects a self-hosted Cove media-library instance to a [Whisparr](https://whisparr.com) v3 (Eros)
or v2 instance. You enter a Whisparr URL + API key on a settings page, test the connection,
auto-import what Whisparr grabs (a webhook plus a polling reconcile backstop), reconcile what
Whisparr tracks against your Cove library, and push to Whisparr — monitor a studio / performer /
scene and add or search in bulk.

**Core Value:** Cove and Whisparr stay in agreement — what Whisparr acquires shows up correctly in
Cove and Cove reflects what it already has — with **near-zero setup friction** and **no grab/import
feedback loop**. If everything else is cut, a safe, loop-free auto-import that never touches
Whisparr's files is the thing that must work.

> The monorepo-wide rules — the extension-authoring contract, build wiring and Cove source
> selection, Central Package Management, the bans on bundling host assemblies and writing to the DB
> directly, the C# and TypeScript comment / doc policy, and documentation upkeep — live in the
> repo-root `CLAUDE.md` and apply here too. This file adds only what is specific to Whisparr Sync.

## Architecture (Whisparr-Sync-specific)

The backend is a `FullExtensionBase` split across partials (`WhisparrSync.cs` / `WhisparrSync.Api.cs`
/ `WhisparrSync.Logging.cs`). Everything crossing to Whisparr goes through one transport client and
one version-adapter seam; the handlers hold no wire knowledge.

| Layer | Responsibility | Location |
| --- | --- | --- |
| `Client/WhisparrClient` | Transport only: `X-Api-Key` header, per-call timeout, status-/content-type guard, retry on idempotent GETs. Classifies every call into a typed `WhisparrResult<T>` (ok / badKey / unreachable / notWhisparr) instead of throwing. | `Client/` |
| `Adapters/IWhisparrAdapter` + `V3Adapter` / `V2Adapter` | The version-adapter boundary — each adapter owns its version's endpoint paths and payload shapes. `AdapterSelector` maps `3 → V3Adapter`, `2 → V2Adapter`, and **refuses** any other version (never a silent wrong-adapter call). | `Adapters/` |
| `Push/SceneActions` + `Monitor/EntityMonitor` | The outward mutation spine: register / monitor / search a scene, monitor a studio/performer, and the bulk variants. | `Push/`, `Monitor/` |
| `Scene/SceneStatusProjector` | Derives each scene's read-only 4-state Whisparr status from the reconciliation movie set + one exclusion read — **no StashDB call**. | `Scene/` |
| `Matching/IdentityMatcher` + `MatchStateStore` + `ReconciliationService` | Zero-mutation reconciliation: a single id-only match (StashDB id on v3, ThePornDB id on v2) and the Confirm/Reject match store (its only writes). | `Matching/` |
| `Ingest/WebhookReceiver` + `IngestCoordinator` + `WhisparrRootGuard` | Turns a Whisparr On-Import into a Cove item **in place**; the root guard is fail-closed and the coordinator holds no relocation primitive. | `Ingest/` |
| `Reconcile/ReconcileJob` + `ReconcileScheduler` | The `PeriodicTimer` polling backstop: an exclusive `IJobService` reconcile every 15 min over `GET /history` since a stored checkpoint. | `Reconcile/` |
| `State/EventLedger` + `ImportLog` + `Checkpoint` | The cross-channel idempotency key (`SHA-256(downloadId | NormalizePath(path))`), the append-only import audit journal, and the poll checkpoint — all single-blob over `IExtensionStore`. | `State/` |
| `Options/WhisparrOptions` + `OptionsStore` | One JSON blob over `IExtensionStore`; a corrupt/absent blob loads as safe defaults. The API key + webhook secret live here, server-side only. | `Options/` |
| `Safety/RootOverlapDetector`, `Webhook/WebhookUrlBuilder` | Best-effort re-grab-loop advisory; webhook-secret mint + URL builder. | `Safety/`, `Webhook/` |

The minimal-API surface is mounted under `/api/extensions/com.alextomas955.whisparrsync/` and is
permission-checked **in the handler** — the host's `[RequiresPermission]` filter is inert on
minimal-API routes. Side-effect-free read projections gate on `extensions.read`; any route reaching
the stored credentials or making an outbound call gates on `extensions.configure`. The one exception
is the inbound `/webhook` route, which carries no Cove principal — a shared-secret token is its auth.
See `docs/ARCHITECTURE.md` for the full endpoint table and the reconciliation match model.

## Loop-safety invariants (must not regress)

Auto-import means Cove and Whisparr act on the same files, so these invariants are the reason a
Cove-initiated action can never start a grab/import feedback loop. Treat them as safety-critical —
they earn comments, and `V2OutwardParityTests` / `NoMutationTests` are the contract tests that guard
them.

- **Every *add* issues `searchForMovie:false`** — per-scene add, add-all-missing, and owned-scene
  availability registration all register the movie in Whisparr **without grabbing**.
- **Only an explicit user "Search" grabs.** The single grab-capable actions are "Search now" /
  "Search all monitored" / "Search for upgrades" (`MoviesSearch`) and the interactive "Grab this
  release". Turning monitoring on never triggers a search.
- **Every mutation is origin-tagged `cove-sync`** (read-or-created via `GET`/`POST /tag`) and
  **idempotent** — a 409/exists is treated as **success**, not a duplicate.
- **No second ingest path.** Any grab that results imports back through the *same* On-Import webhook
  + polling reconcile, which is already idempotent (`EventLedger`), so a Cove-initiated add cannot
  feed a re-ingest loop.
- **Never move or delete inside a Whisparr root.** `IngestCoordinator` imports in place and holds no
  filesystem-relocation API at all; `NoMutationTests` fails if a `File.Move`/`Delete`/`Directory.*`
  call is ever added to the coordinator source.

## Version adapter: the outward surface on v3 and v2

Import, reconciliation, and the read-only status views work on **both v3 and v2**. The **outward**
surface — monitor / add / search / bulk — works on **both**, keyed on the identity each version
actually carries: **v3 resolves by StashDB id, v2 by ThePornDB (TPDB) id**. The server picks the
right remote id from the entity's own Cove remote ids by matching the connected version's endpoint
(`WhisparrOptions.IdentityEndpoint`: `StashDbEndpoint` on v3, `TpdbEndpoint` on v2), so no caller ever
supplies a bare id.

v2 is Sonarr-shaped: a **site = series**, a **scene = episode**, keyed on TPDB. A Cove studio monitors
as a v2 SITE — add-then-flip the series resolved by its TPDB id (non-grabbing via
`addOptions.searchForMissingEpisodes:false`, origin-tagged, idempotent), its status is
grabbed-of-total over the site's episodes, and "search all monitored" issues the episode search (the
one grab-capable v2 verb). This mirrors `V3Adapter`'s studio path; `V2OutwardParityTests` proves the
GO flows and every DEFER refusal.

The capabilities with no v2 analog still DEFER on v2, each with a real reason — a classified
`VersionMismatch("v2")` **before the transport** (zero wire calls, no stray `cove-sync` tag): the
adapter declares its capability via `SupportsSceneAdd` / `SupportsEntityMonitor(kind)` and the
orchestration seam reads it to defer before resolving root/origin-tag.

- **Monitor a performer** — v2 has no performer entity; performers are embedded `episode.actors`
  metadata, nothing monitorable.
- **Add / monitor a single scene** — v2 has no `POST /episode`; a scene is acquired by adding its site
  and searching the episode, so there is no independent per-scene add.
- **Grab quality upgrades** — Sonarr has no cutoff-upgrade-only search variant; v2 keeps ONE grab verb
  (the episode search) by design.
- **Exclusions, interactive release grab, and the per-scene status views** — v2 exclusions and
  releases are TPDB-keyed and cannot be tied back to a Cove scene without a scene-level id, so these
  stay v3-only this release.

The UI reflects this: a v2 studio with a TPDB id shows the controls **enabled**; where the connected
version does not offer a capability for the entity the control is disabled reading **"Currently
available on Whisparr v3 (Eros)"** (single-sourced in `monitorLogic.VERSION_CAPABILITY_COPY`) — never
wording that implies the user must migrate. v2 and v3 are both first-class.

Reconciliation is a separate concern: v2 scene rows are still synthesized with `StashId = null` and
`ItemType = "v2scene"` (never `"scene"`), so a v2 row's StashDB match no-ops by design — it matches via
the ThePornDB id instead, the same id-only rule as v3, just keyed on a different id. There is no
separate bridge to build; outward push and reconciliation both key on TPDB for v2.

## Frontend UI-slot contract + the card-status surfaces

The UI (`src/WhisparrSync.Ui/`, React 19 + TypeScript → `dist/index.mjs`) rides Cove's **native**
slots. Two host-contract quirks the code cannot show, so they earn a comment where relied on:

- **Slot components read their entity from top-level props** (`props.studio` / `props.performer` /
  the video context) per Cove's slot contract — **never** `props.context.*`.
- **Component-map keys and the `whisparrBatchSelected` action-handler key MUST be byte-identical to
  the C# manifest `componentName` / `HandlerName`** (`WhisparrSync.Api.cs`) — one literal each, in
  both places, that must agree. `defineExtension` does not type `actionHandlers`; it is attached via
  a local cast (as Renamer does), never by editing the SDK.

Whisparr status paints **directly on library cards**, gated by an off-by-default toolbar pill. The
host exposes the card slots this rides on — `video-card-content` (scene badge, `WhisparrCardBadge`),
`studio-card-footer` / `performer-card-footer` (the "Monitored · present/catalog" entity badge,
`WhisparrEntityCardBadge`), and the full-width `*-list-row` below each list toolbar — and contains
each slot so a misbehaving extension cannot break a card. `OverrideComponent("video.card")` and
`actionType:"context-menu"` remain silent host no-ops, so the badge renders in the card CONTENT area
(not by replacing the card). The pill (`WhisparrLibraryToggle` on the `*-list-toolbar-end` slots)
gates BOTH the card badges and the count row, sharing on/off state through `libraryToggleStore`, so
the cards stay clean until the user opts in. Scene status also surfaces on the scene detail
**Whisparr tab** (`AddTab("video")`, `WhisparrScenePanel`) and the **reconciliation Whisparr column**.

Version and entity gating of these surfaces is a real contract, not incidental: the studio badge +
row register on **both** v2 and v3; performer and per-scene surfaces are **v3-only** (registered in
the v3 block), because v2 has no performer entity and no scene-level id. A v2 studio with a ThePornDB
id resolves and badges normally.

Enum wire casing is a hard contract: `SceneWhisparrState` is pinned to **camelCase** by a
property-level `[JsonConverter]`, and the frontend `*Logic.ts` label maps key on those exact strings
— drift fails an offline gate rather than silently blanking rows.

## Shared modules it consumes

- **`Cove.Extensions.Shared`** (`shared/Cove.Extensions.Shared/`) — the generic options store,
  minimal-API permission gate, and JSON factory, referenced as a `ProjectReference`. It ships
  **bundled** as `Cove.Extensions.Shared.dll` (it is first-party, so **not** `Private=false` and
  absent from the host-assembly strip denylist — it survives the strip). No direct Cove reference
  and no `System.IO.Hashing` here (unlike Renamer, this extension hashes nothing).
- **`@cove-ext/ui-shared`** (`shared/cove-extensions-ui/`) — shared field primitives + their pure
  logic, resolved from **raw TS source** through a Vite `resolve.alias` + tsconfig path (never a
  node_modules install), so Vite transforms it through the same pipeline as the bundle's own source.
- **`@cove/extension-sdk`** — the Cove *frontend* host SDK, vendored as a committed `file:` tarball
  under `src/WhisparrSync.Ui/vendor/` (not published to npm; `npm ci` installs it offline).

## Tests & e2e

- `src/WhisparrSync.Tests/` — xUnit. The outbound HTTP boundary is faked with
  `FakeHttpMessageHandler`, so the client and adapters are testable with no live Whisparr;
  `CoveContextFactory` + the `Fake*` ports give zero-DB unit tests. `V2LiveE2ETests` are skippable
  live probes (they no-op without a reachable v2). The safety contracts live in `Safety/`
  (`NoMutationTests`, `RootOverlapTests`) and `Adapters/V2OutwardParityTests`.
- `src/WhisparrSync.Ui/` — the frontend gate is `npm run verify` (typecheck + lint + format:check +
  the `check-classes` host-JIT-Tailwind/XSS guard + the offline `*Logic.ts` contract checks + the
  Vite build). Each `*Logic.ts` module is extracted precisely so it can be checked without a DOM.
- `e2e/` — the containerized Playwright + Testcontainers subset (Cove app image + Whisparr), run by
  the catalog-driven CI e2e job.

## Comments — where they are earned in Whisparr Sync

The monorepo comment / doc policy (root `CLAUDE.md`) applies. Whisparr Sync's value is a *loop-free,
file-safe* sync, so the invariants that earn a comment here are the safety-critical ones: the
`searchForMovie:false` / origin-tag / 409-as-success loop-safety contract, the import-in-place
never-move-or-delete rule, the fail-closed root guard and token gate, the cross-channel ledger
idempotency key, the version-adapter identity rule (why v3 keys outward on StashDB and v2 on TPDB, why
the StashDB *match* leg still no-ops on v2, and why each no-analog capability defers), and the host
UI-slot quirks (top-level-props, the pill-gated card slots + their version/entity gating, the pinned
enum casing). Comment those; leave the obvious code uncommented.
