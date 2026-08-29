// A receiver an application under test can call, on the harness network, with nothing mounted and
// nothing published.
//
// Reachable only by its network alias, because the only callers are containers on that network and a
// published port would put a receiver that answers every unauthenticated POST on the machine running
// the probe. What arrived comes back out through the container's own log stream, which is what the
// sentinel prefix on each capture line is for.
//
// Testcontainers-managed like everything else the probes start, so Ryuk reaps it when a run is
// killed rather than exited.
import { join } from "node:path";

import { GenericContainer, Wait } from "testcontainers";

import { tailContainerLog } from "../../lib/harness.mjs";
import { attemptUntil } from "../../lib/poll.mjs";
import { whisparrImage } from "../../lib/whisparr-images.mjs";

// A committed file copied in, never a heredoc and never a string assembled in shell: a
// heredoc-written script carries CRLF into every path it handles, and the failure then blames the
// path.
const SCRIPT_SOURCE = join(import.meta.dirname, "webhook-listener.py");
const SCRIPT_TARGET = "/tmp/webhook-listener.py";

const SENTINEL = "@@WEBHOOK@@";
const READY_LINE = "@@LISTENER-READY@@";

const DEFAULT_STARTUP_TIMEOUT_MS = process.env.CI ? 240_000 : 120_000;

// Wide enough that a run's whole capture history is still in reach, since a row reads the captures
// of every row that ran before it and takes only its own.
const LOG_LINES = 2_000;

// The log stream follows a running container, so it never ends on its own and this bound is what
// returns it. Short, because a capture poll pays it on every attempt.
const LOG_READ_TIMEOUT_MS = 2_000;

const DEFAULT_CAPTURE_TIMEOUT_MS = 60_000;
const CAPTURE_POLL_INTERVAL_MS = 500;

/**
 * Every complete capture line in `log`, in the order the listener printed them.
 *
 * The sentinel is matched at line start only, so a delivery whose own body carries the sentinel
 * cannot forge a second capture.
 *
 * A line the log read cut in half is skipped rather than reported: the stream is re-read on the next
 * poll, where the same line arrives whole.
 */
function parseCaptureLines(log) {
  const captures = [];
  for (const line of log.split("\n")) {
    if (!line.startsWith(`${SENTINEL} `)) continue;
    try {
      captures.push(JSON.parse(line.slice(SENTINEL.length + 1)));
    } catch {
      continue;
    }
  }
  return captures;
}

/**
 * Starts the listener on `network` under `alias`, and returns a handle that reads back what arrived.
 *
 * `network` is one of the started Cove container's own networks, read off it rather than named, so
 * this stays independent of which Cove image the run booted.
 *
 * CALLER CONTRACT: this handle's `stop()` must run BEFORE the harness's own. A compose down removes
 * the project network and the daemon refuses to remove one that still has an attached endpoint.
 *
 * @param {{network: string, alias?: string, port?: number, startupTimeoutMs?: number}} options
 */
export async function startWebhookListener({
  network,
  alias = "listener",
  port = 8099,
  startupTimeoutMs = DEFAULT_STARTUP_TIMEOUT_MS,
} = {}) {
  if (!network) {
    throw new Error(
      "startWebhookListener: no network given; pass one of the started Cove container's own networks, e.g. harness.container.getNetworkNames()[0].",
    );
  }
  if (!Number.isInteger(port) || port < 1 || port > 65_535) {
    throw new Error(
      `startWebhookListener: port must be an integer TCP port, got ${JSON.stringify(port)}; it is passed to a process inside the container.`,
    );
  }

  const container = await new GenericContainer(whisparrImage("v3"))
    .withNetworkMode(network)
    // An alias is scoped to the network; a container NAME is daemon-global and would collide the
    // moment two probe runs overlap.
    .withNetworkAliases(alias)
    .withCopyFilesToContainer([{ source: SCRIPT_SOURCE, target: SCRIPT_TARGET }])
    // The image's own entrypoint is a supervisor that brings the application up. Replacing it is
    // what leaves a container running nothing but the listener.
    .withEntrypoint(["python3", SCRIPT_TARGET, "--port", String(port)])
    // A line the listener prints once its socket is bound, rather than an elapsed time: a sleep is
    // either short enough to race the bind or long enough to be paid on every run.
    .withWaitStrategy(Wait.forLogMessage(READY_LINE))
    .withStartupTimeout(startupTimeoutMs)
    .start();

  const handle = {
    alias,
    port,

    /** The URL a container on this network registers to reach the listener. */
    url(path = "/") {
      return `http://${alias}:${port}${path}`;
    },

    /** Every delivery the listener has printed so far, parsed, in arrival order. */
    async captures() {
      return parseCaptureLines(
        await tailContainerLog(container, { lines: LOG_LINES, timeoutMs: LOG_READ_TIMEOUT_MS }),
      );
    },

    /**
     * The captures matching `match` once there are at least `count` of them, or an error naming what
     * was expected and what was seen.
     *
     * A delivery is asynchronous, so the count is polled. `match` exists because every row in a run
     * shares one listener and takes only the deliveries it caused.
     *
     * @param {number} count
     * @param {{timeoutMs?: number, match?: (capture: object) => boolean}} [options]
     */
    async waitForCaptures(count, { timeoutMs = DEFAULT_CAPTURE_TIMEOUT_MS, match } = {}) {
      const { settled, value, note } = await attemptUntil(
        async (_signal, record) => {
          const all = await handle.captures();
          const matched = match === undefined ? all : all.filter((capture) => match(capture));
          record(`${matched.length} matching (${all.length} on the listener in total)`);
          return matched.length >= count ? { value: matched } : null;
        },
        {
          timeoutMs,
          intervalMs: CAPTURE_POLL_INTERVAL_MS,
          label: "webhook listener waitForCaptures",
        },
      );
      if (!settled) {
        throw new Error(
          `startWebhookListener: waitForCaptures expected ${count} capture(s) at ${alias}:${port} and saw ${note} within ${timeoutMs}ms.`,
        );
      }
      return value;
    },

    async stop() {
      await container.stop();
    },
  };

  return handle;
}
