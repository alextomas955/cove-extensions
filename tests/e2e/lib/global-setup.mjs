// Runs once before any worker.
//
// This is the only safe moment to create the shared network: creating one raises a host address
// event, and Chromium drops in-flight requests when it sees one. Here no browser exists yet.
//
// Returns nothing, so Playwright runs no teardown. lib/shared-network.mjs says why the network is
// left in place rather than removed.
import { ensureSharedNetwork, SHARED_NETWORK_NAME } from "./shared-network.mjs";

export default async function globalSetup() {
  const outcome = await ensureSharedNetwork();
  console.log(`[e2e] shared docker network '${SHARED_NETWORK_NAME}' ${outcome}`);
}
