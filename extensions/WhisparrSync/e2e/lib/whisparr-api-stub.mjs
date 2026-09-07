// A one-route stand-in for Whisparr's own API, joined to the harness network so the COVE container
// reaches it by alias. It exists because the library-wide status summary pairs a Whisparr call with a Cove
// library read: with nothing answering `GET /api/v3/movie` the join degrades to an empty index and the
// library half — the half where a hidden write could hide — proves nothing.
//
// Deliberately not the real Whisparr container: this tier asserts the extension's own read path (movie
// list → library fold → join → wire envelope), not Whisparr's behavior, and a browser-tier spec
// that booted an acquisition stack would be paying minutes for facts the node-tests tier already owns.
//
// CONTENT SAFETY: the caller supplies the rows, so nothing real is baked in here. Every caller passes
// synthetic titles and fabricated paths; no API key is meaningful (the stub authenticates nothing).
import { GenericContainer, Wait } from 'testcontainers';

const IMAGE = process.env.WHISPARR_API_STUB_IMAGE ?? 'node:22-alpine';
const ALIAS = 'whisparr-api-stub';
const PORT = 6979;

/**
 * Starts the stub under the alias `whisparr-api-stub` on {@link networkName}, answering
 * `GET /api/v3/movie` with {@link movies} and every other path with a 404 `[]`.
 *
 * @param {{ networkName: string, movies?: unknown[], openApiPaths?: string[] }} opts - the harness's Docker
 *   network (read from the Cove container's own `getNetworkNames()`), the movie rows to serve, and — when a
 *   caller needs the instance to DECLARE its surface — the path keys its API description lists.
 *
 *   `openApiPaths` is what separates "this build does not carry that route" from "this instance did not
 *   answer": the capability read refuses only on a document that ARRIVED and declared no such route, so a
 *   caller inducing that refusal must serve a document rather than withhold one. Omitted, the description is
 *   404ed exactly as every other path is, which is the did-not-answer case.
 * @returns {Promise<{ baseUrlFromCove: string, baseUrlFromHost: string, stop: () => Promise<void> }>}
 *   `baseUrlFromCove` is what goes into the extension's stored options; `baseUrlFromHost` lets the test
 *   process confirm the stub itself is up before blaming the extension for an empty read.
 */
export async function startWhisparrApiStub({ networkName, movies = [], openApiPaths }) {
  if (!networkName) {
    throw new Error('startWhisparrApiStub: networkName is required');
  }

  // The Content-Type is load-bearing, not incidental: WhisparrClient guards on an exact
  // `application/json` media type before deserializing, so a text/plain answer classifies as
  // "not the Whisparr API" and the read fails as an outage.
  const script = `
const http = require('http');
const MOVIES = ${JSON.stringify(movies)};
const OPENAPI_PATHS = ${JSON.stringify(openApiPaths ?? null)};
const server = http.createServer((req, res) => {
  const path = req.url.split('?')[0];
  if (OPENAPI_PATHS !== null && req.method === 'GET' && path === '/docs/v3/openapi.json') {
    const paths = {};
    for (const declared of OPENAPI_PATHS) paths[declared] = {};
    res.writeHead(200, { 'Content-Type': 'application/json' });
    res.end(JSON.stringify({ paths }));
    return;
  }
  const hit = req.method === 'GET' && path === '/api/v3/movie';
  res.writeHead(hit ? 200 : 404, { 'Content-Type': 'application/json' });
  res.end(JSON.stringify(hit ? MOVIES : []));
});
server.listen(${PORT}, '0.0.0.0');
`;

  const container = await new GenericContainer(IMAGE)
    .withNetworkMode(networkName)
    .withNetworkAliases(ALIAS)
    .withExposedPorts(PORT)
    .withCopyContentToContainer([{ content: script, target: '/stub/server.cjs' }])
    .withCommand(['node', '/stub/server.cjs'])
    .withWaitStrategy(Wait.forListeningPorts())
    .withStartupTimeout(60_000)
    .start();

  return {
    baseUrlFromCove: `http://${ALIAS}:${PORT}`,
    baseUrlFromHost: `http://${container.getHost()}:${container.getMappedPort(PORT)}`,
    async stop() {
      await container.stop();
    },
  };
}
