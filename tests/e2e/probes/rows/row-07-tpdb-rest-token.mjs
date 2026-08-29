// Settles whether the ThePornDB credential this machine's Cove install already holds authenticates
// on that provider's REST host.
//
// The credential is stored against the stash-box GraphQL form of the provider, and a later phase
// wants to read the same provider over REST. Those are two different hosts, so whether one token
// covers both is a question rather than a fact, and this row asks it once.
//
// A refusal is the answer to that question, recorded in the spec's own terms. This row does not look
// for another way in.
//
// The credential is held for the length of one request header and described everywhere else, because
// a record outlives the run and is read by someone who was not here.
import { CONFIG_FILE, describeServers, liftMetadataServers } from "../../lib/cove-providers.mjs";

const PROVIDER_NAME = "ThePornDB";
const REST_ORIGIN = "https://api.theporndb.net";

// Tried in order until one answers definitively. A path the host does not serve says nothing about
// the credential, and the first one asks the host only to report the caller, which is the smallest
// question that has an answer.
const CANDIDATE_PATHS = ["/auth/user", "/scenes?per_page=1"];

// The statuses that attribute an answer to the credential. Every other status is a statement about
// the path instead, so the row keeps looking rather than reading one as a verdict.
const DEFINITIVE_STATUSES = new Set([200, 401, 403]);

const AUTH_SCHEME = "Authorization: Bearer";

const REQUEST_TIMEOUT_MS = 20_000;

// The rate to fall back to when the install declares none. Slower than any provider would impose, so
// an absent configuration cannot become a faster run.
const UNCONFIGURED_INTERVAL_MS = 1_000;

// A record is read by a person and transcribed by hand, so a list in one is summarised rather than
// carried whole.
const MAX_LISTED = 20;

/**
 * A list as a record carries it: how many there were, and enough of them to recognise.
 *
 * A truncated list says so, so a reader can tell a short answer from a shortened one.
 */
export function bounded(values) {
  const list = Array.isArray(values) ? values : [];
  return {
    count: list.length,
    values: list.slice(0, MAX_LISTED),
    truncated: list.length > MAX_LISTED,
  };
}

/**
 * A gate holding outbound calls to the rate the machine's own install is configured for.
 *
 * That rate is the closest available proxy for the provider's real limit: it is what the install
 * that supplied the credential already asks this service at. The enforced minimum interval is the
 * guarantee; the rates the report carries are there so the claim is arithmetic a reader can check
 * rather than an assurance.
 *
 * Each call is charged one whole interval, the first included, so the window a rate is divided over
 * covers every call's own slot. Dividing a handful of calls by the span between the first and the
 * last instead reports a burst shorter than a minute as a rate per minute, which reads as a limit
 * broken by a run that never broke one.
 *
 * @param {{requestsPerMinute: number|null|undefined, label: string}} options
 */
export function pacer({ requestsPerMinute, label }) {
  const configured =
    Number.isFinite(requestsPerMinute) && requestsPerMinute > 0 ? requestsPerMinute : null;
  const minIntervalMs =
    configured === null ? UNCONFIGURED_INTERVAL_MS : Math.ceil(60_000 / configured);
  const startedAt = Date.now();
  let previous = 0;
  let calls = 0;
  let shortestGapMs = null;

  return {
    async wait() {
      const due = previous + minIntervalMs - Date.now();
      if (due > 0) await new Promise((resolve) => setTimeout(resolve, due));
      const now = Date.now();
      if (previous !== 0) {
        const gap = now - previous;
        shortestGapMs = shortestGapMs === null ? gap : Math.min(shortestGapMs, gap);
      }
      previous = now;
      calls += 1;
    },
    report() {
      const elapsedMs = Date.now() - startedAt;
      const chargedWindowMs = elapsedMs + minIntervalMs;
      const peak = shortestGapMs === null ? null : Number((60_000 / shortestGapMs).toFixed(2));
      return {
        label,
        configuredRequestsPerMinute: configured,
        minIntervalMs,
        calls,
        elapsedMs,
        chargedWindowMs,
        shortestGapMs,
        requestsPerMinute:
          calls === 0 ? null : Number(((calls * 60_000) / chargedWindowMs).toFixed(2)),
        peakRequestsPerMinute: peak,
        withinConfiguredRate: configured === null || peak === null ? null : peak <= configured,
      };
    },
  };
}

/**
 * The install's entry for one provider, or the reason there is none.
 *
 * No install, an install naming no such provider, and an entry carrying no key are three different
 * reasons, and each is named. None of them is an error: a machine with nothing to ask with has
 * answered the question it was asked, and the install is never a thing to change in response.
 *
 * `described` is the only form the entry may be recorded in, and a skip names the document that was
 * consulted rather than where that document lives.
 *
 * @param {string} name
 * @returns {{entry: object|null, described: object|null, skip: string|null}}
 */
export function liftProvider(name) {
  const lifted = liftMetadataServers({ names: [name] });
  const entry = lifted.servers[0];
  if (entry === undefined) {
    return {
      entry: null,
      described: null,
      // The provider leads the reason, because the reason is read where the record lists several.
      skip: `${name}: ${lifted.skip ?? `${CONFIG_FILE} declares no metadata server named ${name}`}`,
    };
  }
  if (typeof entry.apiKey !== "string" || entry.apiKey.length === 0) {
    return {
      entry: null,
      described: describeServers([entry])[0],
      skip: `${name}: ${CONFIG_FILE} declares it but carries no api key for it`,
    };
  }
  return { entry, described: describeServers([entry])[0], skip: null };
}

/** The rate-limit headers a provider answered with, and only the ones it actually sent. */
function rateLimitHeaders(headers) {
  const interesting = [
    "retry-after",
    "x-ratelimit-limit",
    "x-ratelimit-remaining",
    "x-ratelimit-reset",
  ];
  return Object.fromEntries(
    interesting.map((name) => [name, headers.get(name)]).filter(([, value]) => value !== null),
  );
}

/**
 * One REST call, described down to what the row records.
 *
 * The body is reduced to whether it parsed and to its top-level key names. Nothing a provider
 * returned is kept as a value, and the credential never reaches the URL, an error or the result.
 *
 * A host this machine cannot reach throws naming the host, so an unreachable service cannot be read
 * as a service that answered something.
 */
async function restCall(path, { token, pace }) {
  await pace.wait();
  let response;
  try {
    response = await fetch(`${REST_ORIGIN}${path}`, {
      headers: {
        Accept: "application/json",
        ...(token === null ? {} : { Authorization: `Bearer ${token}` }),
      },
      signal: AbortSignal.timeout(REQUEST_TIMEOUT_MS),
    });
  } catch (cause) {
    throw new Error(`${REST_ORIGIN} could not be reached for ${path}: ${cause.message}`, { cause });
  }

  const text = await response.text();
  let parsed;
  try {
    parsed = JSON.parse(text);
  } catch {
    parsed = undefined;
  }
  const isObject = parsed !== undefined && parsed !== null && typeof parsed === "object";

  return {
    path,
    sentCredential: token !== null,
    status: response.status,
    contentType: response.headers.get("content-type") ?? "",
    isJson: isObject,
    topLevel: bounded(isObject && !Array.isArray(parsed) ? Object.keys(parsed) : []),
    rateLimit: rateLimitHeaders(response.headers),
  };
}

/** Whether two endpoints name the same host, or null when the stored one will not parse as a URL. */
function sameHostAs(storedEndpoint, origin) {
  try {
    return new URL(storedEndpoint).host === new URL(origin).host;
  } catch {
    return null;
  }
}

/**
 * The verdict, and what it rests on.
 *
 * A 200 alone does not attribute to the credential: a surface that answers the same without one has
 * said nothing about the token, and that case is `inconclusive` rather than a pass.
 */
function judge(definitive, control, { storedEndpoint }) {
  if (definitive === undefined) {
    return {
      verdict: "inconclusive",
      notObservable: `no probed path answered ${[...DEFINITIVE_STATUSES].join(", ")}, so nothing the host returned attributes to the credential.`,
      finding: null,
    };
  }
  if (definitive.status !== 200) {
    return {
      verdict: "rejected",
      notObservable: null,
      finding: `The credential this machine's Cove install stores for ${PROVIDER_NAME} is stored against ${storedEndpoint}, and ${REST_ORIGIN} answered ${definitive.status} to it on ${definitive.path}. The spec assumes one token covers both surfaces, and it does not. The correction belongs in the spec, which should record which surface the stored credential covers.`,
    };
  }
  if (control.status === 200) {
    return {
      verdict: "inconclusive",
      notObservable: `${definitive.path} answered 200 with no credential as well as with one, so a 200 here says nothing about the token.`,
      finding: null,
    };
  }
  return { verdict: "valid", notObservable: null, finding: null };
}

export const row = {
  id: "row-07-tpdb-rest-token",
  label: `Does the ${PROVIDER_NAME} credential Cove stores authenticate on ${REST_ORIGIN}?`,
  requires: {
    cove: false,
    whisparr: [],
    seedHistory: false,
    support: [],
    rootFolder: false,
    network: true,
    // Reaches a service this project does not own, with a real personal credential.
    live: true,
  },
  async run() {
    const { entry, described, skip } = liftProvider(PROVIDER_NAME);
    if (skip !== null) {
      return {
        method: null,
        verdict: "skipped",
        skip,
        observed: { provider: PROVIDER_NAME, credential: described, calls: 0 },
      };
    }

    const pace = pacer({
      requestsPerMinute: entry.maxRequestsPerMinute,
      label: `${PROVIDER_NAME} as this install configures it`,
    });

    const attempts = [];
    let definitive;
    for (const path of CANDIDATE_PATHS) {
      const attempt = await restCall(path, { token: entry.apiKey, pace });
      attempts.push(attempt);
      if (DEFINITIVE_STATUSES.has(attempt.status)) {
        definitive = attempt;
        break;
      }
    }

    // The same path with nothing sent, so a 200 can be attributed to the credential rather than to
    // the path being open.
    const control =
      definitive === undefined ? null : await restCall(definitive.path, { token: null, pace });

    const { verdict, notObservable, finding } = judge(definitive, control ?? { status: null }, {
      storedEndpoint: entry.endpoint,
    });

    return {
      method: {
        verb: "GET",
        path: `${REST_ORIGIN}${definitive?.path ?? CANDIDATE_PATHS[0]}`,
        inputs: { authScheme: AUTH_SCHEME, candidatePaths: CANDIDATE_PATHS },
      },
      verdict,
      observed: {
        provider: PROVIDER_NAME,
        credential: described,
        // The endpoint the token is stored for and the host it was tested against, side by side:
        // that difference is what the row exists to settle.
        storedEndpoint: entry.endpoint,
        testedOrigin: REST_ORIGIN,
        sameHost: sameHostAs(entry.endpoint, REST_ORIGIN),
        attempts,
        control,
        finding,
        notObservable,
        pace: pace.report(),
      },
    };
  },
};
