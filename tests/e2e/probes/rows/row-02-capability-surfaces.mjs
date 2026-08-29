// Records what each Whisparr generation can actually be asked to do, from the strongest evidence
// that generation offers: v3 publishes a document about itself, and v2 publishes none.
//
// The two halves are therefore different KINDS of evidence, and the record says which is which. A
// document could omit a route the build serves, or list one it does not; a hand probe covers only
// the routes someone thought to try. A route absent from the v2 half is unknown, not absent.
//
// Three traps invert this row's answers if they are not defeated inside it:
//   - v3 answers an unmatched path with its frontend and a 200, so a status is not a presence;
//   - one route answers 404 to GET and 202 to POST, so a verb-blind probe records it absent;
//   - one lookup answers 503 with no term and 200 with one, so an input-blind probe does the same.
const OPENAPI_PATH = "/docs/v3/openapi.json";
const LOOKUP_TERM = "brazzers";

// A term the vendor's metadata service answers for, so the lookups below exercise the route rather
// than its empty case.
const V2_LOOKUP_PATH = "/api/v3/series/lookup";
const V3_LOOKUP_PATH = "/api/v3/lookup/studio";

// Deep enough to reach an image entry inside a lookup result, shallow enough that a deeply nested
// document cannot turn this into a walk of the whole response.
const URL_SEARCH_DEPTH = 5;

// The routes later phases depend on, read out of v3's own document rather than called. `unpaged`
// marks a route whose row count grows with the library: it is recorded as declared and never
// fetched, because one call would put a page of it in this record.
//
// The works route is listed under two spellings on purpose. A path template is part of the route's
// identity, so a candidate a document does not declare and a route a build does not serve are
// indistinguishable unless both spellings are recorded side by side.
const V3_CANDIDATES = [
  { verb: "GET", path: "/api/v3/wanted/missing" },
  { verb: "GET", path: "/api/v3/wanted/cutoff" },
  { verb: "GET", path: "/api/v3/performer" },
  { verb: "POST", path: "/api/v3/performer" },
  { verb: "GET", path: "/api/v3/studio" },
  { verb: "POST", path: "/api/v3/studio" },
  { verb: "GET", path: "/api/v3/studio/{id}/works", unpaged: true },
  { verb: "GET", path: "/api/v3/studio/{studioForeignId}/works", unpaged: true },
  { verb: "GET", path: "/api/v3/exclusions" },
  { verb: "POST", path: "/api/v3/exclusions" },
  { verb: "POST", path: "/api/v3/exclusions/bulk" },
  { verb: "DELETE", path: "/api/v3/exclusions/bulk" },
  { verb: "GET", path: "/api/v3/lookup/scene" },
  { verb: "GET", path: "/api/v3/lookup/movie" },
  { verb: "GET", path: "/api/v3/lookup/studio" },
  { verb: "GET", path: "/api/v3/lookup/performer" },
  { verb: "PATCH", path: "/api/v3/movie/bulk/monitor" },
  { verb: "PUT", path: "/api/v3/movie/editor" },
  { verb: "DELETE", path: "/api/v3/movie/editor" },
];

// Hand-probed, because this generation publishes nothing to derive a table from. The verb is part of
// the candidate, and the entries carrying a body or a query exist to defeat the two traps above
// rather than to enumerate a surface. `as` names the record's key where a path alone would collide.
//
// The nonsense path is this half's own negative control: a reader can see from it that a miss on
// this generation looks nothing like a miss on the other, which is what makes the rest of the table
// readable at all.
const V2_CANDIDATES = [
  { verb: "GET", path: "/api/v3/zz-no-such-route-here", control: true },
  { verb: "GET", path: "/api/v3/movie" },
  { verb: "GET", path: "/api/v3/scene" },
  { verb: "GET", path: "/api/v3/performer" },
  { verb: "GET", path: "/api/v3/studio" },
  { verb: "GET", path: "/api/v3/exclusions" },
  { verb: "GET", path: "/api/v3/lookup/scene" },
  { verb: "GET", path: "/api/v3/series" },
  { verb: "GET", path: "/api/v3/episode" },
  { verb: "GET", path: "/api/v3/wanted/missing" },
  { verb: "GET", path: "/api/v3/wanted/cutoff" },
  { verb: "GET", path: "/api/v3/queue" },
  { verb: "GET", path: "/api/v3/history" },
  { verb: "GET", path: "/api/v3/importlistexclusion" },
  { verb: "GET", path: "/api/v3/seasonpass" },
  { verb: "PUT", path: "/api/v3/seasonpass", body: {} },
  {
    verb: "POST",
    path: "/api/v3/seasonpass",
    body: {},
    as: "POST /api/v3/seasonpass (empty body)",
  },
  {
    verb: "POST",
    path: "/api/v3/seasonpass",
    // The minimum input the handler accepts. An empty collection asks it to monitor nothing, so the
    // route is exercised and the instance is left as it was found.
    body: { series: [] },
    as: "POST /api/v3/seasonpass (empty series list)",
  },
  { verb: "GET", path: V2_LOOKUP_PATH },
  { verb: "GET", path: `${V2_LOOKUP_PATH}?term=${LOOKUP_TERM}` },
  { verb: "GET", path: "/api/v3/rootfolder" },
  { verb: "GET", path: "/api/v3/remotepathmapping" },
  { verb: "GET", path: "/api/v3/tag" },
  { verb: "GET", path: "/api/v3/qualityprofile" },
  { verb: "GET", path: "/api/v3/importlist" },
  { verb: "GET", path: "/api/v3/command" },
  { verb: "GET", path: "/api/v3/config/naming" },
  { verb: "GET", path: "/api/v3/system/task" },
];

/** A body's shape and, for a collection, its length — never the body. */
function bodyShape(response) {
  if (Array.isArray(response.json)) return `array[${response.json.length}]`;
  if (response.json !== null && typeof response.json === "object" && response.json !== undefined) {
    return "object";
  }
  return response.text === "" ? "empty" : "non-json";
}

const observeResponse = (response) => ({
  status: response.status,
  contentType: response.contentType,
  shape: bodyShape(response),
});

async function call(api, { verb, path, body }) {
  if (verb === "GET") return api.get(path);
  if (verb === "DELETE") return api.delete(path);
  if (verb === "POST") return api.post(path, body ?? {});
  if (verb === "PUT") return api.put(path, body ?? {});
  throw new Error(`row-02-capability-surfaces: no client method is wired for the verb "${verb}".`);
}

/**
 * Every host named by a URL-bearing field in `value`, sorted and deduplicated.
 *
 * A lookup result is a third party's record of a real person or business, so the HOST is the whole
 * of what this row wants from one and the rest is discarded here rather than at the record.
 */
function remoteHosts(value, depth = 0, found = new Set()) {
  if (depth > URL_SEARCH_DEPTH || value === null || typeof value !== "object") return found;
  for (const [key, held] of Object.entries(value)) {
    if (typeof held === "string" && /url/i.test(key)) {
      try {
        found.add(new URL(held).host);
      } catch {
        // Not a URL. A field named like one and holding something else is not this row's subject.
      }
    } else if (typeof held === "object") {
      remoteHosts(held, depth + 1, found);
    }
  }
  return found;
}

/** The hosts an instance's own configuration names, so the outbound dependency is observed. */
async function configuredHosts(instance) {
  const { output } = await instance.container.exec(["cat", "/config/config.xml"]);
  const hosts = new Set();
  for (const [, host] of output.matchAll(/https?:\/\/([A-Za-z0-9.-]+)/g)) hosts.add(host);
  return [...hosts].sort();
}

/** The top-level API areas a document's own path keys describe. */
function areasOf(paths) {
  const areas = new Set();
  for (const path of paths) {
    const area = /^\/api\/v3\/([^/{]+)/.exec(path)?.[1];
    if (area !== undefined) areas.add(area);
  }
  return [...areas].sort();
}

async function readV3Document(api) {
  const response = await api.get(OPENAPI_PATH);
  const document = response.json;
  const paths = Object.keys(document?.paths ?? {});
  const areas = areasOf(paths);
  return {
    document,
    summary: {
      path: OPENAPI_PATH,
      status: response.status,
      contentType: response.contentType,
      byteLength: response.text.length,
      openapi: document?.openapi ?? null,
      infoTitle: document?.info?.title ?? null,
      // The document's own version string, which is not the build's; row 1 carries that one.
      infoVersion: document?.info?.version ?? null,
      pathCount: paths.length,
      schemaCount: Object.keys(document?.components?.schemas ?? {}).length,
      areaCount: areas.length,
      // One string rather than a list: the areas are read as a set and a list of them would be the
      // longest array in this record for no gain.
      areas: areas.join(" "),
    },
  };
}

export const row = {
  id: "row-02-capability-surfaces",
  label: "What each Whisparr generation's API surface offers, and by which kind of evidence",
  requires: {
    // Whisparr containers join the Cove instance's own network, so a row asking for one asks for both.
    cove: true,
    whisparr: ["v3", "v2"],
    seedHistory: false,
    support: [],
    // The lookup probes leave the machine: both generations proxy them through the vendor's own
    // metadata service.
    network: true,
    live: false,
  },
  async run(ctx) {
    const v3Api = ctx.whisparr.apiFor("v3");
    const v2Api = ctx.whisparr.apiFor("v2");

    const { document, summary } = await readV3Document(v3Api);
    if (document === undefined) {
      throw new Error(
        `row-02-capability-surfaces: GET ${OPENAPI_PATH} answered ${summary.status} ${summary.contentType}, which did not parse as a document, so v3's surface cannot be derived from the build itself.`,
      );
    }

    const v3Routes = Object.fromEntries(
      V3_CANDIDATES.map(({ verb, path, unpaged }) => [
        `${verb} ${path}`,
        {
          declaredInDocument: document.paths?.[path]?.[verb.toLowerCase()] !== undefined,
          ...(unpaged === true
            ? { unpaged: true, fetched: false, reason: "its row count grows with the library" }
            : {}),
        },
      ]),
    );

    const v2Probes = {};
    for (const candidate of V2_CANDIDATES) {
      v2Probes[candidate.as ?? `${candidate.verb} ${candidate.path}`] = {
        // Carried as its own field as well as in the key, because the verb is what lets a later
        // reader tell an absence from a mis-probe.
        verb: candidate.verb,
        path: candidate.path,
        ...observeResponse(await call(v2Api, candidate)),
        ...(candidate.control === true ? { control: "a path this build cannot serve" } : {}),
      };
    }

    const v3Lookup = await v3Api.get(`${V3_LOOKUP_PATH}?term=${LOOKUP_TERM}`);
    const v2Lookup = await v2Api.get(`${V2_LOOKUP_PATH}?term=${LOOKUP_TERM}`);
    for (const [generation, response] of [
      ["v3", v3Lookup],
      ["v2", v2Lookup],
    ]) {
      if (!Array.isArray(response.json) || response.json.length === 0) {
        throw new Error(
          `row-02-capability-surfaces: ${generation}'s lookup for "${LOOKUP_TERM}" answered ${response.status} ${response.contentType} with ${bodyShape(response)}. Both generations proxy a lookup through the vendor's metadata service, so this row needs outbound network; an empty result here would be recorded as a missing capability.`,
        );
      }
    }

    return {
      method: {
        verb: "GET",
        path: `${OPENAPI_PATH} (v3) and each candidate below (v2)`,
        inputs: { lookupTerm: LOOKUP_TERM },
      },
      verdict: "surfaces-recorded",
      observed: {
        v3: {
          evidence: "derived from the document this build publishes about itself",
          caveat:
            "A route the document omits but the build serves, or lists but does not serve, would not be caught here.",
          document: summary,
          routes: v3Routes,
          sceneArea: {
            present: areasOf(Object.keys(document.paths ?? {})).includes("scene"),
            note: "There is no scene area on this generation. A scene is a movie carrying itemType 'scene'.",
          },
        },
        v2: {
          evidence: "hand-probed, one call per candidate, with the verb recorded beside the result",
          caveat:
            "This is a probe of the candidates below, not an enumeration. A route absent from this table is unknown, not absent.",
          document: `none; see the OpenAPI row for the candidate paths tried on this generation`,
          probes: v2Probes,
          verbSensitive: {
            path: "/api/v3/seasonpass",
            note: "Recorded under three verbs, because reading the GET result alone would record a route this build serves as absent.",
          },
          inputSensitive: [
            {
              path: V2_LOOKUP_PATH,
              note: "Recorded with and without a term, because reading the term-less result alone would record a capability this build has as missing.",
            },
            {
              path: "/api/v3/seasonpass",
              note: "Recorded under an empty body and under the minimum the handler accepts, because the empty one faults inside the handler and would be read as a broken route.",
            },
          ],
        },
        metadataSource: {
          v3: {
            path: V3_LOOKUP_PATH,
            status: v3Lookup.status,
            contentType: v3Lookup.contentType,
            resultCount: v3Lookup.json.length,
            hosts: [...remoteHosts(v3Lookup.json[0])].sort(),
          },
          v2: {
            path: V2_LOOKUP_PATH,
            status: v2Lookup.status,
            contentType: v2Lookup.contentType,
            resultCount: v2Lookup.json.length,
            hosts: [...remoteHosts(v2Lookup.json[0])].sort(),
          },
        },
        externalDependency: {
          note: "Both generations proxy a lookup through the vendor's own metadata service, so any fixture step built on a lookup depends on outbound network. Seeding the datastore directly does not.",
          configuredHosts: {
            v3: await configuredHosts(ctx.whisparr.v3),
            v2: await configuredHosts(ctx.whisparr.v2),
          },
        },
      },
    };
  },
};
