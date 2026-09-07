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
| `SceneStatus/SceneStatusProjector` | Derives each scene's read-only 4-state Whisparr status from the reconciliation movie set + one exclusion read — **no StashDB call**. | `SceneStatus/` |
| `Matching/IdentityMatcher` | Zero-mutation reconciliation, reduced to its two predicates: a single id-only match (StashDB id on v3, ThePornDB id on v2). **Nothing is persisted** — there is no match store, and each surface derives what it needs per read. A durable movie-id → Cove-id map is the wrong shape (one entry per movie becomes one per library scene once a library sync runs); the bounded form of that handle is a remote-id lookup at event time. A caller resolving an id must consider EVERY video carrying it — picking one arbitrarily reports a scene Cove owns as missing. | `Matching/` |
| `Ingest/WebhookReceiver` + `IngestCoordinator` + `WhisparrRootGuard` | Turns a Whisparr On-Import into a Cove item **in place**; the root guard is fail-closed and the coordinator holds no relocation primitive. | `Ingest/` |
| `Matching/ReconcileJob` + `ReconcileScheduler` | The `PeriodicTimer` polling backstop: an exclusive `IJobService` reconcile every 15 min over `GET /history` since a stored checkpoint. | `Matching/` |
| `State/EventLedger` + `ImportLog` + `Checkpoint` | The cross-channel idempotency key (`SHA-256(downloadId &#124; NormalizePath(path))`), the bounded import status record, and the poll checkpoint — all single-blob over `IExtensionStore`, and all four keys (with `health`) bounded by construction. Nothing persisted may grow with the library. | `State/` |
| `Library/ICoveLibraryPort.StreamAllVideosAsync` | The keyset-paged seam every library-wide fold reads through, so an aggregate over millions of files holds one page rather than the library. It is the ONLY whole-library read the port offers; the materializing sibling is gone, so nothing can hold the library by mistake. | `Library/` |
| `Options/WhisparrOptions` + `OptionsStore` | One JSON blob over `IExtensionStore`; a corrupt/absent blob loads as safe defaults. The API key + webhook secret live here, server-side only. | `Options/` |
| `Safety/RootOverlapDetector` + `Safety/SceneFolderOverlapDetector` + `Safety/WhisparrRootsPort`, `Ingest/WebhookUrlBuilder` | The re-grab-loop advisory, and the **one seam every Whisparr root read goes through** — its cache, refill gate, fail-closed rule and failure classification. A new consumer takes the port; it never adds a second root read. The advisory answers `FolderOverlapResponse(Checked, Reason, Findings, NotApplicable)`: `Checked` is the discriminator, so an empty `Findings` under `Checked: true` is a genuine all-clear while `Checked: false` carries one of four `FolderOverlapReason` values naming why nothing was compared, and `NotApplicable` lists the finding kinds *this connection* cannot answer at all — never an error and never a prompt to change generation. Two finding kinds: `rootContainment` (both generations) and `sceneFolderFormat` (Eros only). The handler returns facts; every sentence is composed client-side. Webhook-secret mint + URL builder. | `Safety/`, `Ingest/` |

The minimal-API surface is mounted under `/api/extensions/com.alextomas955.whisparrsync/`. Each route
**declares its access tier at registration** (`RouteGates.cs` — `ReadGated()` / `ConfigureGated()` /
`TokenGated()`, thin names over the host's `RequireCovePermission` / `AllowCoveAnonymous` conventions)
and Cove enforces it in middleware before the extension scope exists. **No handler re-checks the
principal** — one gate per route, and never inside the handler. Side-effect-free read projections are
read-gated; any route reaching the stored credentials or making an outbound call is configure-gated. The
one exception is the inbound `/webhook`, which carries no Cove principal — its shared-secret token is
the auth, declared as `TokenGated()` rather than left undeclared (an undeclared extension route is
anonymous for backward compatibility, so silence is the dangerous state). `RouteAccessTierTests` reads
the registered route table and fails on an undeclared route, a drifted route set, or a tier that does not
match the capability. Because enforcement is host middleware, in-process tests can prove only what a
route *declares*; the denial itself is proven on the containerized e2e leg. See `docs/ARCHITECTURE.md`
for the full endpoint table and the reconciliation match model.

The in-handler `Forbidden(principal, …)` helper in `Cove.Extensions.Shared` is **not** the default and is
not used here. It survives for the one shape a route convention cannot express — a permission chosen from
the request body, which is Renamer's case (video vs image permissions per submitted entity kind).

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
`VersionMismatch("v2")` **before the transport** (zero wire calls, no stray `cove-sync` tag):
capability is expressed by **presence of a role interface** (`adapter is IWhisparrScenePush`), which a
v2 instance structurally lacks, and the orchestration seam reads that to defer before resolving
root/origin-tag. There is no `Supports*` probe.

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
available on Whisparr v3 (Eros)"** (single-sourced in `common/lib/whisparrCopy.ts` as
`VERSION_CAPABILITY_COPY`, re-exported by `monitorLogic.ts`) — never
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
**Whisparr tab** (`AddTab("video")`, `WhisparrScenePanel`). There is no reconciliation table and no
Whisparr column on one — both went with the endpoints behind them.

Version and entity gating of these surfaces is a real contract, not incidental, and it does not split on
entity alone — read the manifest, not the entity name. Registered on **both** generations: the studio
badge + row, the studio AND performer detail control and status line, and all three per-entity Missing
tabs. **Omitted on v2**: the per-scene surfaces, the videos and performers library affordances, and both
bulk actions — v2 has no scene-level id and no performer entity to monitor. So the performer *detail*
control renders on v2 and refuses there with the capability copy; its quiet state is a client-side
decision, not a manifest omission. A v2 studio with a ThePornDB id resolves and badges normally.

Enum wire casing is a hard contract: `SceneWhisparrState` is pinned to **camelCase** by its
type-level `[JsonConverter]`, which holds on every response options object (`WireSerializers` keeps
two: a plain-Web one and one adding camelCase enum strings — the same policy, so the two agree). The
frontend `*Logic.ts` label maps key on those exact strings — drift fails an offline gate rather than
silently blanking rows, and `WireEnumCasingTests` asserts every enum's literal wire value.
`MonitorScope` is deliberately NOT wire-facing: it is an inbound request value (parsed from
`req.Scope`) and a persisted option, so the only casing pinned for it is the PascalCase blob spelling
an existing stored value must keep loading.

## Shared modules it consumes

- **`Cove.Extensions.Shared`** (`shared/Cove.Extensions.Shared/`) — the generic options store and JSON
  factory, referenced as a `ProjectReference`. It ships **bundled** as `Cove.Extensions.Shared.dll` (it
  is first-party, so **not** `Private=false` and absent from the host-assembly strip denylist — it
  survives the strip). No direct Cove reference and no `System.IO.Hashing` here (unlike Renamer, this
  extension hashes nothing). Its in-handler permission gate is NOT used here — see the route-tier
  declaration above.
- **`@cove-extensions/ui-shared`** (`shared/cove-extensions-ui/`) — shared field primitives + their pure
  logic, resolved from **raw TS source** through a Vite `resolve.alias` + tsconfig path (never a
  node_modules install), so Vite transforms it through the same pipeline as the bundle's own source.
- **`@cove/runtime/components`** + **`@cove/runtime/api`** — the host's own React surface, served
  through Cove's extension import map and kept external at build time. `DetailListPagination` (the
  canonical list paging control) and `ConfirmDialog` (the confirmation modal, replacing a
  `window.confirm`) come from the barrel; `extensionFetch` is the authenticated fetch that
  `common/lib/coveApi.ts` — the ONE transport this bundle uses — wraps. Types live in
  `shared/cove-extensions-ui/types/coveRuntime.d.ts` so the UI typechecks with no `../cove` sibling.
- **`@cove/extension-sdk`** — the Cove *frontend* host SDK, vendored as a committed `file:` tarball
  under `src/WhisparrSync.Ui/vendor/` (not published to npm; `npm ci` installs it offline). Still the
  home of `defineExtension`, `EntityTabProps` and `ApiError`; its cookie-only `request()` is not used —
  `coveApi.ts` re-exports its `ApiError` so there is exactly one error type.

## Structure & patterns (WhisparrSync-specific)

The monorepo shape rules — six-kind taxonomy, no `Features/` wrapper, capability-not-entity slicing,
suffix-as-kind, two-level shared (`common/` for extension-local), all-camelCase wire + `contracts.ts`,
correctness + testing + tooling gates — live in the root `CLAUDE.md` under **Extension authoring
patterns** and apply here. WhisparrSync specifics (the target shape the refactor waves converge on):

- **Capability slices at the project root, no `Features/` wrapper.** `Activity/ · Discovery/ ·
  Ingest/ · Matching/ · Monitor/ · Push/ · SceneStatus/` sit beside the foundation folders
  (`Adapters/ · Caching/ · Client/ · Contracts/ · Library/ · Options/ · Safety/ · State/`). The entry
  point is `WhisparrSync.cs` plus its per-concern partials (`WhisparrSync.Api.cs`, `.Batch.cs`,
  `.Connection.cs`, `.Logging.cs`, `.SyncLibrary.cs`). `Reconcile/` folds into `Matching/`; `Webhook/`
  into `Ingest/`.
- **Capability naming, not entity naming.** The scene-status projection is `SceneStatus/`, never bare
  `Scene/`. There is no `Studio/`/`Performer/` folder — studio + performer monitor ops live in
  `Monitor/`, scene add/monitor in `Push/`.
- **Adapters split by ROLE, capability-by-presence.** Fifteen role interfaces live in
  `Adapters/roles/`. `IWhisparrAdapter` is no longer a fat interface — it is a composition marker over
  the seven both generations honor (`IWhisparrConnection`, `IWhisparrReconcileSource`,
  `IWhisparrActivitySource`, `IWhisparrWebhookAdmin`, `IWhisparrOwnedImport`, `IWhisparrSceneSearch`,
  `IWhisparrStudioMonitor`). `IWhisparrEntityCatalogue` is also shared but
  implemented directly by each adapter rather than composed in. Seven are v3-only, so `V3Adapter` alone
  declares them:
  `IWhisparrPerformerMonitor`, `IWhisparrScenePush`, `IWhisparrSceneLookup`, `IWhisparrStatusIndexSource`,
  `IWhisparrExclusions`, `IWhisparrReleaseGrab`, `IWhisparrFileSettings` — the monitor role is the ONE entity
  sub-split (studio vs performer). v2 simply doesn't implement what it can't honor: no probe, no
  `VersionMismatch` throw, no stray `cove-sync` tag before a defer.
- **The WHISPARR side is bounded per generation, and the newer one's bound is a constant factor.** The
  toolbar summary reads `/movie` with `HttpCompletionOption.ResponseHeadersRead` and folds each row into its
  status index as it arrives, binding the five-member `WhisparrMovieFacts` instead of the full row and
  sending `excludeLocalCovers=true` (measured honoured; applied to THIS read only, because the discovery
  projection falls back to the cached cover path a row may be the only carrier of). The classifier keys both
  shapes through one rule — `IWhisparrMovieFacts` + `SceneStatusProjector.IndexMovie` — so an omitted member
  is a compile error rather than a silently lost key. Headers-only completion means the transport stops
  governing the body, so the body read takes a token that is NOT the send budget's and is bounded by
  `IdleReadTimeoutStream` (a per-read stall budget, ~30 s); raising `CallTimeout` instead would fail an
  honest large transfer to catch a dead one. A mid-transfer failure arrives as an `IOException`-derived type
  — `HttpIOException` does NOT derive from `HttpRequestException` — so the send loop catches `IOException`
  too, or it escapes the classify-not-throw boundary as a 500. The fold's seed is a `Func` re-invoked per
  attempt, because the retry loop re-invokes the request factory. The read is still WHOLE-SET and
  `WhisparrRequestCounter` still classifies it as one: the payload narrowed, the scope did not.
  `ListMoviesAsync` is RE-SCOPED, never deleted — v2 has no `/movie` entity at all, and four v3 sites still
  need full rows (see `docs/ARCHITECTURE.md` § *What the Whisparr read costs, per generation*). The residual
  is O(Whisparr movie set) at ~650–790 retained B/entry against ~3 230–3 390 for the full rows; the bounded
  Cove-side read that would remove it answers id-set MEMBERSHIP while the summary counts VIDEOS, and the two
  diverge in both directions.
- **The older generation does not answer the toolbar summary at all, and that is the point rather than a gap.**
  A synthesized v2 row carries `StashId = null` and `ItemType = "v2scene"`, so it keys under nothing: the status
  index v2 can build is EMPTY for a library of any size, and the four states partition a library, so the row it
  used to paint read Monitored 0 · Unmonitored 0 · Not added N · Excluded 0 · In library 0 for every library —
  with `notAdded` meaning "Whisparr does not have the scene", the single conclusion those counts cannot support.
  So `IWhisparrStatusIndexSource` is **v3-only**, `SceneStatusSummaryAsync` narrows to it and answers
  `VERSION_UNSUPPORTED` before the transport, and the videos slots are already omitted from the v2 manifest — a
  refusal costs zero wire calls and paints nothing. Keying v2 rows on the TPDB id they DO carry would let v2
  answer and would move the counts a user sees, so that is a capability decision with its own verification, not
  a cleanup: a TPDB id compared against a StashDB id is the cross-match the keying rule exists to prevent.
  `V2Adapter` still exposes ONE internal site walk — series once, then episodes and episode files per site — and
  both `ListMoviesAsync` and `LoadStatusIndexAsync` are folds over it, so `1 + 2N` sequential requests in that
  order is structural rather than lucky and the two cannot synthesize different rows. The per-site fold survives
  UNREACHED by any shipped path here, labelled as such: it is the shape an index read would take if v2's rows
  ever keyed, and what it measures — a per-site walk keeps one site rather than the library — is a property of
  the shared walk `ListMoviesAsync`'s three callers do reach.
- **Capability by the instance's own document, not by a probe.** Presence-of-role is the rule for a
  GENERATION gap; a BUILD gap is decided the same way but from a different source — `WhisparrCapabilityPort`
  reads `/docs/v3/openapi.json` once per connection (memoized beside the list caches, version-keyed) and
  `AdapterSelector`'s capability-aware overload yields `V3EntityCatalogueAdapter` only when the document
  declares both `listBy*ForeignId` routes. The route keys come from a committed live answer
  (`e2e/fixtures/wire/openapi-capability.json`), the unit decision reads that same file, and a document that
  does not answer resolves ABSENT. The version-only `SelectForVersion` overload can never reach the role,
  which is the property rather than an omission. A refusal is `CAPABILITY_UNAVAILABLE`, deliberately distinct
  from `VERSION_UNSUPPORTED`. Never widen a read on absence.
- **`State/` journals are bounded.** The `EventLedger` is NOT the loop guard (that is
  `searchForMovie:false` + origin-tag + 409-as-success) and NOT the duplicate-entity guard (a host
  unique `(ParentFolderId, Basename)` index is) — so bound it: prune `hist:` ≤ checkpoint, cap
  `ImportKey` by window. `ImportLog` (nothing renders its per-attempt journal) reduces to a tiny bounded
  `ImportStatus` record (`LastWebhookEventTicks`, `LastSuccessTicks`, an exact `UnresolvedPathMismatch`
  count, and a `RecentFailures` sample ring capped at `MaxRecentFailures` = 16 — only the sample set is
  capped, never the rendered count). Survivors ride the thin, opt-in
  `SingleWriterBlobStore<T>` base. The `matchstate` blob — the one key with no cap and no `Compact` hook
  — is **deleted**, not capped: a cap on a match map silently forgets matches. Adding a fifth key means
  proving it bounded first.
- **Nothing PERSISTED or MATERIALIZED is O(library) — the qualified claim is the ledger in
  `docs/ARCHITECTURE.md` § *What the Whisparr read costs, per generation*, and that ledger is the wording to
  use.** Four things it does NOT claim, each measured: the toolbar summary is a constant-factor win of about
  five and still O(Whisparr movie set) in BOTH columns; the older generation has no narrow movie endpoint at
  all, so the ceiling is stated per generation; the sync's entity fan-out still scales with entity count; and
  the bulk add path costs a fixed number of extra requests PER SCENE, which is O(library) in requests across
  a full sync. Do not restate any of these as O(1). Library-wide folds read through
  `StreamAllVideosAsync` / `StreamFilePathsAsync` — the only whole-library reads the port offers, since the
  materializing sibling is gone; a question about a catalogue page is answered for that
  page (`LoadOwnedRemoteIdsAsync`), never by reading the owned library; the acquisition worklist keys the
  Whisparr side and streams the Cove side past it. See `docs/ARCHITECTURE.md` § *Bounded by design*.
  The cove-core costs — `RunBatchAsync` materializing its unit list and starting every unit's state
  machine at once (measured ~556 B retained per parked unit), and `RecalculateUnitProgress` making four
  full passes over the job's whole unit dictionary under the job lock on every `StartUnit` / `Report` /
  `Complete` — are unchanged in cove and are met from THIS side, by handing the host fewer units and
  reporting less often. A library sync's SCENE fan-out is `PlanSceneSlices` → at most `MaxSceneSlices`
  (64) half-open `(After, UpTo]` id ranges of `MinSliceScenes` (500, one keyset page) or more, planned
  from a `COUNT(*)` plus one id-column pass retaining ≤63 ints, with every planned `SyncUnitId`
  pre-registered before the single `maxInFlight: 1` `RunBatchAsync` so the drawer's denominator is right
  from the first report (`StartUnit` is idempotent per id, so the batch's own call reuses each state).
  Clamp the COUNT, never fix the slice SIZE — a fixed size still grows with the library. A slice reports
  at most once per `CoveLibraryPort.StreamPageSize`, because `IJobUnit.Report` triggers the same lock-held
  recount; per-scene reporting reintroduces exactly what slicing removes. **Still O(entities), by
  design:** one unit per studio and per performer, each a distinct outbound op with its own outcome —
  do not claim the fan-out is O(1). **Accepted trade:** the host composes the end-of-run summary from
  unit counts with no override seam, so that line counts SLICES; `LogSyncLibrary` must therefore be fed
  the accumulated per-scene tally (`SyncTally`), never `batch.*`, or the mitigation is false.
- **UI slices directly under `src/`; `common/` for extension-local shared.** `WhisparrLogo.tsx` →
  `common/ui/` (Whisparr-branded, so NOT repo-level `shared/`); stores unify on `resourceEntryLogic`.
- **The activity sub-page's groupings are declared once, in a descriptor table.** `ACTIVITY_SECTIONS`
  in `wanted/activityLogic.ts` is the one place a grouping is declared — three today, in tab order:
  `wanted · queue · history`. It carries each grouping's key, label, badge rule, route and empty copy;
  `ActivityTab`, the tab order, the section store's Map and the page render are all derived from it.
  Adding a grouping is one table entry plus one `ROW_VIEWS` arm (and its `ActivityRowByKey` entry) —
  both records are keyed on `ActivityTab`, so a missing arm is a typecheck failure, not a blank tab.
  No `active === "…"` comparison, no tab-bar edit, no store edit.
- **Wire: one `contracts.ts`** (was 3× duplicated unions) + a `Contracts/` unit; enum casing
  (`SceneWhisparrState` camelCase) is a hard offline-gated contract. Tests mirror the source slices
  (`Adapters/ · Ingest/ · Matching/ · Monitor/ · Push/ · SceneStatus/ · …`), with `TestSupport/`, the
  `Api/` and `Batch/` endpoint groups, and the single-file `Api/TransportSmokeTests.cs` transport smoke
  as the test-only exceptions.

## Tests & e2e

- `src/WhisparrSync.Tests/` — xUnit. The outbound HTTP boundary is faked with
  `FakeHttpMessageHandler`, so the client and adapters are testable with no live Whisparr;
  `CoveContextFactory` + the `Fake*` ports give zero-DB unit tests. `V2LiveE2ETests` are skippable
  live probes (they no-op without a reachable v2). The safety contracts live in `Safety/`
  (`NoMutationTests`, `RootOverlapDetectorTests`, `RootReadSeamTests`, `WhisparrRootsPortTests`,
  `FolderOverlapEndpointTests`) and `Adapters/V2OutwardParityTests`.
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
