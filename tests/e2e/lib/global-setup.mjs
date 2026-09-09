// Runs once before any worker, and its returned function once after the last one.
//
// This is the only safe moment to create the shared network: creating one raises a host address
// event, and Chromium drops in-flight requests when it sees one. Here no browser exists yet.
import { ensureSharedNetwork, removeSharedNetwork, SHARED_NETWORK_NAME } from "./shared-network.mjs";

export default async function globalSetup() {
  const outcome = await ensureSharedNetwork();
  console.log(`[e2e] shared docker network '${SHARED_NETWORK_NAME}' ${outcome}`);
  return removeSharedNetwork;
}
