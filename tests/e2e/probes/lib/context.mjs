// The probe runner's bring-up: whatever the selected rows between them need, started once and
// shared, and torn down in the order the compose network requires.
//
// Everything started here is Testcontainers-managed so Ryuk reaps it when the run is killed rather
// than exited. A raw `docker run` would escape that, and a probe is exactly the kind of thing a
// person interrupts.
import { resolveCoveImage, startHarness } from "../../lib/harness.mjs";
import { startWhisparr } from "../../lib/whisparr-fixture.mjs";
import { whisparrImage } from "../../lib/whisparr-images.mjs";
import {
  describeServers,
  liftMetadataServers,
  placeholderProviderEnv,
  providerEnv,
  PLACEHOLDER_SERVERS,
} from "../../lib/cove-providers.mjs";

const STATUS_PATH = "/api/v3/system/status";

/**
 * The union of what every row in `rows` declares it needs.
 *
 * Pure, so a selection's cost is knowable without starting anything.
 */
export function aggregateRequirements(rows) {
  const whisparr = new Set();
  const support = new Set();
  let cove = false;
  let seedHistory = false;
  let network = false;
  for (const row of rows) {
    const requires = row.requires ?? {};
    cove ||= requires.cove === true;
    seedHistory ||= requires.seedHistory === true;
    network ||= requires.network === true;
    for (const generation of requires.whisparr ?? []) whisparr.add(generation);
    for (const name of requires.support ?? []) support.add(name);
  }
  return {
    cove,
    seedHistory,
    network,
    whisparr: [...whisparr],
    support: [...support],
  };
}

/**
 * The provider entries a fixture Cove is configured with, and where they came from.
 *
 * A machine with no install of its own gets the placeholder set, so the configured SHAPE is present
 * either way and a provider read fails rather than finding nothing configured. `skip` carries the
 * reason the lift found nothing, for the record to name.
 */
function resolveProviders() {
  const lifted = liftMetadataServers();
  if (lifted.servers.length > 0) {
    return {
      source: "install",
      skip: lifted.skip,
      servers: describeServers(lifted.servers),
      env: providerEnv(lifted.servers),
    };
  }
  return {
    source: "placeholder",
    skip: lifted.skip,
    servers: describeServers(PLACEHOLDER_SERVERS),
    env: placeholderProviderEnv(),
  };
}

/**
 * Starts what `requirements` asks for and returns the handle rows are run against.
 *
 * `stop()` tears down in the one order that works: a Whisparr container holds an endpoint on the
 * compose network, and the daemon refuses to remove a network that still has one.
 *
 * @param {ReturnType<typeof aggregateRequirements>} requirements
 */
export async function startProbeContext(requirements) {
  if (requirements.support.length > 0) {
    throw new Error(
      `startProbeContext: a selected row asks for the support container(s) ${requirements.support.join(", ")}, and this context has no starter for one. Add it here, beside the Whisparr start.`,
    );
  }
  if (requirements.whisparr.length > 0 && !requirements.cove) {
    throw new Error(
      "startProbeContext: a row asking for Whisparr must also ask for cove, because the containers join the Cove instance's own network.",
    );
  }

  const providers = resolveProviders();
  let harness = null;
  let whisparr = null;
  try {
    if (requirements.cove) {
      harness = await startHarness({ env: providers.env });
      await harness.bootstrapOwner();
    }
    if (requirements.whisparr.length > 0) {
      whisparr = await startWhisparr({
        network: harness.container.getNetworkNames()[0],
        generations: requirements.whisparr,
        seedHistory: requirements.seedHistory,
      });
    }
  } catch (cause) {
    await whisparr?.stop().catch(() => {});
    await harness?.stop().catch(() => {});
    throw cause;
  }

  return {
    harness,
    whisparr,
    providers,
    builds: await readBuilds({ whisparr }),
    async stop() {
      await whisparr?.stop();
      await harness?.stop();
    },
  };
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
