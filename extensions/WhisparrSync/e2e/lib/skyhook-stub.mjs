// An offline record-and-replay stand-in for Whisparr's hosted metadata source (api.whisparr.com).
// It exists so identity resolution runs deterministically with NO outbound egress and NO secret: a
// fork PR (which has no credentials and no network to the real service) must still resolve a real id
// to at least one monitorable row. Whisparr's <WhisparrMetadata> URL is pointed here, so every
// studio/site/scene lookup is answered from committed recordings instead of the live service.
//
// Structural offline guarantee: there is NO forwarding/upstream code path in this server — a request
// with no matching recording returns an empty array, it can never fall through to api.whisparr.com.
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import { readFileSync } from 'node:fs';
import { GenericContainer, Wait } from 'testcontainers';

const HERE = dirname(fileURLToPath(import.meta.url)); // …/extensions/WhisparrSync/e2e/lib
const SKYHOOK_DIR = join(HERE, '..', 'fixtures', 'skyhook');
const ALIAS = 'skyhook-stub';
const STUB_PORT = 9797;

// The stub runs Node's own http server inside a stock Node image; no npm install is needed (node:http
// only), keeping the stub free of any package install. Overridable for environments that
// pin a locally-cached Node image instead of pulling the default.
const STUB_IMAGE = process.env.SKYHOOK_STUB_IMAGE ?? 'node:22-alpine';

/**
 * Loads one `index.json` and its referenced recordings into a `{ route: responseJson }` map.
 *
 * Content-safety exception (fixtures/skyhook only): those committed recordings are the suite's small
 * allowlist of real, SFW metadata-source ids / names / descriptions — never images, never explicit text
 * (scrubbed per fixtures/skyhook/README.md). The exception exists because Whisparr validates every
 * add/monitor/search id against its metadata source, so fully-synthetic ids resolve to zero rows and
 * cannot prove sync. A caller-supplied directory carries no such exception and states its own rule.
 */
function loadRecordings(dir) {
  const index = JSON.parse(readFileSync(join(dir, 'index.json'), 'utf8'));
  const recordings = {};
  for (const [route, file] of Object.entries(index)) {
    recordings[route] = JSON.parse(readFileSync(join(dir, file), 'utf8'));
  }
  return recordings;
}

/**
 * Starts the replay stub as a container joined to {@link networkName} under the fixed alias
 * `skyhook-stub`, so a Whisparr container on the same network resolves it by name (a container alias
 * is CI-portable in a way `host.docker.internal` is not on Linux runners). The recordings are baked
 * into the served script so the container needs no bind-mount and no fixture path resolution.
 *
 * @param {{ networkName: string, extraRecordingsDir?: string, extraRecordings?: Record<string, unknown> }} opts
 *   - `networkName` the shared Docker network the Whisparr container is on.
 *   - `extraRecordingsDir` a second directory holding its own `index.json`, merged OVER the captured
 *     corpus so a caller can serve additional routes without editing the captured contract. It adds
 *     recordings only — the structural offline guarantee above is a property of the served script,
 *     which has no upstream path either way.
 *   - `extraRecordings` an already-built `{ route: responseJson }` map, merged last. It exists for a
 *     corpus generated per run rather than committed: writing a hundred records to disk to feed one
 *     measurement would leave a hundred files behind that no other spec reads.
 * @returns a handle with `urlFromWhisparr` (the `<WhisparrMetadata>` URL template, keeping the
 *   `{route}` token Whisparr substitutes) and `stop()`.
 */
export async function startSkyHookStub({ networkName, extraRecordingsDir, extraRecordings } = {}) {
  if (!networkName) {
    throw new Error('startSkyHookStub: networkName is required');
  }
  const recordings = {
    ...loadRecordings(SKYHOOK_DIR),
    ...(extraRecordingsDir ? loadRecordings(extraRecordingsDir) : {}),
    ...(extraRecordings ?? {}),
  };

  // A hit returns the recorded at-least-one-row JSON; a miss returns [] with 404 (never a 500, never
  // an upstream call). Keyed by the exact SkyHook path+query captured live in index.json.
  const script = `
const http = require('http');
const RECORDINGS = ${JSON.stringify(recordings)};
const server = http.createServer((req, res) => {
  const hit = Object.prototype.hasOwnProperty.call(RECORDINGS, req.url) ? RECORDINGS[req.url] : null;
  res.writeHead(hit ? 200 : 404, { 'Content-Type': 'application/json' });
  res.end(hit ? JSON.stringify(hit) : '[]');
});
server.listen(${STUB_PORT}, '0.0.0.0');
`;

  const container = await new GenericContainer(STUB_IMAGE)
    .withNetworkMode(networkName)
    .withNetworkAliases(ALIAS)
    .withExposedPorts(STUB_PORT)
    .withCopyContentToContainer([{ content: script, target: '/stub/server.cjs' }])
    .withCommand(['node', '/stub/server.cjs'])
    .withWaitStrategy(Wait.forListeningPorts())
    .withStartupTimeout(60_000)
    .start();

  const host = container.getHost();
  const mappedPort = container.getMappedPort(STUB_PORT);

  return {
    container,
    alias: ALIAS,
    port: STUB_PORT,
    /** The `<WhisparrMetadata>` URL template Whisparr writes into config.xml (keeps the `{route}` token). */
    urlFromWhisparr: `http://${ALIAS}:${STUB_PORT}/{route}`,
    /** The base the Whisparr container reaches over the shared network (no `{route}` token). */
    baseUrlFromWhisparr: `http://${ALIAS}:${STUB_PORT}`,
    /** The base the TEST process reaches on the mapped host port (for a direct stub assertion). */
    baseUrlFromHost: `http://${host}:${mappedPort}`,
    async stop() {
      await container.stop();
    },
  };
}
