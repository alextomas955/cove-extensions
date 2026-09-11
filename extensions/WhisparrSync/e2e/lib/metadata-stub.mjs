// A stand-in for the hosted metadata service the instance resolves every identifier through.
//
// WHY IT IS NEEDED. Neither generation calls the metadata source directly. Every lookup goes to a
// hosted service of the vendor's, which no sealed container run can reach, so an entity read that
// resolves an identifier answers "the instance refused" and no monitoring can be driven at all.
//
// WHY IT SERVES SYNTHETIC ROWS. It answers for the rows the caller seeded and for nothing else. That
// is possible because a spec using this owns both sides: it wrote the catalogue row, so it knows the
// identifier and can say what a lookup of it returns. Replaying recordings of the real service would
// put a copy of somebody else's catalogue in this repository to prove something the spec already
// controls.
//
// IT REACHES NOTHING. There is no forwarding path in the served script. A request matching no seeded
// row answers an empty array, which is the same answer the real service gives for an identifier it
// does not know, and it cannot fall through to the real service.
import { GenericContainer, Wait } from "testcontainers";

const IMAGE = process.env.METADATA_STUB_IMAGE ?? "node:22-alpine";
const ALIAS = "metadata-stub";
const PORT = 9797;

/**
 * Starts the stub on `networkName` under the alias `metadata-stub`, answering a lookup of any of
 * `sites` with that one row.
 *
 * `sites` are `{ tvdbId, title, titleSlug }`. The identifier is the field the instance's own answer
 * is read by, so it has to be the one the catalogue row carries.
 *
 * The returned `urlFromWhisparr` keeps the `{route}` token the instance substitutes, because the
 * config element it goes into is a template and not an address.
 *
 * @param {{ networkName: string, sites: { tvdbId: number, title: string, titleSlug: string }[] }} options
 */
export async function startMetadataStub({ networkName, sites }) {
  if (!networkName) {
    throw new Error("startMetadataStub: networkName is required");
  }
  if (!Array.isArray(sites) || sites.length === 0) {
    throw new Error(
      "startMetadataStub: no sites given. A stub answering nothing is a stub every lookup refuses, which is the state it exists to remove.",
    );
  }

  const script = `
const http = require('http');
const SITES = ${JSON.stringify(sites)};
const PORT = ${PORT};

// The term the instance asks by carries the identifier somewhere in it, in a spelling that differs
// per route. Matching on the digits it contains is what lets one stub answer every shape of lookup
// without this file having to know the vendor's route table.
function matching(url) {
  const digits = (decodeURIComponent(url).match(/\\d+/g) || []).map(Number);
  return SITES.filter((site) => digits.includes(site.tvdbId));
}

// The instance maps the service's row onto its own model, and the field it reads the identifier from
// carries the SERVICE's name, which the vendor publishes no contract for. Measured: a row carrying
// only tvdbId comes back out of the instance's own lookup as tvdbId 0, so that is not it. Which of
// the spellings below is the one has not been established, and emitting all of them is what avoids
// having to establish it: the instance takes the one it maps and drops the rest, because its model
// declares no property for them.
function row(site) {
  return {
    ...site,
    id: site.tvdbId,
    tpdbId: site.tvdbId,
    foreignId: String(site.tvdbId),
    siteId: site.tvdbId,
    network: site.title,
    overview: '',
    images: [],
    seasons: [],
  };
}

const server = http.createServer((req, res) => {
  const found = matching(req.url || '');
  res.writeHead(200, { 'content-type': 'application/json' });
  // One row, or none. Never several: the caller's own correspondence between the identifier it holds
  // and the entity acted on rests on there being exactly one answer.
  res.end(JSON.stringify(found.slice(0, 1).map(row)));
});
server.listen(PORT, '0.0.0.0', () => console.log('metadata-stub serving ' + SITES.length + ' site(s) on ' + PORT));
`;

  const container = await new GenericContainer(IMAGE)
    .withNetworkMode(networkName)
    .withNetworkAliases(ALIAS)
    .withExposedPorts(PORT)
    .withCommand(["node", "-e", script])
    .withWaitStrategy(Wait.forListeningPorts())
    .withStartupTimeout(60_000)
    .start();

  return {
    urlFromWhisparr: `http://${ALIAS}:${PORT}/{route}`,
    urlFromHost: `http://${container.getHost()}:${container.getMappedPort(PORT)}`,
    stop: () => container.stop(),
  };
}
