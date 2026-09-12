// A stand-in for the metadata service the Missing tab reads a catalogue from.
//
// WHY IT IS NEEDED. Cove reads a studio's scene catalogue from a hosted service of a third party's,
// behind a key. A suite that asks the real one needs that key on the machine running it, which no
// runner has, so the specs that read a catalogue used to stop early and report a pass over
// assertions they never reached.
//
// WHY IT CAN STAND IN AT ALL. The address is configuration, not a constant: Cove holds the metadata
// servers and this product reads whichever one shares a registrable domain with the identity an
// entity carries. A container answering to the service's own name on this network is therefore the
// configured server, with no change to the product and no change to what an entity's identity says.
//
// WHAT IT SERVES. The page a real instance of the service answered, captured beside the backend
// tests. It composes nothing: a query it was given no answer for gets an empty object, which is what
// the service itself returns for a collection holding nothing.
import { GenericContainer, Wait } from "testcontainers";
import { join } from "node:path";

const IMAGE = process.env.PROVIDER_STUB_IMAGE ?? "node:22-alpine";

/** The name the service answers to, which is also the alias this container takes on the network. */
const STASHDB_HOST = "stashdb.org";

/**
 * Port 80, so the configured address carries no port and stays a spelling of the real one.
 *
 * The registrable domain is what this product matches a configured server on, and a port does not
 * change it — but an address a reader has to decode is worse than one they recognise.
 */
const PORT = 80;

const SERVER_SOURCE = join(import.meta.dirname, "provider-stub-server.mjs");
const CAPTURED_PAGE = join(
  import.meta.dirname,
  "..",
  "..",
  "src",
  "WhisparrSync.Tests",
  "TestSupport",
  "Fixtures",
  "stashdb-2026-09-scene-page.json",
);

/** The address to configure Cove with, which the product resolves this stub by. */
export const STASHDB_STUB_ENDPOINT = `http://${STASHDB_HOST}/graphql`;

/**
 * The host's metadata-server entry naming this stub.
 *
 * The key is a literal: this product refuses a server carrying none, and the stub reads it for
 * nothing.
 */
export const STASHDB_STUB_SERVER = {
  endpoint: STASHDB_STUB_ENDPOINT,
  apiKey: "stub-key-not-a-credential",
  name: "stashdb",
  maxRequestsPerMinute: 6000,
};

/**
 * Starts the stub on `networkName` under the service's own name.
 *
 * @param {{networkName: string}} options
 */
export async function startProviderStub({ networkName }) {
  if (!networkName) {
    throw new Error("startProviderStub: networkName is required");
  }

  const container = await new GenericContainer(IMAGE)
    .withNetworkMode(networkName)
    .withNetworkAliases(STASHDB_HOST)
    .withCopyFilesToContainer([
      { source: SERVER_SOURCE, target: "/stub/server.mjs" },
      { source: CAPTURED_PAGE, target: "/stub/page.json" },
    ])
    .withCommand(["node", "/stub/server.mjs", "/stub/page.json", String(PORT)])
    .withWaitStrategy(Wait.forLogMessage(/provider-stub serving/))
    .withStartupTimeout(60_000)
    .start();

  return {
    endpoint: STASHDB_STUB_ENDPOINT,

    /** Every query the stub was asked, as it recorded them. */
    async asked() {
      const stream = await container.logs();
      return new Promise((resolve) => {
        let seen = "";
        const answer = () => resolve(seen.split(/\r?\n/).filter((line) => line.includes("ASKED")));
        stream.on("data", (chunk) => (seen += String(chunk)));
        stream.on("end", answer);
        // A log stream that stays open would never end on its own.
        setTimeout(answer, 3000);
      });
    },

    stop: () => container.stop(),
  };
}

/**
 * Points the running Cove at `servers`, replacing whatever metadata servers it declares.
 *
 * Every wanted server is written in ONE call, because the element is the whole list: a second call
 * naming one server takes the first one away. Answers the configuration that was written, so a
 * caller can assert it took.
 */
export async function configureProviderStub(api, servers = [STASHDB_STUB_SERVER]) {
  const read = await api.get("/api/system/config");
  if (read.status >= 300) {
    throw new Error(`configureProviderStub: GET /api/system/config answered ${read.status}`);
  }

  const config = read.json;
  config.scraping.metadataServers = [...servers];

  const saved = await api.put("/api/system/config", config);
  if (saved.status >= 300) {
    throw new Error(
      `configureProviderStub: PUT /api/system/config answered ${saved.status}: ${String(saved.text).slice(0, 300)}`,
    );
  }
  return saved.json;
}
