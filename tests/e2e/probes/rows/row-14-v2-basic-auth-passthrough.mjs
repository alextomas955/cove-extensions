// Whether a callback secret can travel out of band on the older Whisparr generation, which has no
// custom-header field for one.
//
// Two INDEPENDENT questions, and the answer to the pair is what decides the fallback. Either one
// answering no settles it, so both are recorded whatever they say:
//
//   1. Does v2, given a webhook whose user and password fields are set, actually send an
//      authorization header on its delivery? Read off the listener's own capture, never off the
//      registration's echo — a field a schema declares and a save accepts, but the build then does
//      not send, would answer the echo exactly as a working one does.
//   2. Does Cove's own request pipeline pass a request carrying an authorization header through to an
//      extension route that declares the anonymous convention, or does it consume or reject it first?
//
// Question 2 is a DIFFERENTIAL. The extension's own refusal and a pipeline refusal are both 401, so a
// status alone cannot separate them. Three requests per instance make them separable: the callback
// path with no authorization header (what the extension's own refusal looks like here), the same path
// with one, and a sibling path nothing mounts (what routing does when no handler matched). A
// www-authenticate header is the pipeline naming itself; the extension emits none.
//
// The whole of question 2 is taken twice, on an instance with authentication off and on one with it
// on, because a pipeline that ignores an authorization header when nothing parses them says nothing
// about one that does.
//
// The user and password are synthetic and authorise nothing anywhere. Header NAMES are recorded and
// no header VALUE is: a record outlives the run that produced it.
import { resolveCoveImage, startHarness } from "../../lib/harness.mjs";
import { resolveExtensionPaths } from "../../lib/resolve-extension.mjs";

const SCHEMA_PATH = "/api/v3/notification/schema";
const NOTIFICATION_PATH = "/api/v3/notification";

const EXTENSION_ID = "com.alextomas955.whisparrsync";
const CALLBACK_PATH = `/api/extensions/${EXTENSION_ID}/callback`;

// Mounted by nothing, here or anywhere, under the same prefix. Whatever routing answers for it is
// what a path with no POST handler of this extension's looks like, and the callback path's answer is
// read against it.
const ABSENT_PATH = `/api/extensions/${EXTENSION_ID}/no-such-route`;

// One of this extension's PERMISSION-GATED routes, called exactly as the callback path is. It is
// what the pipeline's own answer to an unauthenticated caller looks like on this instance, which is
// what the callback path's answer has to differ from for that answer to be the extension's.
const GATED_PATH = `/api/extensions/${EXTENSION_ID}/connection/test`;

// Obviously synthetic, and they authorise nothing. The password is not recorded at all.
const SYNTHETIC_USER = "cove-probe-row14";
const SYNTHETIC_PASSWORD = "row14-not-a-real-secret";

// The compose file's own switch for the host's authentication, rather than the setting it sets: the
// setting is written once, there, and naming it here would be a second declaration free to drift.
const AUTH_SWITCH = "COVE_E2E_AUTH_ENABLED";

const CAPTURE_TIMEOUT_MS = 60_000;

// The closed set a reader may find in `verdict`. The last is what an observation that establishes
// neither half is called, so a run that measured nothing cannot read as one that measured an absence.
const VERDICTS = [
  "out-of-band-secret-possible-on-v2",
  "out-of-band-secret-impossible-on-v2",
  "inconclusive",
];

// resolveExtensionPaths is the one place allowed to encode the extensions/<Ext>/e2e/lib/… layout.
// This row is not in that layout, so it hands the function the module URL a fixture module there
// would have rather than restating the hops to the repo root for itself.
const WHISPARR_SYNC = resolveExtensionPaths(
  new URL("../../../../extensions/WhisparrSync/e2e/lib/probe.mjs", import.meta.url).href,
  { srcProject: "WhisparrSync" },
);

const triggerFlagNames = (webhook) =>
  Object.entries(webhook)
    .filter(([key, value]) => typeof value === "boolean" && !key.startsWith("supports"))
    .map(([key]) => key);

/** Every trigger except the ones a running instance raises on its own schedule. */
const enabledTriggers = (webhook) =>
  Object.fromEntries(
    triggerFlagNames(webhook).map((name) => [name, !/health|applicationupdate/i.test(name)]),
  );

async function readWebhookSchema(api) {
  const response = await api.get(SCHEMA_PATH);
  const webhook = response.json?.find?.((entry) => entry.implementation === "Webhook");
  if (webhook === undefined) {
    throw new Error(
      `row-14-v2-basic-auth-passthrough: GET ${SCHEMA_PATH} on v2 answered ${response.status} ${response.contentType} and declared no Webhook entry.`,
    );
  }
  return webhook;
}

/**
 * Whether the older generation sends an authorization header when its user and password are set.
 *
 * The registration echoes the fields back on save whatever the build does with them, so the answer is
 * taken from the inbound request the listener captured and from nowhere else.
 */
async function measureV2Delivery(api, listener) {
  const webhook = await readWebhookSchema(api);
  const declared = new Map(webhook.fields.map((field) => [field.name, field]));
  const name = "cove-probe-row14-v2";
  const url = listener.url("/row14/v2/create");

  const missing = ["username", "password"].filter((field) => !declared.has(field));
  if (missing.length > 0) {
    return {
      registered: false,
      whatCouldNotBeObserved: `the v2 Webhook schema declares no ${missing.join(" and no ")} field, so there is nothing to set and no delivery to observe.`,
      sendsAuthorizationHeader: false,
    };
  }

  const created = await api.post(NOTIFICATION_PATH, {
    ...enabledTriggers(webhook),
    name,
    implementation: webhook.implementation,
    implementationName: webhook.implementationName,
    configContract: webhook.configContract,
    tags: [],
    fields: [
      { name: "url", value: url },
      { name: "method", value: 1 },
      { name: "username", value: SYNTHETIC_USER },
      { name: "password", value: SYNTHETIC_PASSWORD },
    ],
  });
  if (created.status !== 201) {
    throw new Error(
      `row-14-v2-basic-auth-passthrough: POST ${NOTIFICATION_PATH} on v2 answered ${created.status} ${created.contentType}: ${created.text.slice(0, 400)}.`,
    );
  }

  const [capture] = await listener.waitForCaptures(1, {
    timeoutMs: CAPTURE_TIMEOUT_MS,
    match: (delivery) => delivery.path === "/row14/v2/create",
  });

  const headerNames = Object.keys(capture.headers).map((header) => header.toLowerCase());
  return {
    registered: true,
    registrationStatus: created.status,
    fieldsSet: ["url", "method", "username", "password"],
    syntheticUser: SYNTHETIC_USER,
    passwordRecorded: false,
    deliveredHeaderNames: [...new Set(headerNames)].sort().join(" "),
    // The whole answer, read off the inbound request rather than off the save's echo.
    sendsAuthorizationHeader: headerNames.includes("authorization"),
    capturedAt: capture.ts,
  };
}

/** One request to a Cove route, summarised without any header value bar the pipeline's own challenge. */
async function callCove(baseUrl, path, { authorization } = {}) {
  let response;
  try {
    response = await fetch(`${baseUrl}${path}`, {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        ...(authorization === undefined ? {} : { Authorization: authorization }),
      },
      body: "{}",
    });
  } catch (cause) {
    return { path, status: 0, transportError: cause.message };
  }

  const text = await response.text();
  return {
    path,
    carriedAuthorizationHeader: authorization !== undefined,
    status: response.status,
    contentType: response.headers.get("content-type") ?? "",
    byteLength: text.length,
    headerNames: [...response.headers.keys()]
      .map((name) => name.toLowerCase())
      .sort()
      .join(" "),
    // The one header value taken, because it is the pipeline naming itself rather than anything
    // belonging to the caller.
    wwwAuthenticate: response.headers.get("www-authenticate") ?? null,
    transportError: "",
  };
}

/**
 * Whether an authorization header reaches an anonymous extension route on this instance.
 *
 * `unchanged` is the finding: the same answer with the header as without it means the pipeline
 * neither rejected it nor diverted the request, so what the extension's handler sees is what arrived.
 */
async function measurePassthrough(harness, { authEnabled }) {
  const install = await harness.installExtension(WHISPARR_SYNC);

  // Read through the handle rather than captured: an install restarts the container, which re-mints
  // the token and may republish the instance on a different host port.
  const withoutHeader = await callCove(harness.baseUrl, CALLBACK_PATH);
  const withHeader = await callCove(harness.baseUrl, CALLBACK_PATH, {
    authorization: `Basic ${btoa(`${SYNTHETIC_USER}:${SYNTHETIC_PASSWORD}`)}`,
  });
  const absentRoute = await callCove(harness.baseUrl, ABSENT_PATH);
  const gatedRoute = await callCove(harness.baseUrl, GATED_PATH);

  // Three things together, because no one of them is enough. A POST handler answers at the callback
  // path; an arbitrary path under the same prefix does NOT answer the same way, so the answer is not
  // a catch-all; and the answer differs from what the pipeline's own refusal looks like on this
  // instance, read off a permission-gated route of this extension's called the same way. With
  // authentication off that route is answered outright, and with it on it is refused with a body of
  // its own — either way an answer the callback path does not give is one the pipeline did not give.
  const differsFromThePipelinesOwnRefusal =
    gatedRoute.status !== withoutHeader.status ||
    gatedRoute.contentType !== withoutHeader.contentType ||
    gatedRoute.byteLength !== withoutHeader.byteLength;
  const reached =
    ![0, 404, 405].includes(withoutHeader.status) &&
    absentRoute.status !== withoutHeader.status &&
    differsFromThePipelinesOwnRefusal;
  const unchanged =
    reached &&
    withHeader.status === withoutHeader.status &&
    withHeader.contentType === withoutHeader.contentType &&
    withHeader.byteLength === withoutHeader.byteLength &&
    withHeader.wwwAuthenticate === withoutHeader.wwwAuthenticate;

  return {
    instance: { image: resolveCoveImage(), authSwitch: AUTH_SWITCH, authEnabled },
    installedExtensionId: install.id,
    // Without this the two 401s below would be indistinguishable from a route nothing mounted.
    routeIsMountedAndAnswers: reached,
    withoutAuthorizationHeader: withoutHeader,
    withAuthorizationHeader: withHeader,
    absentRouteControl: absentRoute,
    permissionGatedControl: gatedRoute,
    callbackAnswerDiffersFromThePipelinesOwnRefusal: differsFromThePipelinesOwnRefusal,
    pipelineChallenged: withHeader.wwwAuthenticate !== null,
    // The answer to question 2 on this instance.
    passesAuthorizationHeaderThrough: unchanged,
  };
}

/**
 * The verdict the two halves support.
 *
 * Both must be yes for a secret to travel out of band on the older generation. Either answering no
 * selects D-10's stated fallback, and an unestablished half is `inconclusive` rather than the no it
 * resembles.
 */
export function judgeOutOfBandOnV2({ v2Delivery, passthrough }) {
  if (typeof v2Delivery?.sendsAuthorizationHeader !== "boolean") return "inconclusive";
  if (passthrough.some((one) => one.routeIsMountedAndAnswers !== true)) return "inconclusive";
  const passesEverywhere = passthrough.every(
    (one) => one.passesAuthorizationHeaderThrough === true,
  );
  return v2Delivery.sendsAuthorizationHeader && passesEverywhere
    ? "out-of-band-secret-possible-on-v2"
    : "out-of-band-secret-impossible-on-v2";
}

export const row = {
  id: "row-14-v2-basic-auth-passthrough",
  label:
    "Whether Whisparr v2 sends an authorization header, and whether Cove delivers one to an anonymous extension route",
  requires: {
    // Whisparr and the listener both join the Cove instance's own network, so a row asking for
    // either asks for cove too.
    cove: true,
    whisparr: ["v2"],
    seedHistory: false,
    support: ["webhook-listener"],
    rootFolder: false,
    network: false,
    live: false,
  },
  async run(ctx) {
    const v2Delivery = await measureV2Delivery(
      ctx.whisparr.apiFor("v2"),
      ctx.support["webhook-listener"],
    );

    const authOff = await measurePassthrough(ctx.harness, { authEnabled: "false" });

    // A second instance rather than a setting flipped on the first: the host reads its authentication
    // setting once, at start, so the only way to observe the enforced path is an instance that booted
    // with it on.
    const guarded = await startHarness({ env: { [AUTH_SWITCH]: "true" } });
    let authOn;
    try {
      guarded.owner = await guarded.bootstrapOwner();
      authOn = await measurePassthrough(guarded, { authEnabled: "true" });
    } finally {
      await guarded.stop();
    }

    const passthrough = [authOff, authOn];
    const verdict = judgeOutOfBandOnV2({ v2Delivery, passthrough });

    return {
      method: {
        verb: "POST",
        path: `${SCHEMA_PATH} then ${NOTIFICATION_PATH} on v2 against a listener, then ${CALLBACK_PATH} with and without an authorization header, with ${ABSENT_PATH} and ${GATED_PATH} as the controls, on an auth-off and an auth-on Cove`,
        inputs: { syntheticUser: SYNTHETIC_USER, extensionId: EXTENSION_ID },
      },
      verdict,
      observed: {
        verdictVocabulary: VERDICTS.join(" | "),
        headerPolicy:
          "Header NAMES only. The one header VALUE recorded is www-authenticate, which is the pipeline naming itself rather than anything belonging to a caller. The synthetic password is never recorded.",
        // Question 1.
        v2Delivery,
        // Question 2, per instance.
        passthrough: {
          authOff,
          authOn,
          note: "A pipeline that ignores an authorization header where nothing parses them says nothing about one that does, so the auth-on instance is not a repeat of the auth-off one.",
        },
        // Stated rather than left to be inferred from two booleans.
        consequence:
          verdict === "out-of-band-secret-possible-on-v2"
            ? "On v2 the registration can carry the secret in the user and password fields, so the registered address carries none on either generation."
            : "On v2 the registration carries the secret in the address, plus the standing quiet note that sits under the registration status while events still arrive that way and clears once one arrives out of band. That is the state HOOK-3 already describes, so the fallback needs no new behaviour beyond the note.",
        gapCrossReference: {
          id: "GAP-1",
          why: "GAP-1 records that v2's Webhook connection declares no headers field. This row settles the only remaining alternative that would have kept a v2 secret off the address.",
        },
        limits:
          "Question 2 establishes that Cove neither rejected nor diverted a request carrying an authorization header, taken as a differential against the same request without one and against two controls. It does not read the header out of the extension's own handler, so it bounds the pipeline rather than proving what the handler saw.",
      },
    };
  },
};
