// Core harness lifecycle: bring up an isolated Cove instance, wait for it to be ready, install an
// extension into it, and tear it down. This is the one entry point extension authors need —
// everything else (compose file, install mechanics, staging) is an implementation detail behind it.
//
// Built on Testcontainers (https://node.testcontainers.org/), not a hand-rolled `docker compose`
// child_process wrapper. Testcontainers' Ryuk sidecar guarantees container/network/volume cleanup
// even if the test process is killed (not just on a graceful exit) — a hand-rolled wrapper only
// cleans up in the success path, leaking containers on a killed run. It also owns port resolution
// and health-check waiting, removing two hand-written polling loops this file used to have.
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import { DockerComposeEnvironment, Wait } from 'testcontainers';
import { installViaContainerCopy, installViaUrl } from './install-extension.mjs';

const __dirname = dirname(fileURLToPath(import.meta.url));
const COMPOSE_DIR = join(__dirname, '..', 'docker');
const COMPOSE_FILE = 'docker-compose.yml';

// Shared-runner container cold-start is measurably slower than a dedicated dev machine's Docker
// Desktop — widen the default startup budget in CI rather than tuning it tight against local timing.
const DEFAULT_STARTUP_TIMEOUT_MS = process.env.CI ? 240_000 : 180_000;

/**
 * Brings up an isolated Cove instance and returns a handle with baseUrl + install/teardown methods.
 * Every instance gets a random project name (Testcontainers) and a random host port so parallel
 * test runs never collide.
 */
export async function startHarness({ image, timeoutMs = DEFAULT_STARTUP_TIMEOUT_MS } = {}) {
  let environment = new DockerComposeEnvironment(COMPOSE_DIR, COMPOSE_FILE)
    .withStartupTimeout(timeoutMs)
    .withWaitStrategy('cove', Wait.forHealthCheck())
    .withWaitStrategy('db', Wait.forHealthCheck());

  if (image) {
    environment = environment.withEnvironment({ COVE_E2E_IMAGE: image });
  }

  const started = await environment.up();
  let coveContainer = started.getContainer('cove-1');

  const handle = {
    get baseUrl() {
      return `http://${coveContainer.getHost()}:${coveContainer.getMappedPort(5073)}`;
    },
    get containerId() {
      return coveContainer.getId();
    },
    /** The raw Testcontainers StartedGenericContainer, for helpers (e.g. seedVideo) that need copyFilesToContainer/exec directly. */
    get container() {
      return coveContainer;
    },

    async installExtension({ publishDir, manifestPath, uiBundlePath }) {
      const result = await installViaContainerCopy({ container: coveContainer, publishDir, manifestPath, uiBundlePath });
      await coveContainer.restart();
      // A restart on a container published with an ephemeral host port can reassign a NEW host
      // port — re-fetch the started container's own view of itself rather than trusting a cached
      // port number. `restart()` mutates the same StartedGenericContainer in place (its internal
      // port-binding state is refreshed), so re-reading getMappedPort() after restart is correct.
      await waitForExtensionEnabled(handle.baseUrl, result.id, { timeoutMs });
      return result;
    },

    async installExtensionFromUrl(zipUrl) {
      const result = await installViaUrl({ baseUrl: handle.baseUrl, zipUrl });
      await waitForExtensionEnabled(handle.baseUrl, result.id ?? result.manifest?.id, { timeoutMs });
      return result;
    },

    /** Runs a command inside the Cove container (e.g. to inspect /data2 for the cross-device test). */
    exec(command) {
      return coveContainer.exec(command);
    },

    /**
     * Creates the first (owner) account and returns its access token. REQUIRED before any
     * browser-driven test: Cove's frontend (App.tsx's `showSetupWizard`) hard-gates the ENTIRE
     * app behind a first-run setup wizard whenever no owner account exists, with no way to
     * dismiss it — confirmed directly (a "Skip setup for now" click does nothing while
     * `ownerMissing` is true). This is unrelated to `COVE__Auth__Enabled=false`: the auth-bypass
     * principal used for API calls exists independently, but the UI itself still checks
     * `GET /api/auth/bootstrap-status`'s `ownerExists` field and refuses to render past the
     * wizard until an owner is created via `POST /api/auth/bootstrap-owner`. Every extension's
     * browser-driven E2E test needs this, so it lives here rather than being copy-pasted per test.
     */
    async bootstrapOwner({ username = 'e2e-owner', password = 'E2eTestPassword123!' } = {}) {
      const res = await fetch(`${handle.baseUrl}/api/auth/bootstrap-owner`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ username, password }),
      });
      if (!res.ok) {
        const body = await res.text().catch(() => '<unreadable body>');
        throw new Error(`bootstrapOwner: POST /api/auth/bootstrap-owner failed (${res.status}): ${body}`);
      }
      return res.json();
    },

    async stop() {
      await started.down({ removeVolumes: true });
    },
  };
  return handle;
}

async function waitForExtensionEnabled(baseUrl, extensionId, { timeoutMs = 60_000, intervalMs = 1000 } = {}) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    const res = await fetch(`${baseUrl}/api/extensions`).catch(() => null);
    if (res?.ok) {
      const extensions = await res.json();
      const match = extensions.find((e) => e.id === extensionId);
      if (match?.enabled) return match;
    }
    await new Promise((r) => setTimeout(r, intervalMs));
  }
  throw new Error(
    `waitForExtensionEnabled: extension "${extensionId}" was not found/enabled within ${timeoutMs}ms at ${baseUrl}/api/extensions`
  );
}
