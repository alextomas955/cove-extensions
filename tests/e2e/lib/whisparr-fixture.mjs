// Brings Whisparr containers up beside an already-running harness Cove, on that instance's own
// network, each answering an API key this module minted. Opt-in: the shared compose file is
// untouched, so a suite that never calls this pays neither the image pull nor the boot.
//
// Built on Testcontainers like the harness itself, so its Ryuk sidecar reaps whatever this starts
// even when the test process is killed rather than exiting.
import { GenericContainer, Wait } from "testcontainers";
import { createApiClient } from "./apiClient.mjs";
import { whisparrImage } from "./whisparr-images.mjs";
import { buildConfigXml, seedHistory } from "./whisparr-seed.mjs";

const WHISPARR_PORT = 6969;

// Both generations serve this under `/api/v3` — v2's Sonarr-lineage API carries the same prefix — so
// the path does not tell the two apart. The version in its body does.
const STATUS_PATH = "/api/v3/system/status";

// A shared runner cold-starts containers more slowly than a dev machine's Docker Desktop, so the CI
// budget is the wider one rather than local timing tuned tight.
const DEFAULT_STARTUP_TIMEOUT_MS = process.env.CI ? 240_000 : 180_000;

const STARTUP_LOG_LINES = 60;

/**
 * The key every container this module starts answers to.
 *
 * Synthetic and committed on purpose: it authorises nothing outside a container started here, and a
 * reader must not be able to mistake it for a credential lifted from a real install.
 */
export const FIXTURE_API_KEY = "0e2e0e2e0e2e0e2e0e2e0e2e0e2e0e2e";

const aliasFor = (generation) => `whisparr-${generation}`;

/**
 * Starts one Whisparr container per requested generation on `network`, each pre-seeded with
 * `apiKey`, and returns a handle addressing them.
 *
 * `network` is one of the started Cove container's own networks — read off it
 * (`harness.container.getNetworkNames()[0]`) rather than named, which is what keeps this independent
 * of which Cove image the run booted.
 *
 * CALLER CONTRACT: this handle's `stop()` must run BEFORE the harness's own. A compose down removes
 * the project network, and the daemon refuses to remove a network that still has an attached
 * endpoint, so a container left running here turns teardown into an error naming neither this module
 * nor the cause.
 *
 * `seedHistory` asks for instances that already have an import past, which is what makes a claim
 * about a FIRST synchronisation pass falsifiable — against an empty instance, "it imported nothing"
 * and "it did nothing" are the same observation.
 *
 * @param {{network: string, generations?: ("v3"|"v2")[], apiKey?: string, startupTimeoutMs?: number,
 *          seedHistory?: boolean|{count?: number}}} options
 */
export async function startWhisparr({
  network,
  generations = ["v3", "v2"],
  apiKey = FIXTURE_API_KEY,
  startupTimeoutMs = DEFAULT_STARTUP_TIMEOUT_MS,
  seedHistory: history = false,
} = {}) {
  if (!network) {
    throw new Error(
      "startWhisparr: no network given; pass one of the started Cove container's own networks, e.g. harness.container.getNetworkNames()[0].",
    );
  }

  // Concurrently: a cold boot costs about the same for each generation, so starting them in sequence
  // would pay that twice for containers that share nothing.
  const outcomes = await Promise.allSettled(
    generations.map((generation) =>
      startGeneration(generation, { network, apiKey, startupTimeoutMs }),
    ),
  );
  const failure = outcomes.find((outcome) => outcome.status === "rejected");
  if (failure) {
    // A sibling that DID start still holds an endpoint on the harness's network, and the compose
    // teardown that follows cannot remove a network while one is attached. The start failure is the
    // error worth raising, so a failure to clean up behind it does not displace it.
    await Promise.allSettled(
      outcomes.filter((o) => o.status === "fulfilled").map((o) => o.value.stop()),
    );
    throw failure.reason;
  }

  const instances = Object.fromEntries(
    generations.map((generation, index) => [
      generation,
      instanceHandle(outcomes[index].value, generation, apiKey),
    ]),
  );

  const handle = {
    ...instances,
    generations: [...generations],
    apiKey,

    /**
     * A JSON client over one generation, presenting the seeded key on every request. Its base URL is
     * re-read per call rather than captured, matching the shared client's own contract.
     */
    apiFor(generation) {
      const instance = instances[generation];
      if (instance === undefined) {
        throw new Error(
          `startWhisparr: apiFor("${generation}") — this call started ${handle.generations.join(", ") || "nothing"}.`,
        );
      }
      return createApiClient(() => instance.baseUrl, undefined, {
        headers: { "X-Api-Key": apiKey },
      });
    },

    /**
     * Gives one generation an import past, and records what the instance itself rendered for each
     * event type it now holds — also reachable afterwards as `handle[generation].history`.
     */
    async seedHistory(generation, options = {}) {
      const instance = instances[generation];
      if (instance === undefined) {
        throw new Error(
          `startWhisparr: seedHistory("${generation}") — this call started ${handle.generations.join(", ") || "nothing"}.`,
        );
      }
      instance.history = await seedHistory({
        container: instance.container,
        api: handle.apiFor(generation),
        generation,
        ...options,
      });
      return instance.history;
    },

    async stop() {
      await Promise.all(Object.values(instances).map((instance) => instance.container.stop()));
    },
  };

  if (history) {
    // Anything that fails here leaves started containers holding endpoints on the harness's
    // network, and the compose teardown that follows cannot remove a network while one is attached.
    try {
      await Promise.all(
        generations.map((generation) =>
          handle.seedHistory(generation, history === true ? {} : history),
        ),
      );
    } catch (cause) {
      await handle.stop().catch(() => {});
      throw cause;
    }
  }

  return handle;
}

function instanceHandle(container, generation, apiKey) {
  return {
    get baseUrl() {
      return `http://${container.getHost()}:${container.getMappedPort(WHISPARR_PORT)}`;
    },
    /** The raw Testcontainers StartedGenericContainer, for helpers needing exec/copy directly. */
    get container() {
      return container;
    },
    alias: aliasFor(generation),
    apiKey,
    /** What `seedHistory` last observed on this instance; undefined until something seeds it. */
    history: undefined,
  };
}

async function startGeneration(generation, { network, apiKey, startupTimeoutMs }) {
  const image = whisparrImage(generation);

  // Testcontainers stops and REMOVES a container whose wait strategy failed before start() rejects,
  // so the log has to be captured while the container still runs. This is the whole diagnostic in
  // the case that matters: a container can stay up with its app dead and answer nothing, and a bare
  // wait timeout then names the wrong cause entirely.
  const logChunks = [];

  const builder = withApiKeySeed(new GenericContainer(image), generation, apiKey)
    .withNetworkMode(network)
    // An alias is scoped to the network; a container NAME is daemon-global and would collide the
    // moment two harnesses run at once.
    .withNetworkAliases(aliasFor(generation))
    // The container port only, so Testcontainers publishes it on an ephemeral host port of its own
    // choosing. A fixed one would compete with whatever else this machine is already serving.
    .withExposedPorts(WHISPARR_PORT)
    .withLogConsumer((stream) => {
      stream.on("data", (chunk) => {
        logChunks.push(chunk.toString());
        if (logChunks.length > STARTUP_LOG_LINES) logChunks.shift();
      });
    })
    // Neither image declares a HEALTHCHECK, so the health-check strategy the harness waits on for
    // Cove is unavailable here. HTTP against the app's own status route is the stronger gate anyway:
    // it proves the key seed, which a startup log line would not.
    .withWaitStrategy(
      Wait.forHttp(STATUS_PATH, WHISPARR_PORT)
        .withHeaders({ "X-Api-Key": apiKey })
        .forStatusCode(200),
    )
    .withStartupTimeout(startupTimeoutMs);

  try {
    return await builder.start();
  } catch (cause) {
    throw new Error(
      [
        `startWhisparr: ${generation} (${image}) did not answer ${STATUS_PATH} with the seeded key within ${startupTimeoutMs}ms (${cause?.message ?? cause})`,
        "--- whisparr container log (tail) ---",
        tailOf(logChunks),
      ].join("\n"),
      { cause },
    );
  }
}

function tailOf(logChunks) {
  const text = logChunks.join("");
  return text
    ? text.split("\n").slice(-STARTUP_LOG_LINES).join("\n")
    : "<container produced no log output>";
}

// The two generations take the key by different mechanisms. v3 reads it from configuration and
// writes no key element to its config file at all; v2 ignores every environment spelling and has to
// be handed a config file before it starts. They stay separate on purpose — the environment route
// needs no copy, no file mode and no ordering, so unifying on the file route would buy symmetry and
// pay for it in failure surface.
function withApiKeySeed(builder, generation, apiKey) {
  if (generation === "v3") {
    return builder.withEnvironment({ WHISPARR__AUTH__APIKEY: apiKey });
  }
  if (generation === "v2") {
    return builder.withCopyContentToContainer([
      {
        content: buildConfigXml({ apiKey, port: WHISPARR_PORT }),
        target: "/config/config.xml",
        // The mode is the load-bearing field. Without it the file arrives root-owned, the image's
        // init does not chown a file it did not create, and the app exits on its first config write
        // — while the supervisor keeps the container up and the API answers nothing at all.
        mode: 0o666,
      },
    ]);
  }
  throw new Error(`startWhisparr: no API-key seed is wired for generation "${generation}".`);
}
