# Whisparr Sync

A Cove extension (`com.alextomas955.whisparrsync`) that connects Cove to a
[Whisparr](https://whisparr.com) v3 (Eros) or v2 instance. You enter your Whisparr URL and API key on
a settings page, test the connection, and Cove reads the instance's version and name back over a real
call to Whisparr's `GET /api/v3/system/status`. Your API key is stored server-side only — it is
never returned to the browser and never written to logs.

> **Status:** 0.1.0. You enter a URL + key, test the connection, pick a root folder and quality
> profile from auto-populated dropdowns, and generate (and optionally auto-register) a webhook URL —
> plus a **reconciliation view**:
> compare what Whisparr tracks against your Cove library (matched / unmatched / needs-review) and
> confirm or reject low-confidence matches. It auto-imports what Whisparr grabs (a webhook plus a
> polling backstop), and lets you push to Whisparr — monitor a studio, performer, or scene, and add or
> search in bulk — with an opt-in per-scene Whisparr status view.

After a successful test you pick a **root folder** and **quality profile** from lists read live from
your instance (no hand-typed paths or ids), and the page shows a ready-to-use **webhook URL** with an
embedded secret that you can copy into Whisparr or let the extension add for you. When Whisparr
finishes a grab it calls that webhook and Cove imports the new file **in place**; a periodic reconcile
every 15 minutes catches anything the webhook misses.

You can also **monitor a studio or performer** in one click from its Cove page (studios on both v3 and
v2; performers on v3): a bookmark button in the action row toggles Whisparr monitoring, and a quiet
status line shows Whisparr's own present-in-library / full-catalog scene count for that entity. See the
[Monitoring guide](https://alextomas955.github.io/cove-extensions/extensions/whisparr-sync/monitoring).

An opt-in, off-by-default **Whisparr status** view badges each library card with its state (scenes:
downloaded / monitored / not added / excluded; studios & performers: a "Monitored · present/catalog"
count), derived from the reconciliation map plus an exclusion read — no StashDB calls. One toolbar
pill reveals the badges and a library-level count row; status also appears in the scene detail
Whisparr tab and the reconciliation table's Whisparr column. See the
[Whisparr status reference](https://alextomas955.github.io/cove-extensions/extensions/whisparr-sync/status).

## Whisparr v2

Both **v3 (Eros)** and **v2** are supported. On v2 the extension connects, imports what Whisparr
acquires (webhook + polling reconcile), and reconciles by **file path and fuzzy title/year**.

The one difference in the *import/reconcile* path is match precision: Whisparr v2 is ThePornDB-native
and carries **no StashDB id** on any scene, so the authoritative StashDB-id match — the durable
identity key v3 matching leads with — cannot apply to v2. This is a permanent property of v2's data
model, not a missing feature: more v2 scenes land in unmatched / needs-review than on v3. See
[Architecture](./docs/ARCHITECTURE.md#whisparr-v2-adapter) for why.

### Outward controls on v3 and v2

The controls that *write to* Whisparr work on **both v3 and v2**, keyed on the id each version carries:
v3 resolves its Whisparr target by the entity's **StashDB** id, v2 by its **ThePornDB (TPDB)** id
(Cove stores both when the metadata source provides them). v2 is Sonarr-shaped — a **site is a
series** and a **scene is an episode** — so a Cove **studio** monitors as a v2 site: the extension adds
the matching series (found by its TPDB id) without grabbing, tags it as Cove-originated, and flips it
monitored; "Search all monitored" runs the episode search. This mirrors the v3 studio path exactly and
never starts a download loop.

A few controls have no v2 counterpart and stay v3-only for now. On a v2 connection each is disabled
reading **"Currently available on Whisparr v3 (Eros)"** — never a silent no-op, and never wording that
implies you must migrate (v2 and v3 are both first-class). The extension refuses each cleanly
(`VersionMismatch("v2")`, no wire call).

| Outward control | Whisparr v2 | Reason |
| --- | --- | --- |
| Monitor a studio | Works | mapped to a v2 site (series) resolved by its TPDB id; add-then-monitor, non-grabbing |
| Search all monitored (bulk) | Works | runs the v2 episode search over the site's episodes — the one grab-capable v2 verb |
| Monitor a performer | v3 only | v2 has no performer entity at all — performers are embedded episode metadata, nothing monitorable |
| Add / monitor a single scene | v3 only | v2 has no `POST /episode`; a scene is acquired by adding its site and searching the episode, so there is no independent per-scene add |
| Grab quality upgrades | v3 only | Sonarr has no cutoff-upgrade-only search variant; v2 keeps a single grab verb (the episode search) by design |
| Add all missing (bulk) | v3 only | builds on the per-scene add, which v2 lacks |
| Exclude a scene / interactive release grab / per-scene status view | v3 only | v2 exclusions and releases are TPDB-keyed and cannot be tied back to a Cove scene without a scene-level id |

What works on v2: connect, import (webhook + polling reconcile), reconciliation, the read-only status
derivation over the reconciliation map, and the studio-monitor / status / search outward path above.
Scene-level id-grade *matching* on v2 still needs a ThePornDB oshash/phash bridge, scoped as a future
milestone — see [Architecture](./docs/ARCHITECTURE.md#whisparr-v2-adapter).

## Documentation

User docs live on the docs site alongside the other extensions:

- **[Whisparr Sync docs](https://alextomas955.github.io/cove-extensions/extensions/whisparr-sync)** — overview and index
- **[Connect guide](https://alextomas955.github.io/cove-extensions/extensions/whisparr-sync/guide)** — connect, pick a root folder / quality profile, add the webhook
- **[Monitoring](https://alextomas955.github.io/cove-extensions/extensions/whisparr-sync/monitoring)** — monitor a studio or performer from its Cove page and read its status line
- **[Reconciliation](https://alextomas955.github.io/cove-extensions/extensions/whisparr-sync/reconciliation)** — view matched / unmatched / needs-review and confirm or reject matches
- **[Whisparr status](https://alextomas955.github.io/cove-extensions/extensions/whisparr-sync/status)** — per-scene states, how each is derived, and the three surfaces that show them (the no-card-slot fallback)
- **[Settings reference](https://alextomas955.github.io/cove-extensions/extensions/whisparr-sync/settings)** — every setting, with defaults
- **[Architecture](./docs/ARCHITECTURE.md)** — the connection / adapter / options / webhook / reconciliation design and the API-key secret model

The rest of this file is for contributors working on the extension itself.

## Build

This extension lives in the `cove-extensions` monorepo and inherits its Cove SDK wiring from the
root `Directory.Build.props`/`.targets` (no per-project Cove reference). Build the whole monorepo
from the repo root:

```sh
dotnet build CoveExtensions.slnx
```

Run the backend unit tests:

```sh
dotnet test extensions/WhisparrSync/src/WhisparrSync.Tests/WhisparrSync.Tests.csproj
```

Build and verify the settings-page UI bundle (`dist/index.mjs`):

```sh
cd extensions/WhisparrSync/src/WhisparrSync.Ui
npm ci
npm run verify   # typecheck + lint + format:check + class-discipline gate + bundle
```

## Layout

- `src/WhisparrSync/` — the C# extension (`FullExtensionBase`): identity, the full-page settings tab
  manifest, the settings endpoints (`/test-connection`, `/status`, `/options`, `/rootfolders`,
  `/qualityprofiles`, `/webhook-url`, `/register-webhook`), the read-only reconciliation endpoints
  (`/preview-sync`, `/reconciliation`, `/match/confirm`, `/match/reject`), the identity matcher and
  match store (`Matching/`), the version-adapter seam (`Adapters/`), the options store (`Options/`),
  the webhook URL builder (`Webhook/`), and the transport-only `Client/WhisparrClient`.
- `src/WhisparrSync.Tests/` — xUnit tests; the outbound HTTP boundary is faked with
  `FakeHttpMessageHandler`, so the client is testable with no live Whisparr.
- `src/WhisparrSync.Ui/` — the React/TypeScript settings page, built with Vite to `dist/index.mjs`.

## Requires

Cove `1.0.0` or newer.
