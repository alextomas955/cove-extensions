# WhisparrSync end-to-end tests

These tests exercise the WhisparrSync extension against a real Cove instance the
[shared harness](../../../tests/e2e/README.md) brings up in Docker. They split into three tiers by
what each one needs to run and where it runs.

All media is the [synthetic corpus](./fixtures/README.md) — obviously invented studios, performers,
and scenes with generated placeholder thumbnails. No real media, no real images, and no credentials
are ever committed. The one narrowed exception is a small [real-identity allowlist](#content-safety-the-real-identity-allowlist):
real, SFW-sounding metadata-source ids, names, and descriptions — never images, never explicit text —
that let the container tier assert genuine Whisparr-side state. Whisparr API keys are read from the
running container at runtime, never from a file in this repository.

## The three tiers

| Tier | What it proves | Runner | Where it runs |
| --- | --- | --- | --- |
| **A — hermetic** | Cove-only UI and API behavior: the settings page, scene panel, library toolbar and batch menu, read-only reconciliation, and inbound-webhook rejection | Playwright (browser) | PR CI |
| **B — container correctness** | Real Whisparr-side state for add / monitor / search, the webhook round-trip, and v2 parity, against a real Whisparr container with an offline metadata stub | `node --test` | PR CI (fork-safe) |
| **C — live** | The committed metadata recordings still match the real service (a drift check) | `node --test` | Local / manual |

CI runs tiers A and B on every pull request, fully offline: neither references a repository secret,
so both run on fork pull requests where secrets are absent. Tier B points Whisparr's metadata source at
a committed [record-and-replay stub](./fixtures/skyhook/README.md) and pulls the public Whisparr images,
so it needs no key. Tier C is the only tier that reaches the real network, and it stays local and manual.

## Run tier A (hermetic, browser)

From the shared harness directory:

```sh
cd ../../../tests/e2e
npx playwright test --project=whisparrsync
```

These need only the Cove container, so they are fast and deterministic. You can also run them through
this package's own script:

```sh
npm test --workspace @cove-extensions/whisparrsync-e2e
```

## Run tier B (container correctness)

From this directory:

```sh
node --test node-tests/*.test.mjs
```

or through the package script:

```sh
npm run test:node --workspace @cove-extensions/whisparrsync-e2e
```

The glob is required — `node --test node-tests/` (a bare directory) is treated as a module entry point
on current Node and fails, so the run must enumerate the spec files. These specs bring up Cove, a
version-parameterized Whisparr container, and the offline metadata stub, then assert the real add /
monitor / search / webhook / reconcile behavior. They need Docker but no key and no network to the real
metadata service.

## Run tier C (live drift check)

Tier C re-verifies the committed metadata recordings against the real service and is a local, manual
activity — never committed CI. Point Whisparr at the real `api.whisparr.com` (instead of the stub) with
real keys, run the allowlist lookups, and compare against the recordings under
[`fixtures/skyhook/`](./fixtures/skyhook/README.md). Those recordings carry a capture date; when the live
contract diverges from them, re-capture and re-scrub per that directory's re-capture procedure. Reserve
`WHISPARR_E2E=1` and real credentials for this tier only.

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

The slot set (one canonical filename per surface) lives in `lib/screenshot-targets.mjs` — the single
source of truth the capture and the placeholder generator (`website/scripts/gen-screenshot-placeholders.mjs`)
both read, so the two never drift. Capture is best-effort per slot: a surface the harness can't render
is logged and keeps its committed placeholder rather than failing the run. Without `WHISPARR_SHOTS` the
spec skips, so the default run and CI never run it. To regenerate the placeholders for any slot without a
real capture:

```sh
node website/scripts/gen-screenshot-placeholders.mjs
```
