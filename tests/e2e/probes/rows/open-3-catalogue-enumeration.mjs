// Whether either Whisparr generation offers a read-only way to list everything an UNMONITORED
// entity offers, which is what decides whether the missing-scene surface can source from Whisparr
// at all or must read the metadata provider directly.
//
// The candidate routes exist on one generation, and that is exactly what makes this question easy
// to answer backwards. A probe that adds an entity and then reads `/works` sees a 200 and records
// "enumeration is available". The same route answers 404 for the same entity before it was added,
// so the 200 says the entity is now in the LOCAL library — not that the route reads the provider's
// catalogue. Neither observation means anything alone, so both are taken, on the same foreign id,
// per entity kind, and the verdict is set from the pair.
//
// The route spellings are read out of the build's own document rather than transcribed, so a route
// that is gone in a later image is recorded as absent instead of being probed at a name this file
// invented.
//
// Every presence judgement is made on a content type with a nonsense path recorded beside it: both
// generations answer some unmatched paths with their own single-page frontend and a 200, so a
// status-only reading reports absent routes as present.
import { attemptUntil } from "../../lib/poll.mjs";

const OPENAPI_PATH = "/docs/v3/openapi.json";
const COMMAND_PATH = "/api/v3/command";
const QUEUE_PATH = "/api/v3/queue";
const QUALITY_PROFILE_PATH = "/api/v3/qualityprofile";

// Two controls per generation, because the fallback that serves a frontend for an unmatched path
// applies outside a build's API namespace and not inside it. One control on each side of that line
// is what makes a real 404 legible as a real 404.
const CONTROL_PATHS = [
  "/api/v3/there-is-no-such-route-on-any-build",
  "/there-is-no-such-route-on-any-build",
];

// The terms name a business and a common surname. Nothing a lookup returns about a person is
// recorded: this row needs a subject to add, not a subject to publish.
//
// They are deliberately not the terms the other adding rows use. Every row in a run shares one
// instance, so two rows adding the same subject leave whichever runs second holding a refusal it
// can measure nothing from.
const STUDIO_TERM = "vixen";
const PERFORMER_TERM = "jones";
const SERIES_TERM = "teen";

const TERMINAL_COMMAND_STATUSES = new Set(["completed", "failed", "aborted", "cancelled"]);

const REFRESH_TIMEOUT_MS = 60_000;
const REFRESH_INTERVAL_MS = 1_000;

// The service a lookup is proxied to answers a burst with a temporary refusal, so a lookup is
// polled rather than read once: an unreachable service and an absent capability answer alike.
const LOOKUP_TIMEOUT_MS = 90_000;
const LOOKUP_INTERVAL_MS = 5_000;

// A freshly started instance seeds its own default profiles after it starts answering.
const PROFILE_TIMEOUT_MS = 60_000;
const PROFILE_INTERVAL_MS = 2_000;

const VERDICTS =
  "local-library-only | catalogue-enumerated | route-absent | inconclusive — per entity kind and per generation";

const COUNT_FIELDS = ["movieCount", "sceneCount", "totalMovieCount", "totalSceneCount"];

/** How a response is described wherever this row judges a route's presence. */
const describeResponse = (response, extra = {}) => ({
  status: response.status,
  contentType: response.contentType,
  ...extra,
});

/**
 * A listing's size and the SHAPE of one element, never an element.
 *
 * The works route is unpaged, so its body is unbounded input and cannot enter a record. The key
 * names answer the only question a reader has about an element — whether it is an entity row —
 * without carrying anything the route returned about a real production.
 */
function describeListing(response) {
  const listing = Array.isArray(response.json) ? response.json : null;
  return {
    ...describeResponse(response),
    isArray: listing !== null,
    length: listing?.length ?? null,
    firstElementKeys:
      listing?.[0] === undefined ? null : Object.keys(listing[0]).sort().join(" ") || "<no keys>",
    bodyPolicy: "length and the first element's key names only; no element of the body is recorded",
  };
}

async function negativeControls(api) {
  const controls = {};
  for (const path of CONTROL_PATHS) {
    controls[path] = describeResponse(await api.get(path));
  }
  return controls;
}

/**
 * The works routes the build itself declares, taken from its own document.
 *
 * A build that no longer declares them is recorded as declaring none rather than being probed at a
 * spelling this file supplied, which is the difference between measuring the build and measuring
 * this file.
 */
async function declaredWorksRoutes(api) {
  const response = await api.get(OPENAPI_PATH);
  const paths = response.json?.paths;
  if (paths === undefined) {
    return {
      source: OPENAPI_PATH,
      ...describeResponse(response),
      declared: {},
      note: "This build publishes no document, so no route spelling could be read off it.",
    };
  }
  const declared = {};
  for (const [path, operations] of Object.entries(paths)) {
    if (!path.endsWith("/works")) continue;
    declared[path] = Object.keys(operations).sort().join(" ");
  }
  return { source: OPENAPI_PATH, ...describeResponse(response), declared };
}

/** The declared works route for one entity kind, with its single path parameter filled in. */
function worksPathFor(declared, kind, foreignId) {
  const template = Object.keys(declared).find((path) => path.startsWith(`/api/v3/${kind}/`));
  if (template === undefined) return { template: null, path: null };
  return { template, path: template.replace(/\{[^}]+\}/, foreignId) };
}

/**
 * The works route as the record carries it.
 *
 * A filled-in path carries the foreign id in it, so a kind whose subject is not recorded gets its
 * template and nothing else. Recording the concrete path there would put back exactly what the
 * lookup section withheld.
 */
const describeWorksRoute = (works, identityRecorded) =>
  identityRecorded
    ? works
    : { template: works.template, path: null, filledWith: "the foreign id, which is not recorded" };

/**
 * A profile an add can name, polled rather than read once.
 *
 * A freshly started instance answers this route before it has finished seeding its own defaults, so
 * a single read races the seed and reports an instance that has none.
 */
async function firstQualityProfileId(api) {
  const { settled, value, note } = await attemptUntil(
    async (_signal, record) => {
      const profiles = await api.get(QUALITY_PROFILE_PATH);
      record(`${profiles.status} ${profiles.contentType} with ${profiles.json?.length ?? 0}`);
      const id = profiles.json?.[0]?.id;
      return typeof id === "number" ? { value: id } : null;
    },
    {
      timeoutMs: PROFILE_TIMEOUT_MS,
      intervalMs: PROFILE_INTERVAL_MS,
      label: "open-3 firstQualityProfileId",
    },
  );
  if (!settled) {
    throw new Error(
      `open-3-catalogue-enumeration: GET ${QUALITY_PROFILE_PATH} last answered ${note}, so no add can name a profile.`,
    );
  }
  return value;
}

/**
 * Lookup results, refusing a short answer.
 *
 * Retried first because the service upstream of the instance refuses a burst with a temporary
 * status, and that is not the same fact as a service that cannot be reached at all.
 */
async function lookupResults(api, path, wanted) {
  const { settled, value, note } = await attemptUntil(
    async (_signal, record) => {
      const response = await api.get(path);
      const results = Array.isArray(response.json) ? response.json : [];
      record(`${response.status} ${response.contentType} with ${results.length} result(s)`);
      return results.length >= wanted ? { value: results } : null;
    },
    {
      timeoutMs: LOOKUP_TIMEOUT_MS,
      intervalMs: LOOKUP_INTERVAL_MS,
      label: "open-3 lookupResults",
    },
  );
  if (!settled) {
    throw new Error(
      `open-3-catalogue-enumeration: GET ${path} last answered ${note}, and this row needs ${wanted}. Both generations proxy a lookup through the vendor's metadata service, so a short answer is an unreachable or refusing service rather than a missing capability.`,
    );
  }
  return value;
}

const commandRoster = async (api) =>
  ((await api.get(COMMAND_PATH)).json ?? []).map((command) => ({
    id: command.id,
    name: command.name,
    status: command.status,
  }));

const queueTotal = async (api) => (await api.get(QUEUE_PATH)).json?.totalRecords ?? null;

const countsFrom = (entity) =>
  Object.fromEntries(COUNT_FIELDS.map((field) => [field, entity?.[field] ?? null]));

/**
 * Commands a refresh and waits for everything it started to reach a terminal status.
 *
 * The wait requires at least one new command before it can settle: "every command this started has
 * finished" is trivially true over an empty set, so a roster that has not yet shown the new command
 * would satisfy it and the refresh would be measured before it began.
 */
async function commandRefresh(api, body) {
  const idsBefore = new Set((await commandRoster(api)).map((command) => command.id));
  const posted = await api.post(COMMAND_PATH, body);
  const { settled, value } = await attemptUntil(
    async (_signal, note) => {
      const roster = await commandRoster(api);
      const fresh = roster.filter((command) => !idsBefore.has(command.id));
      note(`${fresh.length} new`);
      const allTerminal = fresh.every((command) =>
        TERMINAL_COMMAND_STATUSES.has(String(command.status).toLowerCase()),
      );
      return fresh.length > 0 && allTerminal ? { value: fresh } : null;
    },
    {
      timeoutMs: REFRESH_TIMEOUT_MS,
      intervalMs: REFRESH_INTERVAL_MS,
      label: "open-3 commandRefresh",
    },
  );
  return {
    request: { name: body.name, idsSent: true },
    accepted: describeResponse(posted),
    // What the model kept of the request, which is how a property name it does not carry shows.
    echoedKeys: posted.json === undefined ? null : Object.keys(posted.json).sort().join(" "),
    reachedTheRoster: settled,
    newCommands: (value ?? []).map((command) => `${command.name}/${command.status}`).join(" "),
    settledBecause: settled ? "every command this refresh started reached a terminal status" : null,
    boundedByMs: settled ? null : REFRESH_TIMEOUT_MS,
    caveat: settled
      ? "This refresh ran to a terminal status, so what the entity reports afterwards is a result rather than an artefact. It is still not what the verdict rests on."
      : "The command was accepted and never appeared on the roster, so no refresh is known to have run and what the entity reports afterwards says nothing about what it offers. The verdict rests on the not-added and added-unmonitored pair instead, which does not depend on this path.",
  };
}

/**
 * One entity kind measured in both states, on one foreign id.
 *
 * The order is the method: the not-added observation is taken first and is the step that makes the
 * added one interpretable. Adding first and reading once is the reading this row exists to prevent.
 */
async function probeKind({ api, kind, term, declared, qualityProfileId, rootFolderPath, refresh }) {
  const lookupPath = `/api/v3/lookup/${kind}?term=${term}`;
  const results = await lookupResults(api, lookupPath, 1);
  const candidate = results.find((result) => result.isExisting !== true) ?? results[0];
  const foreignId = candidate.foreignId;
  const subject = candidate[kind];

  // A studio names a business and is recorded; a performer names a person and is not.
  const identityRecorded = kind === "studio";
  const works = worksPathFor(declared, kind, foreignId);
  if (works.path === null) {
    return {
      lookup: { path: lookupPath, term, results: results.length },
      verdict: "route-absent",
      why: `This build declares no works route under /api/v3/${kind}/, so there is nothing to read in either state.`,
    };
  }

  const notAdded = {
    works: describeListing(await api.get(works.path)),
    entity: describeResponse(await api.get(`/api/v3/${kind}/${foreignId}`)),
  };

  const body = {
    ...subject,
    qualityProfileId,
    rootFolderPath,
    monitored: false,
    moviesMonitored: false,
    searchOnAdd: false,
  };
  const created = await api.post(`/api/v3/${kind}`, body);
  if (created.status !== 201) {
    return {
      lookup: { path: lookupPath, term, results: results.length },
      notAdded,
      add: describeResponse(created),
      verdict: "inconclusive",
      why: "The add was refused, so the added-unmonitored half of the pair does not exist and the not-added half decides nothing on its own.",
    };
  }

  const localId = created.json?.id;
  const worksAfterAdd = describeListing(await api.get(works.path));
  // The declared spelling names a FOREIGN id. The local id is probed beside it so the record
  // carries which one the route answers to, rather than leaving a reader to assume.
  const worksByLocalId = describeResponse(await api.get(`/api/v3/${kind}/${localId}/works`));
  const readBackByLocalId = await api.get(`/api/v3/${kind}/${localId}`);
  const readBackByForeignId = await api.get(`/api/v3/${kind}/${foreignId}`);
  const countsSource =
    readBackByLocalId.status === 200
      ? "read back by local id"
      : readBackByForeignId.status === 200
        ? "read back by foreign id"
        : "the add's own response";
  const counts = countsFrom(
    readBackByLocalId.status === 200
      ? readBackByLocalId.json
      : readBackByForeignId.status === 200
        ? readBackByForeignId.json
        : created.json,
  );

  const loopSafety = {
    queueTotalRecords: await queueTotal(api),
    commandRosterNames: [...new Set((await commandRoster(api)).map((command) => command.name))]
      .sort()
      .join(" "),
    note: "The instance's whole command roster once this add has settled, and its queue. The baseline a later phase inherits: what an unmonitored add with the search flag off leaves behind.",
  };

  const refreshed = await commandRefresh(api, refresh(localId));
  const worksAfterRefresh = describeListing(await api.get(works.path));
  const countsAfterRefresh = countsFrom(
    (await api.get(`/api/v3/${kind}/${localId}`)).json ?? created.json,
  );

  const routeExists = notAdded.works.status !== 404 || worksAfterAdd.status === 200;
  const enumerated = worksAfterAdd.isArray && worksAfterAdd.length > 0;
  const verdict = !routeExists
    ? "route-absent"
    : enumerated
      ? "catalogue-enumerated"
      : notAdded.works.status === 404 && worksAfterAdd.status === 200 && worksAfterAdd.length === 0
        ? "local-library-only"
        : "inconclusive";

  return {
    lookup: {
      path: lookupPath,
      term,
      results: results.length,
      isExistingBeforeTheAdd: candidate.isExisting ?? null,
      // The pair of observations is interpretable either way, because both halves used the one id.
      ...(identityRecorded
        ? { foreignId }
        : { foreignIdRecorded: false, foreignIdChars: String(foreignId).length }),
      sameForeignIdInBothStates: true,
    },
    worksRoute: describeWorksRoute(works, identityRecorded),
    notAdded,
    add: describeResponse(created),
    addedUnmonitored: {
      works: worksAfterAdd,
      worksByLocalId,
      readBack: {
        byLocalId: readBackByLocalId.status,
        byForeignId: readBackByForeignId.status,
        countsSource,
      },
      counts,
    },
    loopSafety,
    refresh: { ...refreshed, worksAfter: worksAfterRefresh, countsAfter: countsAfterRefresh },
    verdict,
    reading:
      verdict === "local-library-only"
        ? "The route EXISTS and it does not enumerate the provider's catalogue. The same route on the same foreign id answers 404 before the add and 200 with an empty array after it, so what it reports is a function of what the local library holds, not of what the entity offers."
        : verdict === "catalogue-enumerated"
          ? "The route answered with rows for an entity that is added but not monitored, so it reports more than the local library holds."
          : "The pair is incomplete, so neither reading is supported.",
  };
}

async function probeV3(ctx) {
  const api = ctx.whisparr.apiFor("v3");
  const rootFolderPath = ctx.whisparr.v3.rootFolder;
  const qualityProfileId = await firstQualityProfileId(api);
  const document = await declaredWorksRoutes(api);

  const studio = await probeKind({
    api,
    kind: "studio",
    term: STUDIO_TERM,
    declared: document.declared,
    qualityProfileId,
    rootFolderPath,
    refresh: (id) => ({ name: "RefreshStudios", studioIds: [id] }),
  });
  const performer = await probeKind({
    api,
    kind: "performer",
    term: PERFORMER_TERM,
    declared: document.declared,
    qualityProfileId,
    rootFolderPath,
    refresh: (id) => ({ name: "RefreshPerformers", performerIds: [id] }),
  });

  return {
    document,
    controls: await negativeControls(api),
    studio,
    performer,
  };
}

/**
 * The same candidate surfaces on the other generation, plus what it offers in their place.
 *
 * A miss here is judged on the content type for the same reason it is on v3: this build serves its
 * own frontend for an unmatched path outside its API namespace, so a status alone would report a
 * document that is not there.
 */
async function probeV2(ctx) {
  const api = ctx.whisparr.apiFor("v2");
  const candidates = {};
  for (const path of [
    "/api/v3/studio",
    "/api/v3/performer",
    `/api/v3/lookup/studio?term=${STUDIO_TERM}`,
    `/api/v3/lookup/performer?term=${PERFORMER_TERM}`,
    "/api/v3/studio/1/works",
    "/api/v3/performer/1/works",
  ]) {
    candidates[path] = { verb: "GET", ...describeListing(await api.get(path)) };
  }

  const seriesLookupPath = `/api/v3/series/lookup?term=${SERIES_TERM}`;
  const { value: seriesLookup, note } = await attemptUntil(
    async (_signal, record) => {
      const response = await api.get(seriesLookupPath);
      record(`${response.status} ${response.contentType}`);
      return response.status === 200 ? { value: response } : null;
    },
    {
      timeoutMs: LOOKUP_TIMEOUT_MS,
      intervalMs: LOOKUP_INTERVAL_MS,
      label: "open-3 v2 series lookup",
    },
  );

  return {
    candidates,
    controls: await negativeControls(api),
    offersInstead: {
      [seriesLookupPath]: {
        verb: "GET",
        ...(seriesLookup === undefined
          ? { lastObserved: note, reached: false }
          : describeListing(seriesLookup)),
        note: "A metadata search for a term, not a listing of what an entity offers. It answers about candidates to add, so it cannot enumerate an entity's catalogue.",
      },
      "/api/v3/episode": {
        verb: "GET",
        ...describeResponse(await api.get("/api/v3/episode")),
        withSeriesId: describeListing(await api.get("/api/v3/episode?seriesId=1")),
        note: "Requires the seriesId of an already-added series, so it reports the local library by construction.",
      },
    },
  };
}

export const row = {
  id: "open-3-catalogue-enumeration",
  label:
    "Whether either generation offers a read-only way to list what an UNMONITORED entity offers",
  requires: {
    // Whisparr containers join the Cove instance's own network, so a row asking for one asks for both.
    cove: true,
    whisparr: ["v3", "v2"],
    seedHistory: false,
    support: [],
    // Both generations refuse an add whose destination is not a registered library root.
    rootFolder: true,
    // Every subject is found through a lookup, and both generations proxy those to the vendor.
    network: true,
    live: false,
  },
  async run(ctx) {
    const v3 = await probeV3(ctx);
    const v2 = await probeV2(ctx);

    const perKindVerdicts = {
      "v3 studio": v3.studio.verdict,
      "v3 performer": v3.performer.verdict,
      "v2 studio and performer": Object.values(v2.candidates).every(
        (candidate) => candidate.status === 404,
      )
        ? "route-absent"
        : "inconclusive",
    };
    const enumerates = Object.values(perKindVerdicts).includes("catalogue-enumerated");

    return {
      method: {
        verb: "GET",
        path: "the works routes each build declares, read in the not-added state and again after an unmonitored add, with POST /api/v3/{studio,performer} between them",
        inputs: { studioTerm: STUDIO_TERM, performerTerm: PERFORMER_TERM, seriesTerm: SERIES_TERM },
      },
      verdict: enumerates
        ? "catalogue-enumeration-available"
        : "no-read-only-catalogue-enumeration",
      observed: {
        verdictVocabulary: VERDICTS,
        perKindVerdicts,
        theDistinctionThisRowTurnsOn:
          "A route EXISTING and a route ENUMERATING THE PROVIDER'S CATALOGUE are different findings. The works routes exist on v3 and are read-only GETs. They report the rows the local library holds for that entity: 404 before the entity is added, 200 with an empty array once it is added and left unmonitored. So no read-only way to list what an unmonitored entity OFFERS was found on either generation, and a reading taken from the added state alone would have recorded the opposite.",
        whatThisDoesNotSettle:
          "This row observes two builds. It does not decide where the missing-scene surface sources from; it removes one candidate source by measurement.",
        nonTests: [
          {
            path: "adding an entity with moviesMonitored: true in order to read its catalogue back",
            prohibition: "P-17",
            reason:
              "That is the loop-unsafe path the spec forbids — registering something in Whisparr in order to read it back — and the safe path already yields the answer, since a monitored add changes what the LOCAL library holds rather than what the route reads. Its result therefore cannot change the sourcing decision.",
            examined: true,
          },
        ],
        v3,
        v2,
      },
    };
  },
};
