// Core harness lifecycle: bring up an isolated Cove instance, wait for it to be ready, install an
// extension into it, and tear it down. This is the one entry point extension authors need —
// everything else (compose file, install mechanics, staging) is an implementation detail behind it.
//
// Built on Testcontainers (https://node.testcontainers.org/), not a hand-rolled `docker compose`
// child_process wrapper. Testcontainers' Ryuk sidecar guarantees container/network/volume cleanup
// even if the test process is killed (not just on a graceful exit) — a hand-rolled wrapper only
// cleans up in the success path, leaking containers on a killed run. It also owns port resolution
// and health-check waiting, removing two hand-written polling loops this file used to have.
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";
import { DockerComposeEnvironment, Wait } from "testcontainers";
import { installViaContainerCopy, installViaUrl } from "./install-extension.mjs";

const __dirname = dirname(fileURLToPath(import.meta.url));
const COMPOSE_DIR = join(__dirname, "..", "docker");
const COMPOSE_FILE = "docker-compose.yml";

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
    .withWaitStrategy("cove", Wait.forHealthCheck())
    .withWaitStrategy("db", Wait.forHealthCheck());

  if (image) {
    environment = environment.withEnvironment({ COVE_E2E_IMAGE: image });
  }

  const started = await environment.up();
  let coveContainer = started.getContainer("cove-1");

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
      const result = await installViaContainerCopy({
        container: coveContainer,
        publishDir,
        manifestPath,
        uiBundlePath,
      });
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
      await waitForExtensionEnabled(handle.baseUrl, result.id ?? result.manifest?.id, {
        timeoutMs,
      });
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
    async bootstrapOwner({ username = "e2e-owner", password = "E2eTestPassword123!" } = {}) {
      await waitForBuiltinRoles(handle.baseUrl, { timeoutMs });

      const res = await fetch(`${handle.baseUrl}/api/auth/bootstrap-owner`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ username, password }),
      });
      if (!res.ok) {
        const body = await res.text().catch(() => "<unreadable body>");
        // Cove answers an unhandled exception with a bare 500 and an empty body, so the failure as
        // thrown says nothing about its own cause. Carry the server's own log.
        throw new Error(
          [
            `bootstrapOwner: POST /api/auth/bootstrap-owner failed (${res.status}): ${body}`,
            "--- cove container log (tail) ---",
            await tailContainerLog(coveContainer),
          ].join("\n"),
        );
      }
      return res.json();
    },

    async stop() {
      await started.down({ removeVolumes: true });
    },
  };
  return handle;
}

/**
 * Blocks until Cove has seeded its built-in roles. A healthy container is NOT enough:
 * `BootstrapAuthService.StartAsync` pushes the seeding onto a background `Task.Run` and returns, so it
 * is neither awaited by host startup nor covered by the 503 maintenance gate, and `/health` answers 200
 * while it runs.
 *
 * `POST /api/auth/bootstrap-owner` inserts the Owner role itself when absent, so inside that window two
 * writers race one unique role name and the loser escapes as a bare 500. Waiting for the role removes
 * the second writer instead of retrying into the collision.
 */
async function waitForBuiltinRoles(baseUrl, { timeoutMs = 60_000, intervalMs = 250 } = {}) {
  const deadline = Date.now() + timeoutMs;
  let lastSeen = "no response yet";
  while (Date.now() < deadline) {
    const res = await fetch(`${baseUrl}/api/roles`).catch(() => null);
    if (res?.ok) {
      const roles = await res.json().catch(() => null);
      if (Array.isArray(roles)) {
        if (roles.some((role) => role?.name === "Owner")) return;
        lastSeen = `${roles.length} role(s), none named Owner`;
      } else {
        lastSeen = "GET /api/roles did not return an array";
      }
    } else if (res) {
      lastSeen = `GET /api/roles -> ${res.status}`;
    }
    await new Promise((r) => setTimeout(r, intervalMs));
  }
  throw new Error(
    `waitForBuiltinRoles: the Owner role was not seeded within ${timeoutMs}ms at ${baseUrl}/api/roles (last seen: ${lastSeen})`,
  );
}

/**
 * Best-effort tail of a container's own log. Never throws and never hangs: a diagnostic that can fail
 * the run it is trying to explain is worse than no diagnostic, so every failure mode degrades to a
 * short note.
 */
async function tailContainerLog(container, { lines = 60, timeoutMs = 5000 } = {}) {
  try {
    const stream = await container.logs();
    const chunks = [];
    stream.on("data", (chunk) => chunks.push(chunk.toString()));
    await new Promise((resolve) => {
      const done = () => resolve();
      stream.once("end", done);
      stream.once("error", done);
      setTimeout(done, timeoutMs);
    });
    const text = chunks.join("");
    return text ? text.split("\n").slice(-lines).join("\n") : "<container produced no log output>";
  } catch (err) {
    return `<container log unavailable: ${err.message}>`;
  }
}

async function waitForExtensionEnabled(
  baseUrl,
  extensionId,
  { timeoutMs = 60_000, intervalMs = 1000 } = {},
) {
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
    `waitForExtensionEnabled: extension "${extensionId}" was not found/enabled within ${timeoutMs}ms at ${baseUrl}/api/extensions`,
  );
}
