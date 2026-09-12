// A stand-in for the metadata service the older generation identifies entities against.
//
// WHY IT IS NEEDED. This product refuses to read anything for an entity whose source the host names
// no server for, and a server carrying no key is the same refusal. A suite that registered the real
// service would need that service's key on the machine running it, which no runner has, so every
// read on this generation stopped at "no provider is configured" before any surface was reached.
//
// WHAT IT REACHES, AND WHAT IT DOES NOT. Registering this container as the host's server for that
// source is what makes the source RESOLVE, which is the gate above. It is not what makes the
// catalogue readable: this generation's catalogue client builds its request against a compiled-in
// `https://api.theporndb.net` (Providers/ProviderEndpointPort.cs, read at
// Providers/ThePornDbCatalogue.cs) and never consults the configuration, so a catalogue read leaves
// for an address on the open internet whatever is registered here. Reaching it would need a
// generated certificate installed into the Cove container's trust store, which this suite does not
// do. The stub's own log is the evidence of which of the two happened on any given run.
//
// WHAT IT SERVES. The page a real instance of the service answered, captured beside the backend
// tests.
import { GenericContainer, Wait } from "testcontainers";
import { join } from "node:path";

const IMAGE = process.env.PROVIDER_STUB_IMAGE ?? "node:22-alpine";

/** The name the service stamps identity under, which is also the alias this container takes. */
const THEPORNDB_HOST = "theporndb.net";

/** @see provider-stub.mjs — port 80 for the same reason, so the address stays a spelling of the real one. */
const PORT = 80;

const SERVER_SOURCE = join(import.meta.dirname, "theporndb-stub-server.mjs");
const CAPTURED_PAGE = join(
  import.meta.dirname,
  "..",
  "..",
  "src",
  "WhisparrSync.Tests",
  "TestSupport",
  "Fixtures",
  "theporndb-2026-09-scenes-page.json",
);

/** The address to register Cove's server at, which the product resolves this stub by. */
export const THEPORNDB_STUB_ENDPOINT = `http://${THEPORNDB_HOST}/graphql`;

/** The host's metadata-server entry naming this stub. @see provider-stub.mjs `STASHDB_STUB_SERVER` */
export const THEPORNDB_STUB_SERVER = {
  endpoint: THEPORNDB_STUB_ENDPOINT,
  apiKey: "stub-key-not-a-credential",
  name: "theporndb",
  maxRequestsPerMinute: 6000,
};

/**
 * Starts the stub on `networkName` under the service's own name.
 *
 * @param {{networkName: string}} options
 */
export async function startThePornDbStub({ networkName }) {
  if (!networkName) {
    throw new Error("startThePornDbStub: networkName is required");
  }

  const container = await new GenericContainer(IMAGE)
    .withNetworkMode(networkName)
    .withNetworkAliases(THEPORNDB_HOST)
    .withCopyFilesToContainer([
      { source: SERVER_SOURCE, target: "/stub/server.mjs" },
      { source: CAPTURED_PAGE, target: "/stub/page.json" },
    ])
    .withCommand(["node", "/stub/server.mjs", "/stub/page.json", String(PORT)])
    .withWaitStrategy(Wait.forLogMessage(/theporndb-stub serving/))
    .withStartupTimeout(60_000)
    .start();

  return {
    endpoint: THEPORNDB_STUB_ENDPOINT,

    /** Every request the stub was asked, as it recorded them. */
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
