// Creates the one network every harness stack joins (see docker/docker-compose.yml). It runs before
// any worker because creating a network raises a host address event, and no browser exists yet.
//
// The network is left in place afterwards. The name may belong to a network someone else keeps, and a
// second suite on the same daemon may still be using it; the next run joins it.
import { execFile } from "node:child_process";
import { promisify } from "node:util";

const run = promisify(execFile);

const NETWORK = process.env.COVE_E2E_NETWORK || "cove-e2e-shared";

export default async function globalSetup() {
  try {
    await run("docker", [
      "network",
      "create",
      "--label",
      "com.cove-extensions.e2e=shared-harness-network",
      NETWORK,
    ]);
    console.log(`[e2e] created docker network '${NETWORK}'`);
  } catch (error) {
    // The CLI exits 1 for every failure, so only its message separates an existing network from a
    // daemon that is not running.
    const detail = String(error.stderr ?? error.message).trim();
    if (!/already exists/i.test(detail)) {
      throw new Error(`Could not create the shared e2e network '${NETWORK}': ${detail}`, {
        cause: error,
      });
    }
  }
}
