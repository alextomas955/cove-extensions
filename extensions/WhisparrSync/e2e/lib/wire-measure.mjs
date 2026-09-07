// A Whisparr-only bring-up for reading answers off a live instance, plus the record-or-verify writer the
// recorded answers are committed through.
//
// Cove is deliberately absent: nothing measured here reads a Cove surface, and the Cove bring-up costs
// minutes of runtime for no evidence. Whisparr and the metadata stub come from the SAME modules the full
// harness uses (`whisparr-container.mjs`, `skyhook-stub.mjs`) rather than a second bring-up, so a spec
// here cannot silently diverge from the instance every other spec runs against.
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';
import { Network } from 'testcontainers';
import { startSkyHookStub } from './skyhook-stub.mjs';
import { startWhisparr } from './whisparr-container.mjs';

const HERE = dirname(fileURLToPath(import.meta.url)); // …/extensions/WhisparrSync/e2e/lib
const WIRE_SEED_DIR = join(HERE, '..', 'fixtures', 'wire-seed');
const WIRE_ARTIFACT_DIR = join(HERE, '..', 'fixtures', 'wire');
const WHISPARR_PORT = 6969;

/**
 * Boots the metadata stub and one Whisparr instance on a private network and returns a handle carrying
 * the instance's own version string, an API helper, the host-config read/write/restart seam the
 * cache-flag legs drive, and a teardown.
 *
 * @param {{ version?: 'v2'|'v3', extraRecordings?: Record<string, unknown> }} opts
 *   - `extraRecordings` metadata records generated for this run rather than committed, for a corpus whose
 *     size is the variable under measurement.
 */
export async function startWireInstance({ version = 'v3', extraRecordings } = {}) {
  const network = await new Network().start();
  let stub;
  let whisparr;
  try {
    stub = await startSkyHookStub({
      networkName: network.getName(),
      extraRecordingsDir: WIRE_SEED_DIR,
      extraRecordings,
    });
    whisparr = await startWhisparr({
      networkName: network.getName(),
      version,
      metadataUrl: stub.urlFromWhisparr,
    });
  } catch (error) {
    // Tear down whatever did come up; a half-started bring-up would otherwise strand a container
    // holding a mapped port and a live API key.
    await stopAll([whisparr, stub], network);
    throw error;
  }

  // The mapped port moves across a container restart, so every call resolves it from the container's own
  // current view rather than from a value cached at startup.
  const baseUrl = () => `http://${whisparr.container.getHost()}:${whisparr.container.getMappedPort(WHISPARR_PORT)}`;

  async function api(method, path, body) {
    const res = await fetch(`${baseUrl()}${path}`, {
      method,
      headers: {
        'X-Api-Key': whisparr.apiKey,
        ...(body === undefined ? {} : { 'Content-Type': 'application/json' }),
      },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
    const text = await res.text();
    let json;
    try {
      json = text ? JSON.parse(text) : undefined;
    } catch {
      json = undefined;
    }
    return { status: res.status, json, text, contentType: res.headers.get('content-type') };
  }

  const status = await api('GET', '/api/v3/system/status');
  const instanceVersion = status.json?.version;
  if (!instanceVersion) {
    await stopAll([whisparr, stub], network);
    throw new Error('startWireInstance: the instance did not report a version at /api/v3/system/status');
  }

  async function waitForPing(timeoutMs = 180_000) {
    const deadline = Date.now() + timeoutMs;
    while (Date.now() < deadline) {
      const ok = await fetch(`${baseUrl()}/ping`).then((r) => r.ok).catch(() => false);
      if (ok) return;
      await delay(1000);
    }
    throw new Error('startWireInstance: the instance did not answer /ping after a restart');
  }

  return {
    whisparr,
    stub,
    network,
    instanceVersion,
    apiKey: whisparr.apiKey,
    get baseUrlFromHost() {
      return baseUrl();
    },
    api,
    /** The full host-config resource, including the four `whisparrCache*API` flags. */
    async readHostConfig() {
      const res = await api('GET', '/api/v3/config/host');
      return res.json;
    },
    /**
     * Writes {@link patch} over the current host config and returns the config as the instance reports
     * it AFTER the write. Callers assert on the returned value, never on what they sent — the flag state
     * a measurement was taken under is a condition of the reading, not an assumption.
     */
    async writeHostConfig(patch) {
      const current = await api('GET', '/api/v3/config/host');
      await api('PUT', `/api/v3/config/host/${current.json.id}`, { ...current.json, ...patch });
      const after = await api('GET', '/api/v3/config/host');
      return after.json;
    },
    /** Restarts the container and waits for it to answer `/ping`, so the next call sees an empty in-memory cache. */
    async restartInstance() {
      await whisparr.container.restart();
      await waitForPing();
    },
    async stop() {
      await stopAll([whisparr, stub], network);
    },
  };
}

// Best-effort and in reverse order: one failed teardown must not strand the containers behind it.
async function stopAll(handles, network) {
  for (const handle of [...handles].reverse()) {
    await handle?.stop?.().catch(() => {});
  }
  await network?.stop?.().catch(() => {});
}

/**
 * Records {@link value} under `fixtures/wire/{name}` when `WIRE_RECORD=1`, and otherwise compares the
 * captured value against the committed file and throws on any difference.
 *
 * The split is what lets a captured answer be both a committed artifact and a live assertion: an
 * ordinary run leaves the working tree untouched while still failing when the wire changes, and a
 * deliberate re-record is an explicit act with a visible diff.
 */
export function recordArtifact(name, value) {
  const path = join(WIRE_ARTIFACT_DIR, name);
  const serialized = `${JSON.stringify(value, null, 2)}\n`;
  if (process.env.WIRE_RECORD === '1') {
    mkdirSync(WIRE_ARTIFACT_DIR, { recursive: true });
    writeFileSync(path, serialized);
    return { recorded: true, path };
  }
  if (!existsSync(path)) {
    throw new Error(`recordArtifact: ${name} is not committed. Re-run with WIRE_RECORD=1 to capture it.`);
  }
  const committed = readFileSync(path, 'utf8');
  if (committed !== serialized) {
    throw new Error(
      `recordArtifact: the live wire no longer matches the committed ${name}.\n` +
        'Read the difference before re-recording — it is a Whisparr behaviour change, not a stale file.\n' +
        diffSummary(JSON.parse(committed), value),
    );
  }
  return { recorded: false, path };
}

// Names the first differing path rather than dumping two documents, so the failure says what moved.
function diffSummary(committed, live, path = '') {
  if (JSON.stringify(committed) === JSON.stringify(live)) return '';
  const bothObjects = committed && live && typeof committed === 'object' && typeof live === 'object';
  if (!bothObjects) {
    return `  at ${path || '(root)'}: committed ${JSON.stringify(committed)} — live ${JSON.stringify(live)}`;
  }
  const keys = [...new Set([...Object.keys(committed), ...Object.keys(live)])];
  for (const key of keys) {
    const summary = diffSummary(committed[key], live[key], path ? `${path}.${key}` : key);
    if (summary) return summary;
  }
  return '';
}
