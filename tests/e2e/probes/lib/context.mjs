// The probe runner's bring-up: whatever the selected rows between them need, started once and
// shared, and torn down in the order the compose network requires.
//
// Everything started here is Testcontainers-managed so Ryuk reaps it when the run is killed rather
// than exited. A raw `docker run` would escape that, and a probe is exactly the kind of thing a
// person interrupts.
//
// Provider configuration is delivered here, and by two routes rather than one: the container
// environment, and failing that Cove's own configuration API. Which route landed it is recorded on
// the handle, because a row asserting anything about providers has to know.
import { createApiClient } from "../../lib/apiClient.mjs";
import { resolveCoveImage, startHarness } from "../../lib/harness.mjs";
import { attemptUntil } from "../../lib/poll.mjs";
import { startWhisparr } from "../../lib/whisparr-fixture.mjs";
import { whisparrImage } from "../../lib/whisparr-images.mjs";
import { startWebhookListener } from "../support/webhook-listener.mjs";
import {
  describeServers,
  liftMetadataServers,
  placeholderProviderEnv,
  providerEnv,
  PLACEHOLDER_SERVERS,
} from "../../lib/cove-providers.mjs";

const STATUS_PATH = "/api/v3/system/status";
const CONFIG_PATH = "/api/system/config";
const PROVIDER_ENV_PREFIX = "COVE__Scraping__MetadataServers__";

// The support containers a row may ask for by name. Each takes the network off the started Cove
// container, so a row asking for one asks for cove too.
const SUPPORT_STARTERS = { "webhook-listener": startWebhookListener };

// The library root registered on every instance a row asks `rootFolder` for. Registration is what
// makes an entity add possible at all: both generations refuse one whose destination is not a
// registered root, and the refusal names a validator rather than the missing registration.
const PROBE_ROOT_FOLDER = "/probe-root";

const READ_BACK_TIMEOUT_MS = 30_000;
const READ_BACK_INTERVAL_MS = 1_000;

// ---- Pure helpers: no container, so a selection and a verdict are both decidable without one. ----

/**
 * The union of what every row in `rows` declares it needs.
 *
 * Pure, so a selection's cost is knowable before anything starts.
 */
export function aggregateRequirements(rows) {
  const whisparr = new Set();
  const support = new Set();
  let cove = false;
  let seedHistory = false;
  let network = false;
  let rootFolder = false;
  for (const row of rows) {
    const requires = row.requires ?? {};
    cove ||= requires.cove === true;
    seedHistory ||= requires.seedHistory === true;
    network ||= requires.network === true;
    rootFolder ||= requires.rootFolder === true;
    for (const generation of requires.whisparr ?? []) whisparr.add(generation);
    for (const name of requires.support ?? []) support.add(name);
  }
  return {
    cove,
    seedHistory,
    network,
    rootFolder,
    whisparr: [...whisparr],
    support: [...support],
  };
}

/**
 * Rebuilds provider entries out of the environment built for them.
 *
 * The configuration API is offered exactly the entries the environment route was given, rather than
 * a second reading of the install, so the two routes cannot be handed different inputs.
 */
function serversFromProviderEnv(env) {
  const byIndex = new Map();
  for (const [key, value] of Object.entries(env)) {
    if (!key.startsWith(PROVIDER_ENV_PREFIX)) continue;
    const [index, field] = key.slice(PROVIDER_ENV_PREFIX.length).split("__");
    const server = byIndex.get(index) ?? {};
    server[field.charAt(0).toLowerCase() + field.slice(1)] = value;
    byIndex.set(index, server);
  }
  return [...byIndex.entries()]
    .sort(([a], [b]) => Number(a) - Number(b))
    .map(([, server]) => ({
      ...server,
      maxRequestsPerMinute: Number(server.maxRequestsPerMinute),
    }));
}

/**
 * How an instance's reported provider entries compare with the ones it was configured with.
 *
 * Three states, kept distinct: the interesting failure is a section that arrives carrying some of
 * its fields and not others, and collapsing that into either neighbour loses the fact worth having.
 *
 * @param {object[]} configured - described entries, as configured
 * @param {object[]|undefined} observed - described entries, as the instance reports them
 */
export function judgeBinding(configured, observed) {
  if (!Array.isArray(observed) || observed.length === 0) {
    return { verdict: "not-bound", mismatches: [] };
  }
  const mismatches = [];
  if (observed.length !== configured.length) {
    mismatches.push({
      index: null,
      field: "count",
      configured: configured.length,
      observed: observed.length,
    });
  }
  configured.forEach((want, index) => {
    const got = observed[index];
    if (got === undefined) {
      mismatches.push({ index, field: "entry", configured: want.name, observed: null });
      return;
    }
    for (const field of ["endpoint", "name", "maxRequestsPerMinute"]) {
      if (got[field] !== want[field]) {
        mismatches.push({ index, field, configured: want[field], observed: got[field] });
      }
    }
    if (got.apiKey.chars !== want.apiKey.chars) {
      mismatches.push({
        index,
        field: "apiKey.chars",
        configured: want.apiKey.chars,
        observed: got.apiKey.chars,
      });
    }
  });
  return { verdict: mismatches.length === 0 ? "bound" : "partially-bound", mismatches };
}

/**
 * The provider entries a fixture Cove is configured with, and where they came from.
 *
 * A machine with no install of its own gets the placeholder set, so the configured SHAPE is present
 * either way and a provider read fails rather than finding nothing configured. `skip` carries the
 * reason the lift found nothing, for a record to name.
 */
function resolveProviders() {
  const lifted = liftMetadataServers();
  const servers = lifted.servers.length > 0 ? lifted.servers : PLACEHOLDER_SERVERS;
  return {
    source: lifted.servers.length > 0 ? "install" : "placeholder",
    skip: lifted.skip,
    servers: describeServers(servers),
    env: lifted.servers.length > 0 ? providerEnv(lifted.servers) : placeholderProviderEnv(),
  };
}

// ---- Everything below starts containers or talks to one. ----

async function readMetadataServers(api) {
  const response = await api.get(CONFIG_PATH);
  if (!response.ok) {
    throw new Error(
      `probeContext: GET ${CONFIG_PATH} answered ${response.status}: ${response.text || "<empty body>"}`,
    );
  }
  return response.json?.scraping?.metadataServers;
}

/** How many of the provider variables reached the container, which is what tells a dead binder from an undelivered one. */
async function countProviderEnvInContainer(harness) {
  const { output } = await harness.exec([
    "sh",
    "-c",
    `env | grep -c '^${PROVIDER_ENV_PREFIX}' || true`,
  ]);
  return Number(output.trim()) || 0;
}

/**
 * Puts the providers where a row can read them, and reports which route did it.
 *
 * `observedFromEnv` is what the instance reported before anything was saved to it, so it stays a
 * clean observation of the environment route however the entries eventually arrive.
 *
 * A save writes the whole configuration out and Cove re-applies its saved file to live options, so
 * what the providers ARE after a save is only knowable by asking again. That re-read polls: a
 * one-shot read cannot tell a save that has not landed yet from one that never will.
 */
async function deliverProviders(harness, providers) {
  const api = createApiClient(
    () => harness.baseUrl,
    () => harness.token,
  );
  const envVarsInContainer = await countProviderEnvInContainer(harness);
  const observedFromEnv = describeServers((await readMetadataServers(api)) ?? []);

  if (judgeBinding(providers.servers, observedFromEnv).verdict === "bound") {
    return {
      envVarsInContainer,
      observedFromEnv,
      delivery: { by: "environment", readBack: observedFromEnv },
    };
  }

  const current = await api.get(CONFIG_PATH);
  const saved = await api.put(CONFIG_PATH, {
    ...current.json,
    scraping: {
      ...current.json.scraping,
      metadataServers: serversFromProviderEnv(providers.env),
    },
  });
  const { settled, value, note } = await attemptUntil(
    async (_signal, note) => {
      const observed = await readMetadataServers(api).catch((cause) => {
        note(cause.message);
        return undefined;
      });
      note(`${Array.isArray(observed) ? observed.length : 0} entries`);
      return Array.isArray(observed) && observed.length > 0 ? { value: observed } : null;
    },
    {
      timeoutMs: READ_BACK_TIMEOUT_MS,
      intervalMs: READ_BACK_INTERVAL_MS,
      label: "deliverProviders read-back",
    },
  );

  return {
    envVarsInContainer,
    observedFromEnv,
    delivery: {
      by: settled ? "configuration-api" : "none",
      saveStatus: saved.status,
      readBack: settled ? describeServers(value) : [],
      lastPoll: note,
    },
  };
}

/**
 * Starts what `requirements` asks for and returns the handle rows are run against.
 *
 * `stop()` tears down in the one order that works: a Whisparr container holds an endpoint on the
 * compose network, and the daemon refuses to remove a network that still has one.
 *
 * `outDir` is where the caller's records are going, so a row whose evidence is too large for one can
 * put it beside them. A row that writes nothing never reads it.
 *
 * @param {ReturnType<typeof aggregateRequirements>} requirements
 * @param {{outDir?: string}} [destination]
 */
export async function startProbeContext(requirements, { outDir } = {}) {
  const unknownSupport = requirements.support.filter(
    (name) => SUPPORT_STARTERS[name] === undefined,
  );
  if (unknownSupport.length > 0) {
    throw new Error(
      `startProbeContext: a selected row asks for the support container(s) ${unknownSupport.join(", ")}, and this context has no starter for them. Declared support containers are ${Object.keys(SUPPORT_STARTERS).join(", ")}; add one here, beside the Whisparr start.`,
    );
  }
  if ((requirements.whisparr.length > 0 || requirements.support.length > 0) && !requirements.cove) {
    throw new Error(
      "startProbeContext: a row asking for Whisparr or a support container must also ask for cove, because those containers join the Cove instance's own network.",
    );
  }

  const providers = resolveProviders();
  const support = {};
  let harness = null;
  let whisparr = null;
  try {
    if (requirements.cove) {
      harness = await startHarness({ env: providers.env });
      await harness.bootstrapOwner();
      Object.assign(providers, await deliverProviders(harness, providers));
    }
    const network = harness?.container.getNetworkNames()[0];
    if (requirements.whisparr.length > 0) {
      whisparr = await startWhisparr({
        network,
        generations: requirements.whisparr,
        seedHistory: requirements.seedHistory,
        rootFolder: requirements.rootFolder ? PROBE_ROOT_FOLDER : undefined,
      });
    }
    for (const name of requirements.support) {
      support[name] = await SUPPORT_STARTERS[name]({ network });
    }
  } catch (cause) {
    await stopAll({ support, whisparr, harness }, { swallow: true });
    throw cause;
  }

  return {
    harness,
    whisparr,
    support,
    providers,
    outDir,
    builds: await readBuilds({ whisparr }),
    async stop() {
      await stopAll({ support, whisparr, harness });
    },
  };
}

/**
 * Runs every one of `stops`, in order, and reports the ones that threw.
 *
 * A stop that throws does not cancel the ones after it. The order is the whole point — the harness
 * goes last because the daemon refuses to remove a network an earlier container still holds an
 * endpoint on — so short-circuiting on the first failure is what strands both.
 *
 * @param {Array<() => Promise<unknown>>} stops
 * @returns {Promise<unknown[]>} what each failing stop threw, in the order they were run
 */
export async function runEveryStop(stops) {
  const failures = [];
  for (const stop of stops) {
    try {
      await stop();
    } catch (cause) {
      failures.push(cause);
    }
  }
  return failures;
}

/**
 * Stops everything the bring-up started, in the one order that works: a Whisparr or a support
 * container holds an endpoint on the compose network, and the daemon refuses to remove a network
 * that still has one, so the harness goes last.
 *
 * `swallow` is for the failed bring-up, where the start failure is the error worth raising and a
 * failure to clean up behind it must not displace one.
 */
async function stopAll({ support, whisparr, harness }, { swallow = false } = {}) {
  const failures = await runEveryStop([
    ...Object.values(support).map((container) => () => container.stop()),
    ...(whisparr ? [() => whisparr.stop()] : []),
    ...(harness ? [() => harness.stop()] : []),
  ]);
  if (!swallow && failures.length > 0) {
    throw new AggregateError(failures, "startProbeContext: not everything the run started stopped");
  }
}

/**
 * What the run is actually made of, read back off the running instances rather than off what was
 * configured. A Whisparr generation that answers nothing is recorded as such instead of being
 * omitted.
 */
async function readBuilds({ whisparr }) {
  const builds = { cove: resolveCoveImage(), whisparr: {} };
  for (const generation of whisparr?.generations ?? []) {
    const status = await whisparr.apiFor(generation).get(STATUS_PATH);
    builds.whisparr[generation] = {
      image: whisparrImage(generation),
      version: status.json?.version ?? `<no version in HTTP ${status.status}>`,
    };
  }
  return builds;
}
