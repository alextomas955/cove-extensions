// The one network every harness stack joins, and the one operation that brings it into being.
//
// Why it exists at all is written in docker/docker-compose.yml, beside the `external` declaration
// that consumes it: a project-owned network per stack means a bridge per stack, and each new bridge
// takes a host IPv4 address, which is what makes Chromium flush every in-flight request with
// ERR_NETWORK_CHANGED.
import { execFile } from "node:child_process";
import { promisify } from "node:util";

const run = promisify(execFile);

// A FIXED name, not a generated one. Playwright runs globalSetup in its own process and hands
// workers nothing, so a generated name could not reach the compose invocation without a file or an
// environment variable that outlives the process that wrote it. A fixed name needs neither, and the
// create below is idempotent, so a leftover from a killed run is joined rather than fought over.
export const SHARED_NETWORK_NAME = process.env.COVE_E2E_NETWORK || "cove-e2e-shared";

// Marks the network as this suite's, so a human clearing up after a run can tell it from a network
// some other project left behind. Nothing here removes the network, so the label is for the person
// reading `docker network ls`.
const LABEL = "com.cove-extensions.e2e=shared-harness-network";

/**
 * Creates the shared network, treating "it already exists" as success.
 *
 * Idempotent on purpose, and nothing deletes it afterwards. Three reasons it OUTLIVES the run rather
 * than being torn down:
 *
 * A name that already exists is not necessarily ours. `COVE_E2E_NETWORK` can name a network the
 * developer keeps for something else, and an unrelated `cove-e2e-shared` can exist for reasons this
 * suite knows nothing about. Removing what we merely joined would destroy someone else's resource.
 *
 * Two suites can share one Docker daemon. Whichever finished first would take the network away from
 * the other, and the failure would land on compose startup in the run that did nothing wrong.
 *
 * What is left behind is ONE network, which the next run joins, and it is labelled so a person can
 * see whose it is. On CI the runner is discarded anyway. Both costs are smaller than either failure
 * above, and smaller than the 21 networks a run used to create and destroy.
 */
export async function ensureSharedNetwork() {
  try {
    await run("docker", ["network", "create", "--label", LABEL, SHARED_NETWORK_NAME]);
    return "created";
  } catch (error) {
    // Docker's own words for the benign case. Matched on the message because the CLI exits 1 for
    // every failure alike, so the status cannot tell this apart from a daemon that is not running -
    // and reporting the latter as success would hand every stack a network that does not exist.
    if (/already exists/i.test(String(error.stderr ?? error.message))) return "existed";
    throw new Error(
      `Could not create the shared e2e network '${SHARED_NETWORK_NAME}': ${String(error.stderr ?? error.message).trim()}`,
      { cause: error },
    );
  }
}
