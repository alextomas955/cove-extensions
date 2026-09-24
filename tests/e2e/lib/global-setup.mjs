// Creates the networks the harness stacks join (see docker/docker-compose.yml). It runs before any
// worker because creating a network raises a host address event, and no browser exists yet.
//
// ONE NETWORK PER PARALLEL SLOT, not one for the whole run. A network is a DNS namespace, and the
// fixtures reach each other by name: `stashdb.org` for the metadata stub, `metadata-stub`, the
// indexer and the download client. Those names are fixed on purpose - the stub answering to
// `stashdb.org` is how the product's own configured endpoint resolves to it - so on one shared
// namespace every worker's stub answered to the same name and the daemon handed out whichever it
// liked. A worker then reached another worker's stub, instance or Cove, and the spec waited out its
// budget against something that was never going to answer. Per slot, each name is unique again and
// the fixtures need no renaming.
//
// The networks are left in place afterwards. A name may belong to a network someone else keeps, and
// a second suite on the same daemon may still be using it; the next run joins it.
import { execFile } from "node:child_process";
import { promisify } from "node:util";

const run = promisify(execFile);

/** The base name; each slot appends its own index. */
export const NETWORK_BASE = process.env.COVE_E2E_NETWORK || "cove-e2e-shared";

/**
 * The network for one parallel slot.
 *
 * Keyed on Playwright's `TEST_PARALLEL_INDEX`, which is bounded by the worker count and reused when
 * a worker restarts - unlike `TEST_WORKER_INDEX`, which grows and would name a network that setup
 * never created.
 */
export function networkForSlot(slot = process.env.TEST_PARALLEL_INDEX ?? "0") {
  return `${NETWORK_BASE}-${slot}`;
}

export default async function globalSetup(config) {
  // One more than the workers actually used costs an unused bridge; one fewer leaves a worker naming
  // a network that does not exist, which fails every stack it starts.
  const slots = Math.max(1, config?.workers ?? 1);

  for (let slot = 0; slot < slots; slot++) {
    const network = networkForSlot(String(slot));
    try {
      await run("docker", [
        "network",
        "create",
        "--label",
        "com.cove-extensions.e2e=shared-harness-network",
        network,
      ]);
      console.log(`[e2e] created docker network '${network}'`);
    } catch (error) {
      // The CLI exits 1 for every failure, so only its message separates an existing network from a
      // daemon that is not running.
      const detail = String(error.stderr ?? error.message).trim();
      if (!/already exists/i.test(detail)) {
        throw new Error(`Could not create the shared e2e network '${network}': ${detail}`, {
          cause: error,
        });
      }
    }
  }
}
