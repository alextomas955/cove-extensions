// What Cove does with a request that arrives from another container on its own network while
// authentication is off.
//
// The SOURCE ADDRESS is the whole subject, so the request has to originate inside the network. The
// same call issued from the test process arrives through the published port from the daemon's
// gateway, which is a different source and therefore a different question — it is taken anyway, as
// the comparison the in-network result is read against.
//
// A first attempt and a second attempt from the same address are both taken, because "refuses a
// FIRST remote callback" is a claim about the pair and not about either one alone.
//
// The configuration response is not summarised the way the others are. Under this instance's auth
// setting the caller is unauthenticated and the body comes back unredacted, so only its size, its
// shape and its header names are recorded, and its content never leaves the container it was
// fetched in. A body head is taken only from a refusal, which by definition carries no
// configuration.
import { Buffer } from "node:buffer";

import { resolveCoveImage } from "../../lib/harness.mjs";

// The compose service name, which is also the hostname a container on that network addresses, and
// the port the application listens on inside its own container rather than the published one.
const COVE_HOST = "cove";
const COVE_PORT = 5073;

// Files inside the container the client writes to, so a response body is never carried back through
// the exec's stdout.
const BODY_FILE = "/tmp/rc-body";
const HEADER_FILE = "/tmp/rc-headers";
const META_FILE = "/tmp/rc-meta";
const ERROR_FILE = "/tmp/rc-error";

const HEADER_MARKER = "HEADERS";
const BODY_HEAD_BYTES = 300;

const AUTH_ENABLED_VARIABLE = "COVE__Auth__Enabled";

// A liveness route, a privileged read, and a read-only POST — enough to show whether the boundary
// treats a verb or a privilege level differently, without asking the instance to change anything.
const ROUTES = [
  { verb: "GET", path: "/health", privileged: false },
  { verb: "GET", path: "/api/system/config", privileged: true },
  { verb: "POST", path: "/api/videos/find", body: "{}", privileged: false },
];

// The statuses a boundary refusal would arrive as. A refusal on the transport is handled separately,
// because it produces no status at all.
const REFUSAL_STATUSES = new Set([401, 403]);

const VERDICTS = ["refused-first-then-allowed", "refused-always", "allowed-always", "inconclusive"];

const shapeOf = (firstByte, bytes) => {
  if (bytes === 0) return "empty";
  if (firstByte === "{") return "json-object";
  if (firstByte === "[") return "json-array";
  return "non-json";
};

/**
 * A client-agnostic view of one response.
 *
 * Header NAMES rather than header pairs: a refusal names itself in a header name, and a value here
 * could carry a session the record has no business keeping. The one value taken is the challenge
 * header, which is the refusal's own explanation.
 */
function summarise({
  status,
  contentType,
  bytes,
  firstByte,
  headerLines,
  bodyHead,
  transportError,
}) {
  const headers = headerLines
    .map((line) => /^([A-Za-z0-9-]+):/.exec(line)?.[1]?.toLowerCase())
    .filter((name) => name !== undefined);
  const challenge = headerLines.find((line) => /^www-authenticate:/i.test(line)) ?? null;
  return {
    status,
    contentType,
    byteLength: bytes,
    bodyShape: shapeOf(firstByte, bytes),
    headerNames: [...new Set(headers)].sort().join(" "),
    wwwAuthenticate: challenge,
    // Present only for a non-success, so a configuration body cannot arrive here.
    ...(bodyHead === null ? {} : { refusalBodyHead: bodyHead }),
    ...(transportError ? { transportError } : {}),
  };
}

const shellQuote = (value) => `'${String(value).replaceAll("'", `'\\''`)}'`;

/** The command a container runs to make one call and report everything but the body. */
function curlCommand({ verb, path, body }) {
  const url = shellQuote(`http://${COVE_HOST}:${COVE_PORT}${path}`);
  const payload =
    body === undefined ? "" : ` -H 'Content-Type: application/json' --data ${shellQuote(body)}`;
  return [
    `curl -sS -X ${verb}${payload} -D ${HEADER_FILE} -o ${BODY_FILE}`,
    `-w '%{http_code} %{content_type}' ${url} > ${META_FILE} 2> ${ERROR_FILE};`,
    `printf 'EXIT=%s\\n' "$?";`,
    `printf 'META=%s\\n' "$(cat ${META_FILE})";`,
    `printf 'BYTES=%s\\n' "$(wc -c < ${BODY_FILE})";`,
    `printf 'FIRST=%s\\n' "$(head -c 1 ${BODY_FILE})";`,
    `printf 'ERR=%s\\n' "$(head -c 200 ${ERROR_FILE})";`,
    `printf '${HEADER_MARKER}\\n';`,
    `cat ${HEADER_FILE} 2>/dev/null || true`,
  ].join(" ");
}

/** The same call and the same report, for an image without the first client. */
function pythonProgram({ verb, path, body }) {
  return [
    "import sys, urllib.request, urllib.error",
    `url = "http://${COVE_HOST}:${COVE_PORT}${path}"`,
    body === undefined ? "data = None" : `data = ${JSON.stringify(body)}.encode()`,
    `req = urllib.request.Request(url, method=${JSON.stringify(verb)}, data=data)`,
    "if data is not None: req.add_header('Content-Type', 'application/json')",
    "try:",
    "    r = urllib.request.urlopen(req)",
    "    status, headers, payload, err = r.status, r.headers.items(), r.read(), ''",
    "except urllib.error.HTTPError as e:",
    "    status, headers, payload, err = e.code, e.headers.items(), e.read(), ''",
    "except Exception as e:",
    "    status, headers, payload, err = 0, [], b'', str(e)[:200]",
    `open(${JSON.stringify(BODY_FILE)}, 'wb').write(payload)`,
    "ct = ''",
    "for k, v in headers:",
    "    if k.lower() == 'content-type': ct = v",
    "print('EXIT=%s' % (0 if err == '' else 7))",
    "print('META=%s %s' % (status, ct))",
    "print('BYTES=%s' % len(payload))",
    "print('FIRST=%s' % (chr(payload[0]) if payload else ''))",
    "print('ERR=%s' % err)",
    `print(${JSON.stringify(HEADER_MARKER)})`,
    "for k, v in headers: print('%s: %s' % (k, v))",
  ].join("\n");
}

function parseClientOutput(output) {
  const lines = output.split("\n").map((line) => line.replace(/\r$/, ""));
  const marker = lines.indexOf(HEADER_MARKER);
  const field = (name) =>
    lines.find((line) => line.startsWith(`${name}=`))?.slice(name.length + 1) ?? "";
  const [status, ...contentType] = field("META").split(" ");
  return {
    exit: Number(field("EXIT")),
    status: Number(status) || 0,
    contentType: contentType.join(" "),
    bytes: Number(field("BYTES")) || 0,
    firstByte: field("FIRST"),
    error: field("ERR"),
    headerLines: marker === -1 ? [] : lines.slice(marker + 1).filter((line) => line !== ""),
  };
}

/** Which client the image offers, preferring the one that needs no program written for it. */
async function resolveClient(container) {
  const { output } = await container.exec([
    "sh",
    "-c",
    "command -v curl >/dev/null && echo curl || (command -v python3 >/dev/null && echo python3 || echo none)",
  ]);
  const client = output.trim();
  if (client === "none") {
    throw new Error(
      "row-13-remote-callback: the container offers neither curl nor python3, so no request can be made from inside the network, which is the only place this row's subject exists.",
    );
  }
  return client;
}

/** One call made from inside the network, reported without its body. */
async function callFromContainer(container, client, route) {
  const command =
    client === "curl" ? ["sh", "-c", curlCommand(route)] : ["python3", "-c", pythonProgram(route)];
  const { output } = await container.exec(command);
  const parsed = parseClientOutput(output);
  const success = parsed.status >= 200 && parsed.status < 300;
  const bodyHead =
    success || parsed.bytes === 0
      ? null
      : (await container.exec(["sh", "-c", `head -c ${BODY_HEAD_BYTES} ${BODY_FILE}`])).output;
  return {
    verb: route.verb,
    path: route.path,
    ...summarise({
      ...parsed,
      bodyHead,
      transportError: parsed.exit === 0 ? "" : parsed.error || `client exited ${parsed.exit}`,
    }),
  };
}

/** The same call from the test process, which reaches the published port from the daemon's gateway. */
async function callFromHost(baseUrl, route) {
  let response;
  try {
    response = await fetch(`${baseUrl}${route.path}`, {
      method: route.verb,
      ...(route.body === undefined
        ? {}
        : { headers: { "Content-Type": "application/json" }, body: route.body }),
    });
  } catch (cause) {
    return {
      verb: route.verb,
      path: route.path,
      ...summarise({
        status: 0,
        contentType: "",
        bytes: 0,
        firstByte: "",
        headerLines: [],
        bodyHead: null,
        transportError: cause.message,
      }),
    };
  }
  const text = await response.text();
  const success = response.ok;
  return {
    verb: route.verb,
    path: route.path,
    ...summarise({
      status: response.status,
      contentType: response.headers.get("content-type") ?? "",
      bytes: text.length,
      firstByte: text.charAt(0),
      headerLines: [...response.headers].map(([name, value]) => `${name}: ${value}`),
      bodyHead: success || text === "" ? null : text.slice(0, BODY_HEAD_BYTES),
      transportError: "",
    }),
  };
}

const byRoute = (observations) =>
  Object.fromEntries(observations.map((one) => [`${one.verb} ${one.path}`, one]));

/**
 * What one observation says about the boundary: `allowed`, `refused`, or `unreached`.
 *
 * A call that never arrived is its own state. No status at all is not a refusal, and counting it as
 * one records a name that would not resolve, or a container that lost the network, as an instance
 * refusing a callback it never received.
 */
export function classifyObservation(observation) {
  if (observation.transportError || observation.status === 0) return "unreached";
  return REFUSAL_STATUSES.has(observation.status) ? "refused" : "allowed";
}

/**
 * The verdict the two in-network rounds support, which every route has to agree on.
 *
 * The routes differ in privilege, so one of them refusing is a fact about that route. A verdict
 * about the boundary as a whole needs all of them, and a disagreement is `inconclusive` rather than
 * whichever route was read first.
 *
 * @param {{first: string, repeat: string}[]} perRoute
 */
export function judgeBoundary(perRoute) {
  if (perRoute.length === 0) return "inconclusive";
  if (perRoute.some((route) => route.first === "unreached" || route.repeat === "unreached")) {
    return "inconclusive";
  }
  const everyRoute = (first, repeat) =>
    perRoute.every((route) => route.first === first && route.repeat === repeat);
  if (everyRoute("refused", "refused")) return "refused-always";
  if (everyRoute("refused", "allowed")) return "refused-first-then-allowed";
  if (everyRoute("allowed", "allowed")) return "allowed-always";
  return "inconclusive";
}

/**
 * Where the request came from, as the container itself reports it.
 *
 * Every attached network is listed, because a container on more than one has more than one source
 * address and which of them a route selects is not this row's to decide silently.
 */
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

export const row = {
  id: "row-13-remote-callback",
  label: "How Cove answers a request from another container on its network with auth off",
  requires: {
    cove: true,
    // One generation is enough: the subject is the address a container calls from, not what the
    // application at that address is.
    whisparr: ["v3"],
    seedHistory: false,
    support: [],
    rootFolder: false,
    network: false,
    live: false,
  },
  async run(ctx) {
    const container = ctx.whisparr.v3.container;
    const client = await resolveClient(container);
    const addresses = await sourceAddresses(container);

    const inNetworkFirst = [];
    for (const route of ROUTES)
      inNetworkFirst.push(await callFromContainer(container, client, route));

    const fromHost = [];
    for (const route of ROUTES) fromHost.push(await callFromHost(ctx.harness.baseUrl, route));

    const inNetworkRepeat = [];
    for (const route of ROUTES)
      inNetworkRepeat.push(await callFromContainer(container, client, route));

    const perRoute = ROUTES.map((route, index) => ({
      route: `${route.verb} ${route.path}`,
      privileged: route.privileged,
      first: classifyObservation(inNetworkFirst[index]),
      repeat: classifyObservation(inNetworkRepeat[index]),
    }));
    const verdict = judgeBoundary(perRoute);
    const unreached = perRoute.filter(
      (route) => route.first === "unreached" || route.repeat === "unreached",
    );

    const { output: authSetting } = await ctx.harness.exec([
      "sh",
      "-c",
      `printenv ${AUTH_ENABLED_VARIABLE}`,
    ]);
    const coveConfig = await fetch(`${ctx.harness.baseUrl}/api/system/config`, {
      headers: { Authorization: `Bearer ${ctx.harness.token}` },
    });
    const ownerConfigText = await coveConfig.text();
    const security = JSON.parse(ownerConfigText)?.security ?? {};
    // Cove answers this route differently by privilege, blanking part of it for a caller that lacks
    // the settings permission. Comparing sizes rather than fields says whether the unauthenticated
    // caller was answered as the owner is, without depending on which fields the blanking picks.
    const anonymousInNetwork = inNetworkFirst.find(
      (observation) => observation.path === "/api/system/config",
    );
    const privilegeOfTheRemoteCaller = {
      route: "/api/system/config",
      ownerByteLength: Buffer.byteLength(ownerConfigText),
      remoteAnonymousByteLength: anonymousInNetwork.byteLength,
      answeredAsTheOwnerIs: Buffer.byteLength(ownerConfigText) === anonymousInNetwork.byteLength,
    };

    return {
      method: {
        verb: "GET and POST",
        path: `${ROUTES.map((route) => `${route.verb} ${route.path}`).join(", ")}, from inside the network, from the host through the published port, and from inside the network again`,
        inputs: { client, target: `http://${COVE_HOST}:${COVE_PORT}` },
      },
      verdict,
      observed: {
        verdictVocabulary: VERDICTS.join(" | "),
        ...(verdict === "inconclusive"
          ? {
              whatCouldNotBeObserved:
                unreached.length === 0
                  ? "The two in-network rounds disagreed across routes, so neither a refusal nor an allowance holds for the boundary as a whole."
                  : `${unreached.map((route) => route.route).join(", ")} never reached the instance, so nothing observed on them attributes to the boundary.`,
            }
          : {}),
        // The verdict, route by route, so a reader can check it rather than take it.
        perRoute,
        instance: {
          image: resolveCoveImage(),
          authEnabledVariable: AUTH_ENABLED_VARIABLE,
          authEnabled: authSetting.trim(),
        },
        // The two values an address-based failsafe would be configured by. Nothing else from that
        // response is taken.
        addressFailsafeConfiguration: {
          source: "/api/system/config",
          knownProxies: (security.knownProxies ?? []).join(" "),
          trustedHosts: (security.trustedHosts ?? []).join(" "),
        },
        privilegeOfTheRemoteCaller,
        source: {
          client,
          clientNote:
            "Recorded because the two images do not ship the same clients, and a later reader cannot recover which one made the call.",
          ...addresses,
        },
        bodyPolicy:
          "Status, content type, byte length, body shape and header names are recorded for every observation. A body head is taken only from a non-success, because the privileged read answers an unauthenticated caller with an unredacted configuration.",
        inNetworkFirst: byRoute(inNetworkFirst),
        fromHostThroughPublishedPort: byRoute(fromHost),
        inNetworkRepeat: byRoute(inNetworkRepeat),
        secondAttemptDiffers: Object.fromEntries(
          inNetworkFirst.map((first, index) => [
            `${first.verb} ${first.path}`,
            first.status !== inNetworkRepeat[index].status ||
              first.bodyShape !== inNetworkRepeat[index].bodyShape,
          ]),
        ),
      },
    };
  },
};
