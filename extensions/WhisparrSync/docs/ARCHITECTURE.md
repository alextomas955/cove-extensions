# Architecture

Whisparr Sync connects Cove to a self-hosted [Whisparr](https://whisparr.com) v3 instance. This
page traces how the extension turns a URL + API key into a verified connection, auto-populated
root-folder / quality-profile lists, and a registered webhook — for a contributor reading the code
for the first time.

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
- **`Adapters/IWhisparrAdapter` + `V3Adapter`** — the version-adapter boundary (VER-01). All
  v3-specific wire knowledge (endpoint paths, the webhook notification payload shape) lives in
  `V3Adapter`; the handlers never hold it. `AdapterSelector` picks the adapter from the detected
  major version and **refuses** anything but v3 (VER-04) — never a silent wrong-adapter call.
- **`Options/WhisparrOptions` + `OptionsStore`** — a single JSON blob persisted over Cove's
  `IExtensionStore` under the `"options"` key (URL, API key, selected/detected version, root-folder
  id, quality-profile id, webhook secret). A corrupt or absent blob loads as safe defaults.
- **`Webhook/WebhookUrlBuilder`** — mints the webhook secret and builds the copy-paste URL.
- **`WhisparrSync.Api`** — the minimal-API endpoints and the settings-tab manifest.

## Endpoints

All are mounted under `/api/extensions/com.alextomas955.whisparrsync/` and are permission-checked
**in the handler** (the host's `[RequiresPermission]` filter is inert on minimal-API endpoints):
reads gate on `extensions.read`, writes on `extensions.configure`.

| Route | Method | Permission | Purpose |
|-------|--------|-----------|---------|
| `/test-connection` | POST | configure | Classify the connection; return version + instance name on success |
| `/status` | GET | read | Whether the extension is configured + the detected version (no key) |
| `/options` | GET | read | The persisted options as a redaction-safe view (no key, only `hasApiKey`) |
| `/options` | POST | configure | Persist URL / version / root folder / quality profile (write-only key) |
| `/rootfolders` | POST | read | The instance's root folders (creds in the body) |
| `/qualityprofiles` | POST | read | The instance's quality profiles (creds in the body) |
| `/webhook-url` | GET | read | The ready-to-use webhook URL with the embedded secret |
| `/register-webhook` | POST | configure | Best-effort auto-register of the Cove webhook in Whisparr |

## The API-key secret model (CONN-06)

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

## The webhook (CONN-07)

The webhook secret is a 256-bit token minted with `System.Security.Cryptography.RandomNumberGenerator`
(never `System.Random`) and persisted once in the options, so the URL is stable across reads. The
copy-paste URL is
`{coveBase}/api/extensions/com.alextomas955.whisparrsync/webhook?token={secret}`.

Auto-register posts a v3 Notification (`implementation: "Webhook"`, `configContract:
"WebhookSettings"`) to `POST /api/v3/notification`. It is **best-effort**: a non-2xx response (or a
refused version) returns `registered: false` and the UI falls back to copy-paste — the connect flow
never fails on it. The exact `fields` contract is confirmed against a live instance; the copy-paste
URL is the guaranteed path regardless.

The webhook **receiver** that consumes this URL (the inbound endpoint that imports items on a
Whisparr event) is not part of this phase — it arrives in a later phase with its own token
authentication.
