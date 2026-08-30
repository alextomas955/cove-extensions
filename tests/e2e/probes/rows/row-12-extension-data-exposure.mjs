// What Cove's bulk extension-data route returns for an installed extension, and to whom.
//
// The route is permission-gated in source, so the question is not what the attribute says but what
// an instance answers. Two callers are taken against the same store: the bootstrapped owner, and an
// unauthenticated caller on Cove's own container network — the second one because the permission
// filter returns early when a deployment has authentication off, which is the fixture's setting and
// a common self-hosted one.
//
// The whole run is taken twice, once against an instance with authentication off and once against
// one with it on, because "the route discloses" and "this deployment discloses" are different
// findings and only the pair tells them apart.
//
// A control read for an extension id nothing installed is taken as well: without it a 200 for the
// installed id would be as consistent with a catch-all as with the route.
//
// The value planted in the store is synthetic and authorises nothing, and the response body is
// never carried back through an exec's stdout — only its length, its header names and whether the
// marker appears in it. A record outlives the run that produced it.
import { Buffer } from "node:buffer";

import { GenericContainer, Wait } from "testcontainers";

import { createApiClient } from "../../lib/apiClient.mjs";
import { resolveCoveImage, startHarness } from "../../lib/harness.mjs";
import { resolveExtensionPaths } from "../../lib/resolve-extension.mjs";
import { whisparrImage } from "../../lib/whisparr-images.mjs";

// The compose service name, which is also the hostname a container on that network addresses, and
// the port the application listens on inside its own container rather than the published one.
const COVE_HOST = "cove";
const COVE_PORT = 5073;

const EXTENSION_ID = "com.alextomas955.whisparrsync";

// Installed by nothing, here or anywhere, so a 404 for it is the route saying it has no such
// extension rather than the row asserting that it would.
const ABSENT_EXTENSION_ID = "com.alextomas955.no-such-extension";

const DATA_PATH = (id) => `/api/extensions/${id}/data`;
const MARKER_KEY = "probe-marker";

// Obviously synthetic, and it authorises nothing anywhere. It is what the reads look for, so it is
// also chosen to be recognisable in a byte stream and to be unmistakable for a lifted credential.
const MARKER_VALUE = "row12-probe-marker-not-a-secret";

// The compose file's own switch for the host's authentication, rather than the setting it sets: the
// setting is written once, there, and naming it here would be a second declaration free to drift.
const AUTH_SWITCH = "COVE_E2E_AUTH_ENABLED";

// Files inside the caller container, so a store response is never carried back through the exec's
// stdout.
const BODY_FILE = "/tmp/r12-body";
const HEADER_FILE = "/tmp/r12-headers";
const META_FILE = "/tmp/r12-meta";
const ERROR_FILE = "/tmp/r12-error";

const HEADER_MARKER = "HEADERS";

const CALLER_READY_LINE = "@@ROW12-CALLER-READY@@";
const CALLER_STARTUP_TIMEOUT_MS = process.env.CI ? 240_000 : 120_000;

// The closed set a reader may find in `verdict`. The first three are the outcomes D-06 distinguishes
// between; the fourth is what an observation that establishes none of them is called, so a run that
// measured nothing cannot be read as one that measured an absence.
const VERDICTS = [
  "marker-returned-to-anonymous-in-network-caller",
  "marker-returned-to-owner-only",
  "route-returned-nothing-for-this-extension",
  "inconclusive",
];

// resolveExtensionPaths is the one place allowed to encode the extensions/<Ext>/e2e/lib/… layout.
// This row is not in that layout, so it hands the function the module URL a fixture module there
// would have rather than restating the hops to the repo root for itself.
const WHISPARR_SYNC = resolveExtensionPaths(
  new URL("../../../../extensions/WhisparrSync/e2e/lib/probe.mjs", import.meta.url).href,
  { srcProject: "WhisparrSync" },
);

const shellQuote = (value) => `'${String(value).replaceAll("'", `'\\''`)}'`;

/**
 * The command a container runs to read one route and report everything except the body.
 *
 * Whether the marker is in the response is decided by `grep` inside the container, so the answer
 * comes back as a count and the bytes it was computed over stay where they were fetched.
 */
function curlCommand(path) {
  const url = shellQuote(`http://${COVE_HOST}:${COVE_PORT}${path}`);
  return [
    `curl -sS -X GET -D ${HEADER_FILE} -o ${BODY_FILE}`,
    `-w '%{http_code} %{content_type}' ${url} > ${META_FILE} 2> ${ERROR_FILE};`,
    `printf 'EXIT=%s\\n' "$?";`,
    `printf 'META=%s\\n' "$(cat ${META_FILE})";`,
    `printf 'BYTES=%s\\n' "$(wc -c < ${BODY_FILE})";`,
    `printf 'MARKER=%s\\n' "$(grep -c -F ${shellQuote(MARKER_VALUE)} ${BODY_FILE} || true)";`,
    `printf 'ERR=%s\\n' "$(head -c 200 ${ERROR_FILE})";`,
    `printf '${HEADER_MARKER}\\n';`,
    `cat ${HEADER_FILE} 2>/dev/null || true`,
  ].join(" ");
}

/**
 * One in-network read, as the caller container reports it.
 *
 * Header NAMES rather than header pairs: a value here could carry a session the record has no
 * business keeping, and what is being established is which headers the answer came with.
 */
function parseCallerOutput(output) {
  const lines = output.split("\n").map((line) => line.replace(/\r$/, ""));
  const marker = lines.indexOf(HEADER_MARKER);
  const field = (name) =>
    lines.find((line) => line.startsWith(`${name}=`))?.slice(name.length + 1) ?? "";
  const [status, ...contentType] = field("META").split(" ");
  const headerNames = (marker === -1 ? [] : lines.slice(marker + 1))
    .map((line) => /^([A-Za-z0-9-]+):/.exec(line)?.[1]?.toLowerCase())
    .filter((name) => name !== undefined);
  const exit = Number(field("EXIT"));
  return {
    status: Number(status) || 0,
    contentType: contentType.join(" "),
    byteLength: Number(field("BYTES")) || 0,
    markerPresent: Number(field("MARKER")) > 0,
    headerNames: [...new Set(headerNames)].sort().join(" "),
    transportError: exit === 0 ? "" : field("ERR") || `curl exited ${exit}`,
  };
}

/**
 * A container on `network` that does nothing but wait to be exec'd into.
 *
 * The source address is the whole subject of the anonymous read, so it has to originate in a
 * container other than the one being read. The image is the one this suite already pulls, chosen so
 * a probe run costs no additional pull; nothing of the application inside it runs, because its
 * entrypoint is replaced.
 *
 * CALLER CONTRACT: stop this before the harness whose network it joined. The daemon refuses to
 * remove a network that still has an attached endpoint.
 */
async function startCaller(network) {
  return (
    new GenericContainer(whisparrImage("v3"))
      .withNetworkMode(network)
      .withEntrypoint(["sh", "-c", `echo ${CALLER_READY_LINE}; tail -f /dev/null`])
      // A line the container prints, rather than an elapsed time: a sleep is either short enough to
      // race the start or long enough to be paid on every run.
      .withWaitStrategy(Wait.forLogMessage(CALLER_READY_LINE))
      .withStartupTimeout(CALLER_STARTUP_TIMEOUT_MS)
      .start()
  );
}

/** Where the anonymous read came from, as the caller container itself reports it. */
async function sourceAddresses(container) {
  const { output } = await container.exec([
    "sh",
    "-c",
    `hostname -i; printf '|'; getent hosts ${COVE_HOST} | head -1`,
  ]);
  const [own, target] = output.split("|");
  return {
    ownAddresses: own.trim().split(/\s+/).filter(Boolean).join(" "),
    resolvedTarget: target?.trim().split(/\s+/)[0] ?? null,
  };
}

/** The owner's own read of a store, summarised the same way the anonymous one is. */
function summariseOwnerRead(response) {
  return {
    status: response.status,
    contentType: response.contentType,
    byteLength: Buffer.byteLength(response.text),
    markerPresent: response.text.includes(MARKER_VALUE),
  };
}

/**
 * Plants the marker in one instance's store and reads it back as both callers.
 *
 * The extension is installed rather than faked: the route answers only for an `IStatefulExtension`
 * the host has loaded, so an instance without one measures the absence of an extension and not the
 * behaviour of the route.
 */
async function measureInstance(harness, { authEnabled }) {
  const install = await harness.installExtension(WHISPARR_SYNC);
  // Both are read through the handle as getters, never captured: an install restarts the container,
  // which re-mints the token and may republish the instance on a different host port.
  const api = createApiClient(
    () => harness.baseUrl,
    () => harness.token,
  );

  const write = await api.put(`${DATA_PATH(EXTENSION_ID)}/${MARKER_KEY}`, MARKER_VALUE);
  const ownerRead = await api.get(DATA_PATH(EXTENSION_ID));
  const control = await api.get(DATA_PATH(ABSENT_EXTENSION_ID));

  const caller = await startCaller(harness.container.getNetworkNames()[0]);
  let anonymous;
  let addresses;
  try {
    addresses = await sourceAddresses(caller);
    const { output } = await caller.exec(["sh", "-c", curlCommand(DATA_PATH(EXTENSION_ID))]);
    anonymous = parseCallerOutput(output);
  } finally {
    await caller.stop();
  }

  const owner = summariseOwnerRead(ownerRead);
  return {
    instance: { image: resolveCoveImage(), authSwitch: AUTH_SWITCH, authEnabled },
    installedExtensionId: install.id,
    write: { status: write.status },
    ownerRead: owner,
    anonymousInNetworkRead: {
      ...anonymous,
      // Sizes rather than fields, so this says whether the anonymous caller was answered as the
      // owner is without any part of either answer having to be recorded.
      answeredAsTheOwnerIs: anonymous.byteLength === owner.byteLength && anonymous.status === 200,
      source: addresses,
    },
    absentExtensionControl: { path: DATA_PATH(ABSENT_EXTENSION_ID), status: control.status },
  };
}

/**
 * The verdict the auth-off observation supports.
 *
 * The anonymous caller is judged first because it is the stronger disclosure and it subsumes the
 * owner's: on this route the two are the same read. An owner read that carries the marker while the
 * anonymous one does not is the middle outcome, and only a read that carries the marker to nobody
 * says the route returns nothing for this extension.
 *
 * A read that never arrived, or one whose write was refused, establishes none of the three and is
 * `inconclusive` rather than the absence it resembles.
 */
export function judgeExposure({
  write,
  ownerRead,
  anonymousInNetworkRead,
  absentExtensionControl,
}) {
  if (write.status < 200 || write.status >= 300) return "inconclusive";
  if (absentExtensionControl.status !== 404) return "inconclusive";
  if (anonymousInNetworkRead.transportError) return "inconclusive";
  if (ownerRead.status !== 200) return "inconclusive";
  if (anonymousInNetworkRead.markerPresent) return "marker-returned-to-anonymous-in-network-caller";
  if (ownerRead.markerPresent) return "marker-returned-to-owner-only";
  return "route-returned-nothing-for-this-extension";
}

export const row = {
  id: "row-12-extension-data-exposure",
  label:
    "What GET /api/extensions/{id}/data returns for an installed extension, and to which caller",
  requires: {
    cove: true,
    // No Whisparr instance is involved: the subject is a Cove route and the store behind it. The
    // in-network caller this row starts for itself is a container running nothing.
    whisparr: [],
    seedHistory: false,
    support: [],
    rootFolder: false,
    network: false,
    live: false,
  },
  async run(ctx) {
    const authOff = await measureInstance(ctx.harness, { authEnabled: "false" });

    // A second instance rather than a setting flipped on the first: the host reads its
    // authentication setting once, at start, so the only way to observe the enforced path is an
    // instance that booted with it on.
    const guarded = await startHarness({ env: { [AUTH_SWITCH]: "true" } });
    let authOn;
    try {
      guarded.owner = await guarded.bootstrapOwner();
      authOn = await measureInstance(guarded, { authEnabled: "true" });
    } finally {
      await guarded.stop();
    }

    return {
      method: {
        verb: "PUT then GET",
        path: `PUT ${DATA_PATH(EXTENSION_ID)}/${MARKER_KEY}, then GET ${DATA_PATH(EXTENSION_ID)} as the owner and from another container on Cove's own network, then GET ${DATA_PATH(ABSENT_EXTENSION_ID)} as the control`,
        inputs: { extensionId: EXTENSION_ID, markerKey: MARKER_KEY, client: "curl" },
      },
      verdict: judgeExposure(authOff),
      observed: {
        verdictVocabulary: VERDICTS.join(" | "),
        markerPolicy:
          "The planted value is synthetic and authorises nothing. Whether it came back is decided inside the container that fetched the response, so only a length, a header name list and a boolean leave it.",
        // The verdict is read off this one. The guarded instance answers a different question and
        // must not be able to change it.
        authOff,
        authOn,
        deploymentVersusRoute:
          authOff.anonymousInNetworkRead.status === authOn.anonymousInNetworkRead.status
            ? "The anonymous in-network read answered the same status on both instances, so the authentication setting did not decide it."
            : `The anonymous in-network read answered ${authOff.anonymousInNetworkRead.status} with authentication off and ${authOn.anonymousInNetworkRead.status} with it on, so what it discloses is a property of the deployment rather than of the route.`,
      },
    };
  },
};
