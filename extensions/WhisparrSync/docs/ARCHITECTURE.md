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
**in the handler** (the host's `[RequiresPermission]` filter is inert on minimal-API endpoints). The
side-effect-free read projections gate on `extensions.read`; every route that reaches the stored
credentials or makes an outbound call gates on `extensions.configure` (CR-01 — the host confirms
`extensions.configure` *implies* `extensions.read`, so a route that must exclude read-only users has to
require `configure`). The one deliberate exception is the inbound `/webhook` route, which carries no Cove
principal at all — a shared-secret token is its auth (SEC-01), so it omits the principal gate entirely.

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
| `/webhook` | POST | anonymous (token) | Inbound Whisparr On-Import receiver — ingests the imported file (SEC-01) |
| `/import-log` | GET | read | The auto-import audit log: every attempt with its result, source, time, path, and Cove item + counts |
| `/root-overlap` | GET | read | A best-effort advisory: whether a Cove library root overlaps a Whisparr root (a re-grab-loop risk, SEC-02) |

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
   and trusted outright. A movie-typed id is never compared against a Cove scene UUID. A single
   unambiguous hit auto-matches; if *two* Cove videos share the same StashDB id (Cove does not enforce
   cross-video uniqueness), the scene is sent to **needs-review** instead of being matched to an
   arbitrary one.
2. **Content hash — a documented no-op.** Whisparr exposes no comparable content hash, so this leg is
   present as an explicit placeholder rather than a working check.
3. **Path (exact only).** A forward-slash-normalized path comparison. Because no root-translation map
   is configured in this release, the path leg only connects a scene when Whisparr and Cove see it at
   the *same* path — a containerized Whisparr (`/data/…`) and a Cove seeing `/mnt/media/…` will not
   match on path and fall through to the next leg. As with the StashDB leg, two Cove files that
   normalize to the identical path are ambiguous and go to **needs-review** rather than an arbitrary pick.
4. **Fuzzy title + year — a suggestion only.** A title-token Jaccard similarity gated on an equal
   year. This never links anything on its own: a fuzzy hit lands in **needs-review** with
   `AutoApplies == false`, and `ReconciliationService` never promotes it. Only the user's **Confirm**
   turns it into a match.

Anything unresolved is **unmatched** — the safe default, never a silent guess.

The match store is a single JSON blob over `IExtensionStore`, keyed on the **Whisparr movie id** (a
fuzzy suggestion carries no StashDB UUID, so the movie id is the one durable handle every leg shares).
Confirm upserts a `Confirmed` entry that is honored on the next reconcile; Reject records a `Rejected`
entry that suppresses the suggestion on re-run. A fresh `/preview-sync` recomputes the whole diff from
the current library and Whisparr state, but a persisted decision is **one-way in this phase**: a
confirmed pair becomes `Matched` and a rejected one is suppressed to `Unmatched`, so neither returns to
`NeedsReview` and there is no un-confirm / un-reject endpoint yet (a clear/reset path is deferred to a
later phase).

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

The notification also carries the secret as an **`X-Cove-Token` request header** (a `headers`
key-value entry in the payload), not only in the URL query. This is what makes Whisparr's **Test**
button succeed: the Test ping POSTs to the configured URL with the configured headers, so the
receiver sees the token in the header it validates and answers 200 instead of the 401 an unheadered
ping would get. The bare URL keeps its `?token=` for the copy-paste path, so both channels
authenticate the same secret.

**The header is the preferred channel (WR-03).** The receiver checks `X-Cove-Token` first and only
falls back to the `?token=` query when the header is absent. A secret in a URL query string is
routinely captured by Kestrel/reverse-proxy/access logs outside this extension's control, so the
query fallback exists only for hand-pasted webhooks — when it is used, the extension logs a one-time
warning. **Register in Whisparr** (auto-register) always configures the header, so the recommended
setup never relies on the query token.

## Auto-import: webhook + polling-reconcile backstop (IMPT-01/02/03/05)

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
  imports the file **in place** via the kind-appropriate `ImportDownloaded*` (SEC-03 — never moved or
  deleted). A `WhisparrRootGuard` runs FIRST: a path that does not canonicalize inside a known Whisparr
  root (or an unavailable root set) is rejected fail-closed (T-03-PT). A gone/kind-unresolvable in-root
  path falls back to a scoped `StartScan` and is flagged rather than failing silently (IMPT-05).
- **`EventLedger`** — the ONE cross-channel idempotency key, `SHA-256(downloadId | NormalizePath(path))`.
  Both channels derive the identical key from the fields they both carry (download id + the imported
  path), so a webhook-then-poll overlap of the same import is a Skipped no-op (IMPT-03). The reconcile
  additionally records a `hist:{record.id}` self-key so a re-poll of the same page is cheap — but the
  cross-channel dedup is always the shared key.
- **`ImportLog`** — a single-blob audit journal over `IExtensionStore`. Every attempt (Imported / Skipped
  / Flagged) appends exactly one entry with server UTC ticks, source (`webhook` / `poll`), event type,
  path, kind, Cove id, result, reason, and the ledger key. `GET /import-log` reads it back (read-gated)
  for the settings-page **Import activity** section (IMPT-04).

The secret is never logged; the raw webhook body is never logged; the audit log stores paths/ids/results
but never the API key or the webhook token.

## The safety model (SEC-02/03/04)

Auto-import means Cove and Whisparr both act on the same files, so the extension is built around three
guarantees that keep the two systems from fighting each other.

### Never move or delete inside a Whisparr root (SEC-03)

`IngestCoordinator` **imports in place**: it hands the imported path to the host `IScanService`
(`ImportDownloaded*`) or, on a gone/kind-unresolvable in-root path, a scoped `StartScan`. It holds no
filesystem-relocation primitive at all — Cove records the path, and the bytes stay exactly where
Whisparr put them. This is a structural property, not a runtime check, so it is enforced by a
**contract test** (`NoMutationTests`): driving a full webhook Download and a fallback records only
`IScanService` import/scan calls on the recording fake, and a source-level guard asserts the
coordinator source contains no `File.Move`/`File.Delete`/`Directory.*` API. If someone ever adds one,
that test fails.

### Warn on a re-grab feedback loop (SEC-02)

If a Cove library root and a Whisparr root are the same directory (or one contains the other), an
import-in-place can look to Whisparr like a brand-new file and be re-grabbed — a feedback loop.
`RootOverlapDetector` compares the Whisparr roots (`GET /api/v3/rootfolder`) against the Cove library
roots (`CoveConfiguration.CovePaths` when the host injects it, otherwise the distinct folders of the
library's own files) using the same separator-normalized, case-sensitive, segment-bounded containment as
the ingest guard, in **both** directions. `GET /root-overlap` surfaces the result. It is a
**best-effort advisory, never a hard gate**: cross-mount or containerized deployments legitimately see
the same library at different paths, so a non-overlap is not a guarantee and an overlap is not an
error — the warning says as much.

### An honest manifest (SEC-04)

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
