# Whisparr Sync

A Cove extension (`com.alextomas955.whisparrsync`) that connects Cove to a
[Whisparr](https://whisparr.com) v3 (Eros) or v2 instance. You enter your Whisparr URL and API key on
a settings page, test the connection, and Cove reads the instance's version and name back over a real
call to Whisparr's `GET /api/v3/system/status`. Your API key is stored server-side only — it is
never returned to the browser and never written to logs.

> **Status:** early foundation (0.1.0). This release is the connect experience — enter a URL + key,
> test the connection, pick a root folder and quality profile from auto-populated dropdowns, and
> generate (and optionally auto-register) a webhook URL — plus a **read-only reconciliation view**:
> compare what Whisparr tracks against your Cove library (matched / unmatched / needs-review) and
> confirm or reject low-confidence matches. Acquisition auto-import (acting on the webhook) arrives in
> a later phase.

After a successful test you pick a **root folder** and **quality profile** from lists read live from
your instance (no hand-typed paths or ids), and the page shows a ready-to-use **webhook URL** with an
embedded secret that you can copy into Whisparr or let the extension add for you. The webhook
*receiver* (auto-import on a Whisparr event) is a later phase.

## Whisparr v2

Both **v3 (Eros)** and **v2** are supported. On v2 the extension connects, imports what Whisparr
acquires (webhook + polling reconcile), and reconciles by **file path and fuzzy title/year**.

The one difference is match precision: Whisparr v2 is ThePornDB-native and carries **no StashDB id**
on any scene, so the authoritative StashDB-id match — the durable identity key v3 matching leads with
— cannot apply to v2. This is a permanent property of v2's data model, not a missing feature: more v2
scenes land in unmatched / needs-review than on v3. See
[Architecture](./docs/ARCHITECTURE.md#whisparr-v2-adapter) for why.

## Documentation

User docs live on the docs site alongside the other extensions:

- **[Whisparr Sync docs](https://alextomas955.github.io/cove-extensions/extensions/whisparr-sync)** — overview and index
- **[Connect guide](https://alextomas955.github.io/cove-extensions/extensions/whisparr-sync/guide)** — connect, pick a root folder / quality profile, add the webhook
- **[Reconciliation](https://alextomas955.github.io/cove-extensions/extensions/whisparr-sync/reconciliation)** — view matched / unmatched / needs-review and confirm or reject matches
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

Cove `0.9.0` or newer (for the page-layout settings tab).
