// The one network every harness stack joins, and the two operations that manage it.
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

// Marks the network as this suite's, so a human clearing up after a killed run can tell it from a
// network some other project left behind.
const LABEL = "com.cove-extensions.e2e=shared-harness-network";

/**
 * Creates the shared network, treating "it already exists" as success.
 *
 * Idempotent on purpose: a run killed before its teardown leaves the network behind, and the next
 * run must join that one rather than fail. `docker network create` is also the whole of the
 * mitigation's cost - one address event, paid before any browser exists.
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

/**
 * Removes the shared network. Best-effort: a stack that outlived its test still holds an endpoint on
 * it, and Docker refuses to remove a network in use. Leaving it costs one unused network and the
 * next run joins it, so a failure here is not worth failing a green suite over.
 */
export async function removeSharedNetwork() {
  try {
    await run("docker", ["network", "rm", SHARED_NETWORK_NAME]);
  } catch {
    // Intentionally silent; see above.
  }
}
