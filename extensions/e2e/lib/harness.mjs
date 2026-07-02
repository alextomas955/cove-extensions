// Core harness lifecycle: bring up an isolated Cove instance, wait for it to be ready, install an
// extension into it, and tear it down. This is the one entry point extension authors need —
// everything else (compose file, install mechanics, staging) is an implementation detail behind it.
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import { installViaContainerCopy, installViaUrl } from './install-extension.mjs';

const __dirname = dirname(fileURLToPath(import.meta.url));
const COMPOSE_FILE = join(__dirname, '..', 'docker', 'docker-compose.yml');

function compose(args, opts = {}) {
  return execFileSync('docker', ['compose', '-f', COMPOSE_FILE, ...args], {
    encoding: 'utf8',
    stdio: ['ignore', 'pipe', 'pipe'],
    ...opts,
  });
}

function randomProjectName() {
  return `cove-e2e-${Math.random().toString(36).slice(2, 10)}`;
}

async function waitForHealthy(project, service, { timeoutMs = 120_000, intervalMs = 1000 } = {}) {
  const containerName = `${project}-${service}-1`;
  const deadline = Date.now() + timeoutMs;
  let lastStatus = 'unknown';
  while (Date.now() < deadline) {
    try {
      lastStatus = execFileSync(
        'docker',
        ['inspect', containerName, '--format', '{{.State.Health.Status}}'],
        { encoding: 'utf8' }
      ).trim();
    } catch {
      lastStatus = 'not-found';
    }
    if (lastStatus === 'healthy') return;
    if (lastStatus === 'unhealthy') {
      let logs = '';
      try {
        logs = execFileSync('docker', ['logs', '--tail', '80', containerName], { encoding: 'utf8' });
      } catch {
        // best-effort diagnostics only
      }
      throw new Error(
        `waitForHealthy: ${containerName} reported unhealthy before timeout.\nLast 80 log lines:\n${logs}`
      );
    }
    await new Promise((r) => setTimeout(r, intervalMs));
  }
  throw new Error(
    `waitForHealthy: ${containerName} did not become healthy within ${timeoutMs}ms (last status: ${lastStatus}). ` +
      `Check \`docker logs ${containerName}\` for details.`
  );
}

function resolvePort(project, service, containerPort) {
  const out = execFileSync('docker', [
    'compose',
    '-f',
    COMPOSE_FILE,
    '-p',
    project,
    'port',
    service,
    String(containerPort),
  ]).toString().trim();
  const port = out.split(':').pop();
  if (!port || Number.isNaN(Number(port))) {
    throw new Error(`resolvePort: could not resolve host port for ${service}:${containerPort} (got "${out}")`);
  }
  return Number(port);
}

/**
 * Brings up an isolated Cove instance and returns a handle with baseUrl + install/teardown methods.
 * Every instance gets a random project name and a random host port so parallel test runs never collide.
 */
export async function startHarness({ image, timeoutMs } = {}) {
  const project = randomProjectName();
  const env = { ...process.env };
  if (image) env.COVE_E2E_IMAGE = image;

  compose(['-p', project, 'up', '-d', '--wait'], { env });

  const containerName = `${project}-cove-1`;
  await waitForHealthy(project, 'cove', { timeoutMs });

  // `docker restart` on a container published with an ephemeral host port (`ports: ["0:5073"]`)
  // can reassign a NEW host port — Docker does not guarantee the same ephemeral port survives a
  // restart. baseUrl is therefore re-resolved after every restart, not cached from container start.
  const handle = {
    project,
    containerName,
    get baseUrl() {
      return `http://localhost:${resolvePort(project, 'cove', 5073)}`;
    },

    async installExtension({ publishDir, manifestPath, uiBundlePath }) {
      const result = installViaContainerCopy({
        containerName,
        publishDir,
        manifestPath,
        uiBundlePath,
      });
      execFileSync('docker', ['restart', containerName]);
      await waitForHealthy(project, 'cove', { timeoutMs });
      await waitForExtensionEnabled(handle.baseUrl, result.id, { timeoutMs });
      return result;
    },

    async installExtensionFromUrl(zipUrl) {
      const result = await installViaUrl({ baseUrl: handle.baseUrl, zipUrl });
      await waitForExtensionEnabled(handle.baseUrl, result.id ?? result.manifest?.id, { timeoutMs });
      return result;
    },

    async stop() {
      compose(['-p', project, 'down', '-v'], { env });
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
