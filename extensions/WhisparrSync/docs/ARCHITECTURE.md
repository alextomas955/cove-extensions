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
**in the handler** (the host's `[RequiresPermission]` filter is inert on minimal-API endpoints). Only
the two side-effect-free read projections gate on `extensions.read`; every route that reaches the
stored credentials or makes an outbound call gates on `extensions.configure` (CR-01 — the host
confirms `extensions.configure` *implies* `extensions.read`, so a route that must exclude read-only
users has to require `configure`).

| Route | Method | Permission | Purpose |
|-------|--------|-----------|---------|
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

`/preview-sync` is `configure`-gated even though it only *reads*: it reaches the stored credentials to
call Whisparr, so a read-only user must not be able to trigger it (the same CR-01 rule as the list
routes). `/reconciliation` is the only reconciliation route that is `read`-gated, because it reads the
extension's own match store and never touches the credentials.

## The reconciliation match model (MATCH-01/02/03)

Reconciliation answers one question for every Whisparr scene: *which Cove item, if any, is the same
thing?* It never mutates Cove or Whisparr — `/preview-sync` opens an `AsNoTracking` read over the Cove
library, fetches the Whisparr movie list, and composes a diff. The only writes in the whole feature
are Confirm/Reject, and they land solely in the extension's own match store.

`IdentityMatcher` resolves a link through a **confidence-ordered chain**, stopping at the first leg
that fires:

1. **StashDB id (authoritative).** An exact, case-insensitive match on the StashDB UUID. This is the
   durable identity key — a scene keeps its identity across renames and moves — so it is tried first
   and trusted outright. A movie-typed id is never compared against a Cove scene UUID.
2. **Content hash — a documented no-op.** Whisparr exposes no comparable content hash, so this leg is
   present as an explicit placeholder rather than a working check.
3. **Path (exact only).** A forward-slash-normalized path comparison. Because no root-translation map
   is configured in this release, the path leg only connects a scene when Whisparr and Cove see it at
   the *same* path — a containerized Whisparr (`/data/…`) and a Cove seeing `/mnt/media/…` will not
   match on path and fall through to the next leg.
4. **Fuzzy title + year — a suggestion only.** A title-token Jaccard similarity gated on an equal
   year. This never links anything on its own: a fuzzy hit lands in **needs-review** with
   `AutoApplies == false`, and `ReconciliationService` never promotes it. Only the user's **Confirm**
   turns it into a match.

Anything unresolved is **unmatched** — the safe default, never a silent guess.

The match store is a single JSON blob over `IExtensionStore`, keyed on the **Whisparr movie id** (a
fuzzy suggestion carries no StashDB UUID, so the movie id is the one durable handle every leg shares).
Confirm upserts a `Confirmed` entry that is honored on the next reconcile; Reject records a `Rejected`
entry that suppresses the suggestion on re-run. Both are reversible — a fresh `/preview-sync`
recomputes the whole diff from the current library and Whisparr state.

Confirm/Reject validate the submitted `{coveId, whisparrMovieId}` pair against the freshly computed
diff before writing (V5): a forged pair that is not a current needs-review suggestion is refused with
`MATCH_NOT_IN_DIFF`, so a caller cannot write an arbitrary link into the store.

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

## Network posture and residual SSRF

The base URL is user-supplied and the extension manifest requests `network: ["*"]`, because Whisparr
is typically self-hosted on the LAN (`http://localhost:6969`, a Docker bridge address, a private
`192.168.*` / `10.*` host). The extension therefore makes an authenticated outbound request to an
address the operator chooses, which is an inherent server-side request forgery (SSRF) surface: a
`configure` user can point it at an internal host and read reachability / timing from the classified
result (`unreachable` vs `notWhisparr` vs `badKey`).

This is bounded rather than eliminated, because a full private-range block would break the intended
LAN use case:

- **The stored API key never leaves with a caller-chosen host (CR-01).** `ResolveCredsAsync` only
  ever pairs the stored key with the stored host; a request that overrides the base URL must carry
  its own key. So the SSRF probe cannot also exfiltrate the stored credential.
- **Only `configure` users reach the outbound routes.** The list, test-connection, webhook-url, and
  register routes all require `extensions.configure` — a read-only user cannot drive an outbound call.
- **The transport edge validates the URL (WR-02).** `WhisparrClient` rejects a relative, malformed,
  or non-`http(s)` base URL as `Unreachable` before dispatching, so `file://` and similar schemes
  never reach the socket.

Blocking specific link-local / metadata addresses (e.g. `169.254.169.254`) or adding an opt-in
"allow private targets" posture is a possible future hardening; it is deliberately not done here so
the common LAN-Whisparr configuration keeps working out of the box.

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
