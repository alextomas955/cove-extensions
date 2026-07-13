# Whisparr Sync

A Cove extension (`com.alextomas955.whisparrsync`) that connects Cove to a
[Whisparr](https://whisparr.com) v3 (Eros) instance. You enter your Whisparr URL and API key on a
settings page, test the connection, and Cove reads the instance's version and name back over a real
call to Whisparr's `GET /api/v3/system/status`. Your API key is stored server-side only — it is
never returned to the browser and never written to logs.

> **Status:** early foundation (0.1.0). This release is the connect experience: enter a URL + key,
> test the connection, pick a root folder and quality profile from auto-populated dropdowns, and
> generate (and optionally auto-register) a webhook URL. Library reconciliation and acquisition
> auto-import arrive in later phases.

After a successful test you pick a **root folder** and **quality profile** from lists read live from
your instance (no hand-typed paths or ids), and the page shows a ready-to-use **webhook URL** with an
embedded secret that you can copy into Whisparr or let the extension add for you. The webhook
*receiver* (auto-import on a Whisparr event) is a later phase.

## Documentation

User docs live on the docs site alongside the other extensions:

- **[Whisparr Sync docs](https://alextomas955.github.io/cove-extensions/extensions/whisparr-sync)** — overview and index
- **[Connect guide](https://alextomas955.github.io/cove-extensions/extensions/whisparr-sync/guide)** — connect, pick a root folder / quality profile, add the webhook
- **[Settings reference](https://alextomas955.github.io/cove-extensions/extensions/whisparr-sync/settings)** — every setting, with defaults
- **[Architecture](./docs/ARCHITECTURE.md)** — the connection / adapter / options / webhook design and the API-key secret model

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
  `/qualityprofiles`, `/webhook-url`, `/register-webhook`), the version-adapter seam
  (`Adapters/`), the options store (`Options/`), the webhook URL builder (`Webhook/`), and the
  transport-only `Client/WhisparrClient`.
- `src/WhisparrSync.Tests/` — xUnit tests; the outbound HTTP boundary is faked with
  `FakeHttpMessageHandler`, so the client is testable with no live Whisparr.
- `src/WhisparrSync.Ui/` — the React/TypeScript settings page, built with Vite to `dist/index.mjs`.

## Requires

Cove `0.9.0` or newer (for the page-layout settings tab).
