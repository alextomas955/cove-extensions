# WhisparrSync end-to-end tests

These tests exercise the WhisparrSync extension against a real Cove instance the
[shared harness](../../../tests/e2e/README.md) brings up in Docker. This package
(`@cove-extensions/whisparrsync-e2e`) imports that harness **by name** — see
[Adding an extension's E2E suite](../../../website/docs/contributing/authoring-e2e.md) for the
shape every extension suite follows and why there is no second Playwright install here.

All media is the [synthetic corpus](./fixtures/README.md) — obviously invented studios, performers,
and scenes with generated placeholder thumbnails. No real media, no real images, and no credentials
are ever committed. The one narrowed exception is a small [real-identity allowlist](#content-safety-the-real-identity-allowlist):
real, SFW-sounding metadata-source ids, names, and descriptions — never images, never explicit text —
that let the container tier assert genuine Whisparr-side state. Whisparr API keys are read from the
running container at runtime, never from a file in this repository.

## What you need

- **Docker**, running. Every tier boots its own containers; you never start Whisparr yourself.
- **`npm install` at the repository root** — the workspace links (`@cove-extensions/e2e`) and the
  single hoisted Playwright install come from there, never from a `npm install` inside this folder.
- **The extension's build outputs**, which the harness copies into a fresh Cove container:
  `extensions/WhisparrSync/artifacts/publish/` (the DLL + `extension.json`) and
  `src/WhisparrSync.Ui/dist/index.mjs`. `lib/whisparrsync-fixtures.mjs` resolves both self-relatively.
- **Chromium** for the browser tier: `npx playwright install chromium`, once.

## The three tiers

| Tier | What it proves | Runner | Where it runs |
| --- | --- | --- | --- |
| **A — hermetic** | Cove-only UI and API behavior: the settings page and connection classifier, the configuration guard, the scene panel, the library toolbar and batch menu, the per-entity Missing tabs and the activity sub-page, overlay accessibility, read-only projections, and inbound-webhook rejection | Playwright (browser) | PR CI |
| **B — container correctness** | Real Whisparr-side state for add / monitor / search, the file-settings round-trip, the webhook round-trip, the full acquire→import pipeline, and v2 parity, against a real Whisparr container with an offline metadata stub | `node --test` | PR CI (fork-safe) |
| **C — live** | The committed metadata recordings still match the real service (a drift check) | manual | Local only |

CI runs tiers A and B on every pull request, fully offline: neither references a repository secret,
so both run on fork pull requests where secrets are absent. Tier B points Whisparr's metadata source at
a committed [record-and-replay stub](./fixtures/skyhook/README.md) and pulls public images, so it needs
no key. Tier C is the only tier that reaches the real network, and it stays local and manual.

Tier A stands up **no Whisparr at all** — there is no `whisparr` browser fixture. Surfaces whose data
comes from Whisparr are driven by route-intercepting the extension's own endpoints with synthetic
payloads, so a browser spec asserts rendering and click-through, never real Whisparr state. Real
Whisparr state is tier B's job, and tier B asserts it by reading **Whisparr's own API**, never the
extension's return code.

## Run tier A (hermetic, browser)

From the shared harness directory:

```sh
cd ../../../tests/e2e
npx playwright test --project=whisparrsync
```

`whisparrsync` is a project in the shared
[`playwright.config.mjs`](../../../tests/e2e/playwright.config.mjs), pointed at this package's
`tests/`. You can also run it through this package's own script, which does the same `cd`:

```sh
npm test --workspace @cove-extensions/whisparrsync-e2e
```

These need only the Cove container, so they are fast and deterministic.

## Run tier B (container correctness)

From this directory:

```sh
node --test --test-concurrency=1 node-tests/*.test.mjs
```

or through the package script:

```sh
npm run test:node --workspace @cove-extensions/whisparrsync-e2e
```

Two details of that command are load-bearing, and CI issues it identically:

- **The glob is required.** `node --test node-tests/` (a bare directory) is read as a module entry
  point rather than searched, and fails, so the run must enumerate the spec files.
- **`--test-concurrency=1`** keeps one container stack up at a time; the default parallelism boots
  several Cove + Whisparr stacks at once.

Each spec brings up Cove, a version-parameterized Whisparr container (`ghcr.io/hotio/whisparr:v3` or
`:v2`), and the offline metadata stub through `lib/setup.mjs`, then asserts the real add / monitor /
search / file-settings / webhook / refusal behavior. `pipeline.test.mjs` is the heaviest: it adds
qBittorrent 4.6.7 and a Torznab fake indexer sharing a dedicated Docker volume, so a grab can flow
through a real download client and import back. All of it needs Docker but no key and no network to
the real metadata service. Every container image can be overridden by an environment variable
(`SKYHOOK_STUB_IMAGE`, `WHISPARR_API_STUB_IMAGE`, `FAKE_INDEXER_IMAGE`, `QBIT_IMAGE`) when a pull-through
mirror is needed.

## Run tier C (live drift check)

Tier C re-verifies the committed metadata recordings against the real service and is a local, manual
activity — never committed CI. Point Whisparr at the real `api.whisparr.com` (instead of the stub) with
real keys, run the allowlist lookups, and compare against the recordings under
[`fixtures/skyhook/`](./fixtures/skyhook/README.md). Those recordings carry a capture date; when the live
contract diverges from them, re-capture and re-scrub per that directory's re-capture procedure. Reserve
`WHISPARR_E2E=1` and real credentials for this tier only — it marks a run as the live one; no committed
spec reads it.

## Opt-in local legs

Two checks are gated off by default so the standard run and CI never reach them:

- **Live cove-dev click-through** — `discovery-card-parity.spec.mjs` runs an extra block when
  `COVE_DEV_URL` is set, driving the Missing-tab card grid against a running cove-dev instance under
  the real host stylesheet and screenshotting it next to the native videos grid for a human glance.
  It feeds the tab a synthetic list via a route intercept, so no real metadata surfaces and no real
  Whisparr mutation occurs. `COVE_DEV_USER` / `COVE_DEV_PASSWORD` override the cove-dev defaults.
- **Docs screenshots** — `WHISPARR_SHOTS=1`, below.

## Content-safety: the real-identity allowlist

Everything here is synthetic **except** a small committed allowlist of real, SFW-sounding
metadata-source **ids / names / descriptions** — **never images, never explicit text**. It exists
because Whisparr validates every add / monitor / search id against its own metadata source: a
fully-synthetic id resolves to zero rows, so an outward call could only no-op to an attribution-only
result, and there would be nothing real to assert. Only the identity metadata is real; images stay
synthetic and every recorded response is scrubbed of images and explicit text. This exception is
documented, not silent — see [`fixtures/README.md`](./fixtures/README.md),
[`fixtures/allowlist/README.md`](./fixtures/allowlist/README.md), and
[`fixtures/skyhook/README.md`](./fixtures/skyhook/README.md) for the rationale, the chosen entries, and
the captured wire contract.

## Layout

| Path | What it holds |
| --- | --- |
| `tests/` | Tier A Playwright specs (the `whisparrsync` project's `testDir`) |
| `node-tests/` | Tier B `node --test` correctness specs |
| `lib/setup.mjs` | Browser-free bring-up of the whole tier-B stack, plus the plain `api` helper |
| `lib/whisparrsync-fixtures.mjs` | Tier A wiring on the shared harness: the `extension` paths, re-exports, and `routeUsableConfiguration` |
| `lib/*-container.mjs`, `lib/*-stub.mjs`, `lib/fake-indexer.mjs`, `lib/pipeline.mjs` | The Whisparr / qBittorrent containers, the SkyHook replay + Whisparr API stubs, and the acquisition chain |
| `lib/pages/` | Page objects for the settings page, scene panel, and entity detail tabs |
| `lib/seed-fixtures.mjs` | Seeds the synthetic corpus into a running Cove through its API |
| `fixtures/` | The [synthetic corpus](./fixtures/README.md), the [allowlist](./fixtures/allowlist/README.md), and the [SkyHook recordings](./fixtures/skyhook/README.md) |

## How CI runs it

The suite is wired through `extensions/catalog.json`, not through workflow logic: WhisparrSync's entry
declares `e2ePath`, `e2eProject: "whisparrsync"`, and `e2eNodeTestsPath`. The `e2e` job in
`.github/workflows/build.yml` reads those and fails closed — a declared `e2eNodeTestsPath` whose
directory is missing, or which matches no `*.test.mjs`, is an error rather than a silent pass. The
workflow's final gate requires `validate`, `build`, and `e2e` to all be green, so both tiers block a
merge.

Locally, the same two commands are registered in the repo-root gate registry and run together with:

```sh
npm run verify:e2e
```

## Regenerate the docs screenshots

The user docs embed feature-walkthrough screenshots under `website/static/img/whisparr-sync/`. They
ship as labeled placeholders (a maintainer never has to run this for the docs to build link-safe), and
this capture upgrades them to real UI shots — always from the **synthetic corpus**, never real media.

Set `WHISPARR_SHOTS=1` to run the capture spec. It seeds only the synthetic fixtures, drives each
surface through the existing page objects, and overwrites the matching PNG in place:

```sh
cd ../../../tests/e2e
WHISPARR_SHOTS=1 npx playwright test --project=whisparrsync screenshots.spec.mjs
```

The slot set (one canonical filename per surface, five today) lives in `lib/screenshot-targets.mjs` —
the single source of truth the capture and the placeholder generator
(`website/scripts/gen-screenshot-placeholders.mjs`) both read, so the two never drift. Capture is
best-effort per slot: a surface the harness can't render is logged and keeps its committed placeholder
rather than failing the run. Without `WHISPARR_SHOTS` the spec skips, so the default run and CI never
run it. To regenerate the placeholders for any slot without a real capture:

```sh
node website/scripts/gen-screenshot-placeholders.mjs
```
