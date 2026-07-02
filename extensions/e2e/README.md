# Cove Extensions — Shared E2E Test Harness

Reusable end-to-end test infrastructure for any extension in this monorepo. It brings up a real,
isolated, official Cove instance in Docker, installs your extension's built output into it, and
lets you drive it over plain HTTP and/or a real browser (Playwright) — no local native Cove
install required, and no state shared between test runs. Tests run in parallel by default.

## Why not test against my local Cove dev instance?

Because it isn't reproducible: it depends on whatever's currently in your `%LOCALAPPDATA%\cove`
(or `COVE_HOME`), it can't run in CI, and a bad test could corrupt data you care about. This
harness always starts from the official `ghcr.io/yourcove/cove-app` image with an empty database,
so every run starts from the same clean state and touches nothing on your machine.

## Prerequisites

- Docker (Docker Desktop on Windows/macOS, or native Docker on Linux) — running, with the daemon
  reachable from your shell.
- Node.js 22+ and npm (already required elsewhere in this repo).

No host-specific Docker configuration is required — extension install works by copying files
directly into the running container (via Testcontainers' `copyFilesToContainer`/`exec`, not a host
bind-mount). This means it works the same on any machine and any CI runner, regardless of which
drives/folders that machine's Docker is configured to share.

## Quick start

```sh
cd extensions/e2e
npm install
npx playwright install chromium   # one-time browser download
npm test
```

That's it — `npm test` runs every project in [`playwright.config.mjs`](playwright.config.mjs) (see
"One Playwright install, many extensions" below) across 4 parallel workers by default, each spec
file provisioning and tearing down its own isolated Cove instance.

## One Playwright install, many extensions

There is exactly **one** `node_modules`/`@playwright/test` install for the whole monorepo, living
here in `extensions/e2e/`. Each extension's E2E suite is registered as a Playwright **project** in
[`playwright.config.mjs`](playwright.config.mjs) pointing `testDir` at that extension's own test
directory (e.g. `extensions/Renamer/e2e/tests/`) — the test *files* live next to the extension they
test, but there's no second `@playwright/test` install anywhere else.

**This matters, not just a style preference:** two separate `@playwright/test` installs in the same
process break Playwright's internal module singleton (`Requiring @playwright/test second time`) the
moment one test file imports a fixture module from the other install. Adding a new extension's
suite means adding one `projects` entry here — not running `npm install`/`npx playwright install`
again inside that extension's folder.

Run a single extension's suite with `--project`:

```sh
cd extensions/e2e
npx playwright test --project=renamer
```

Each extension's own directory (e.g. `extensions/Renamer/e2e/`) has a minimal `package.json` whose
`test` script just shells out to this pattern, so `npm test` works the same whether you're standing
in the shared `e2e/` directory or in the extension's own.

## Parallel execution

`fullyParallel: true` with `workers: 4` — tests run concurrently by default, each against its own
isolated Cove instance. This is safe because:

- Worker-shared-harness test files (the default — see `lib/fixtures.mjs`) each seed their own
  uniquely-named data per test (timestamp + random suffix), so concurrent tests never collide even
  when they land in the same worker.
- Files that mutate shared extension state itself — install/enable/disable/uninstall — opt out of
  the shared-per-worker harness entirely and provision a fresh, isolated instance **per test**
  instead (see `extension-lifecycle.spec.mjs`'s own `isolatedHarness` fixture). Toggling or
  removing the one shared extension install would otherwise race against any other test in the
  same worker that's mid-assertion against it.

**Worker count is capped at 4, not left at Playwright's CPU-based default.** Each worker brings up
its own Docker Compose network, and Docker's default address-pool allocation is a finite,
**host-wide** resource shared with any other Docker projects already running on the machine —
confirmed directly: an uncapped run (Playwright's default, which scaled to 13 workers on the
machine this was built on) failed 3 of 13 tests with `all predefined address pools have been fully
subnetted` because other, unrelated Docker projects on that machine had already claimed part of the
default pool. Override with `--workers=N` if your machine can sustain more (or needs fewer, e.g. in
a resource-constrained CI runner).

**If a run is killed or a worker crashes before `environment.up()` finishes**, Testcontainers'
Ryuk cleanup can leave healthy containers running (confirmed directly — Ryuk reaps containers when
its own tracked session ends cleanly, but a mid-`up()` crash doesn't always trigger that promptly).
Clean up manually with:

```sh
docker ps -a --filter "name=testcontainers" -q | xargs -r docker rm -f
docker network ls --filter "name=testcontainers" --format "{{.Name}}" | xargs -r docker network rm
```

## Writing your first test

1. Build your extension the normal way (whatever produces its publish output + `extension.json` +
   optional UI bundle — e.g. Renamer's own `scripts/deploy-dev.ps1` build step, minus the deploy).
2. Copy [`tests/template.spec.mjs`](tests/template.spec.mjs) into your own extension's directory
   (e.g. `extensions/<YourExtension>/e2e/tests/`) — see Renamer's `extensions/Renamer/e2e/` for the
   full pattern, including its own thin `lib/<yourextension>-fixtures.mjs` that pre-fills the
   `extension` fixture option so individual test files don't repeat build paths.
3. Add a `projects` entry for your extension in this directory's `playwright.config.mjs`.
4. Update the `test.use({ extension: {...} })` block with your own `publishDir`, `manifestPath`,
   and (if you have one) `uiBundlePath`.
5. Replace the two example tests with your own assertions.

```js
// from e.g. extensions/<YourExtension>/e2e/tests/your-test.spec.mjs
import { test, expect } from '../../../e2e/lib/fixtures.mjs';

test.use({
  extension: {
    publishDir: '/path/to/your/artifacts/publish',
    manifestPath: '/path/to/your/extension.json',
    uiBundlePath: '/path/to/your/dist/index.mjs', // omit if you have no frontend
  },
});

test('your extension does the thing', async ({ api, page }) => {
  // api.get/post/put/delete talk straight to the running instance's REST API — no browser needed.
  const { json } = await api.get('/api/extensions');

  // page is a real Playwright page already navigated to the running instance's home page, ALREADY
  // signed in (see "Signing in" below) — use it for anything needing the real UI.
  await page.goto(`${page.url()}settings`);
});
```

### What the `extension` fixture option does

Setting `test.use({ extension: {...} })` at the top of a test file makes every test in that file
run against an instance with your extension already installed (via Testcontainers'
`copyFilesToContainer` + a restart — the same on-disk shape Cove's own bind-mount Docker install
produces, just without depending on your machine's file-sharing configuration) — see
[`lib/install-extension.mjs`](lib/install-extension.mjs). If a test file has no `extension` fixture
set, it gets a clean instance with only Cove's built-in extensions installed.

### Signing in

Cove's frontend hard-gates the **entire** app behind a first-run setup wizard until an owner
account exists — there is no button to dismiss it while `ownerMissing` is true (confirmed directly:
clicking "Skip setup for now" does nothing in that state). The `harness` fixture calls
`bootstrapOwner()` once per worker before any test runs, and the `page` fixture pre-seeds
`sessionStorage`'s `cove-setup-dismissed` flag via `addInitScript` so the wizard's *other* gate
(`needsSetup`, true whenever no library path is configured — genuinely the case for a fresh
container) doesn't block every UI test either. You don't need to do anything for this — every
`page` fixture use already lands on the real app, signed in. See `lib/harness.mjs`'s
`bootstrapOwner()` doc comment for the full mechanism if you need to touch this.

### Available fixtures

| Fixture | What it gives you |
|---|---|
| `baseUrl` | The running instance's URL (e.g. `http://localhost:54321`) — a fresh random port every run |
| `api` | `{ get, post, put, delete }` helpers for calling the instance's REST API directly, no browser |
| `page` | A real Playwright `Page`, already navigated to `baseUrl` and already signed in |
| `harness` | The raw harness handle, if you need lower-level control (`installExtensionFromUrl`, `container` for direct `exec`/file copy, `stop`, etc.) |

One Cove instance is shared per Playwright **worker** (not per test) to keep the suite fast —
booting a fresh container per test would make even a small suite slow. This means your tests must
not depend on a clean database between tests within the same file; seed your own uniquely-named
test data per test instead of relying on an empty instance. If your test needs to mutate the
extension install itself (enable/disable/uninstall), give it its OWN dedicated harness instead of
the shared one — see `extension-lifecycle.spec.mjs` for the pattern.

## Running locally vs CI

Locally: `npm test` (runs every project, 4 parallel workers) or `npx playwright test
--project=<name>` for one extension — see Quick start and "One Playwright install, many
extensions" above.

CI: the `.github/workflows/build.yml` `e2e` job runs `npx playwright test --project=<name>` for
each catalog entry that declares an `e2ePath`/`e2eProject`, against that entry's just-built
`artifacts/<name>/` output (not a downloaded zip) — see that workflow for the exact steps. There is
no CI-only fork of the harness itself; the same `docker-compose.yml`, install helpers, and fixtures
run in both places.

## When a test fails

Playwright prints which step failed (container boot / extension install / a specific assertion),
not just a generic timeout — the harness's readiness helpers raise a specific error naming what
didn't happen in time. On any failure, Playwright also retains a trace (`test-results/*/trace.zip`,
openable with `npx playwright show-trace <path>`) and a screenshot.

If a container is left running after an interrupted test run, see "Parallel execution" above for
the cleanup command.

## How it works (implementation notes)

- [`docker/docker-compose.yml`](docker/docker-compose.yml) — the official `ghcr.io/yourcove/cove-app`
  image + a `pgvector/pgvector` Postgres container, both using `tmpfs`/ephemeral state, published on
  a random host port (`0:5073` — Docker assigns a free port) so parallel runs never collide. The
  `cove` service also mounts a `tmpfs` at `/data2` — see `lib/harness.mjs` and
  `cross-device-move.spec.mjs` for why.
- [`lib/harness.mjs`](lib/harness.mjs) — `startHarness()` uses
  [Testcontainers-node](https://node.testcontainers.org/)'s `DockerComposeEnvironment` (not a
  hand-rolled `docker compose` child_process wrapper — Testcontainers' Ryuk sidecar guarantees
  cleanup even on a killed process, and it owns port resolution + health-check waiting natively).
  Returns a handle with `baseUrl`, `container` (the raw Testcontainers container object),
  `installExtension`, `installExtensionFromUrl`, `bootstrapOwner`, `exec`, and `stop`.
- [`lib/stage-extension.mjs`](lib/stage-extension.mjs) — copies a build's publish output +
  manifest + UI bundle into the on-disk shape Cove expects (`<id>/extension.json` + DLLs + optional
  `index.mjs`).
- [`lib/install-extension.mjs`](lib/install-extension.mjs) — two install paths:
  - `installViaContainerCopy` — stages the extension, then copies it into the running container's
    `/config/extensions/<id>/` via Testcontainers' own container API and restarts (mirrors Cove's
    documented bind-mount install, without depending on host file-sharing config).
  - `installViaUrl` — calls the real `POST /api/extensions/install-from-url` REST endpoint against
    a running instance; hot-installs, no restart.
- [`lib/fixtures.mjs`](lib/fixtures.mjs) — wires the harness into Playwright's `test`/`expect`,
  including owner bootstrap and setup-wizard bypass for every `page` use.
- [`lib/seed-media.mjs`](lib/seed-media.mjs) — Cove has no "create a fake DB row with no file"
  endpoint; video/image import requires a real on-disk file (`POST /api/videos/from-file` calls
  `File.Exists` before doing anything else). `seedVideo()` copies a tiny real fixture (see
  `lib/fixtures-media/`) into the container and registers it through that real API, so tests
  exercise the actual import path, not a shortcut around it.
- [`lib/poll.mjs`](lib/poll.mjs) — `pollJob()`/`pollUntil()` for polling job status and eventually-
  consistent reads. Some write paths are not read-your-writes on the very next request (observed
  directly: a `GET` immediately after a `200` from an undo endpoint can still return the pre-undo
  value) — poll instead of asserting on the first read, and never paper over this with a fixed
  `sleep()`, which is either flaky (too short) or slows every run for no reason (too long).

## Scope

This harness verifies extension install/lifecycle + behavior against a real Cove instance. It does
not (yet):

- Support the GitHub-registry-backed install flow (only `install-from-url` and container-copy).
- Test Cove's own authentication/permission system. `COVE__Auth__Enabled=false` is the default for
  every test in this suite (the auth-bypass principal always carries wildcard permissions in that
  mode — confirmed by direct source read) — this harness exists to test EXTENSIONS, not Cove's own
  auth model, so permission-enforcement testing was deliberately scoped out rather than built as a
  second harness mode.
- Run true Windows containers (Cove ships no Windows container image — see
  `extensions/.planning/PROJECT.md`'s Key Decisions for the full reasoning).

See `extensions/.planning/REQUIREMENTS.md` (v2 Requirements) for what's deferred and why.
