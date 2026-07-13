# Whisparr Sync

A Cove extension (`com.alextomas955.whisparrsync`) that connects Cove to a
[Whisparr](https://whisparr.com) v3 (Eros) instance. You enter your Whisparr URL and API key on a
settings page, test the connection, and Cove reads the instance's version and name back over a real
call to Whisparr's `GET /api/v3/system/status`. Your API key is stored server-side only — it is
never returned to the browser and never written to logs.

> **Status:** early foundation (0.1.0). This release is the connection walking skeleton: enter a
> URL + key and Test connection. Library reconciliation and acquisition auto-import arrive in later
> phases.

## Documentation

User docs will live on the docs site alongside the other extensions:

- **[Whisparr Sync docs](https://alextomas955.github.io/cove-extensions/extensions/whisparr-sync)** — overview and index

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
  manifest, the `/test-connection` endpoint, and the transport-only `Client/WhisparrClient`.
- `src/WhisparrSync.Tests/` — xUnit tests; the outbound HTTP boundary is faked with
  `FakeHttpMessageHandler`, so the client is testable with no live Whisparr.
- `src/WhisparrSync.Ui/` — the React/TypeScript settings page, built with Vite to `dist/index.mjs`.

## Requires

Cove `0.9.0` or newer (for the page-layout settings tab).
