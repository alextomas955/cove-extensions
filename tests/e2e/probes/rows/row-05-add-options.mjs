// Whether leaving the search-suppressing field out of an entity add starts a search, per generation
// and per add path.
//
// An explicit false and an omission are different questions, and only the second decides whether a
// caller that simply does not think about the field is safe. So every observation here is an add
// actually performed, with the queue and the command roster read on both sides of it; a verdict is
// never carried across from a neighbouring add.
//
// v3 has TWO structurally different flags. A movie carries the suppressing flag inside `addOptions`;
// a performer and a studio each carry a top-level `searchOnAdd` and have no add-options schema at
// all, so one rule does not cover both paths.
//
// v2 publishes no contract, so its property names are established from what the server echoes back
// after an add: a property the model does not carry is dropped in deserialisation and is absent from
// the echo, which is what tells a real spelling from a plausible one.
//
// The instances carry no indexer and no download client, so a search that does start reaches
// nothing. That bounds what an absence of downloads would mean, which is why the roster and not the
// filesystem is the observation.
import { attemptUntil } from "../../lib/poll.mjs";

const QUEUE_PATH = "/api/v3/queue";
const COMMAND_PATH = "/api/v3/command";
const INDEXER_PATH = "/api/v3/indexer";
const DOWNLOAD_CLIENT_PATH = "/api/v3/downloadclient";
const QUALITY_PROFILE_PATH = "/api/v3/qualityprofile";
const OPENAPI_PATH = "/docs/v3/openapi.json";

// The declarations a later phase would code against. They are read from the document the build
// publishes rather than transcribed, so a spelling here cannot drift from the build it describes.
const DECLARED_SCHEMA = "AddMovieOptions";
const DECLARED_ENUMS = ["MonitorTypes", "AddMovieMethod", "ItemType"];

// A command whose name carries this is the observable form of a started search on both generations.
const SEARCH_COMMAND = /search/i;

const TERMINAL_COMMAND_STATUSES = new Set(["completed", "failed", "aborted", "cancelled"]);

// A suppression is an absence, so it is only as good as the window it was watched over. The dwell
// keeps a fast add from settling before the instance has issued whatever it is going to issue.
const SETTLE_TIMEOUT_MS = 30_000;
const SETTLE_DWELL_MS = 8_000;
const SETTLE_INTERVAL_MS = 1_000;

// The service a lookup is proxied to answers a burst with a temporary refusal, so a lookup is polled
// rather than read once.
const LOOKUP_TIMEOUT_MS = 90_000;
const LOOKUP_INTERVAL_MS = 5_000;

// A freshly started instance seeds its own default profiles after it starts answering.
const PROFILE_TIMEOUT_MS = 60_000;
const PROFILE_INTERVAL_MS = 2_000;

// The lookups reach the vendor's metadata service. The terms name businesses rather than people, and
// nothing a lookup returns about its subject is recorded: these results describe real people and
// real companies, and the row needs a subject to add, not a subject to publish.
const SCENE_TERM = "brazzers";
const STUDIO_TERM = "brazzers";
const PERFORMER_TERM = "smith";
const SERIES_TERM = "teen";

// Sent as true, one at a time, because a flag left false is indistinguishable from a flag the model
// does not have. `searchForMovie` is the v3 spelling, carried here as a cross-lineage control: v2 is
// a different lineage, and a probe that only tried names v2 might plausibly use could not tell a
// missing effect from a missing field.
const V2_CANDIDATE_SPELLINGS = [
  { name: "searchForMissingEpisodes", control: false },
  { name: "searchForCutoffUnmetEpisodes", control: false },
  { name: "searchForMovie", control: true },
];

const VERDICTS = "suppressed | search-started | unmeasured";

const joinNames = (names) => [...new Set(names)].sort().join(" ");

/**
 * What the instance could acquire with, which is what bounds every verdict below.
 *
 * Throws rather than recording a non-empty surface: a started search that reaches a configured
 * indexer would make this row's writes acquisitive, and the point of the fixture is that they
 * cannot be.
 */
async function acquisitionSurface(api, generation) {
  const indexers = await api.get(INDEXER_PATH);
  const downloadClients = await api.get(DOWNLOAD_CLIENT_PATH);
  const counts = {
    indexers: Array.isArray(indexers.json) ? indexers.json.length : null,
    downloadClients: Array.isArray(downloadClients.json) ? downloadClients.json.length : null,
  };
  if (counts.indexers !== 0 || counts.downloadClients !== 0) {
    throw new Error(
      `row-05-add-options: ${generation} reported ${JSON.stringify(counts)} for ${INDEXER_PATH} and ${DOWNLOAD_CLIENT_PATH}. This row performs adds that may start a search, and it only does so against an instance that can reach nothing.`,
    );
  }
  return {
    ...counts,
    note: "No indexer and no download client are configured, so a search this row started could find and fetch nothing. The command roster, not the filesystem, is therefore what reveals one.",
  };
}

/**
 * The add-options declaration and the enums it names, taken from the build's own document.
 *
 * The document is fetched for these few names and discarded; a probe that kept it would carry a
 * third party's whole contract inside a record meant to be read by a person.
 */
async function declaredAddOptions(api) {
  const response = await api.get(OPENAPI_PATH);
  const schemas = response.json?.components?.schemas;
  const declaration = schemas?.[DECLARED_SCHEMA];
  if (declaration === undefined) {
    throw new Error(
      `row-05-add-options: GET ${OPENAPI_PATH} answered ${response.status} ${response.contentType} and declared no ${DECLARED_SCHEMA}, so the property names cannot be read off the build.`,
    );
  }
  return {
    source: OPENAPI_PATH,
    schema: DECLARED_SCHEMA,
    properties: Object.fromEntries(
      Object.entries(declaration.properties ?? {}).map(([name, property]) => [
        name,
        property.type ?? property.$ref?.split("/").pop() ?? "unknown",
      ]),
    ),
    enums: Object.fromEntries(
      DECLARED_ENUMS.map((name) => [name, (schemas[name]?.enum ?? []).join(" ")]),
    ),
  };
}

const commandRoster = async (api) =>
  ((await api.get(COMMAND_PATH)).json ?? []).map((command) => ({
    id: command.id,
    name: command.name,
    status: command.status,
    result: command.result,
  }));

const queueTotal = async (api) => (await api.get(QUEUE_PATH)).json?.totalRecords ?? null;

/**
 * A profile an add can name, polled rather than read once.
 *
 * A freshly started instance answers this route before it has finished seeding its own defaults, so
 * a single read races the seed and reports an instance that has none.
 */
async function firstQualityProfileId(api, generation) {
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
      label: "row-05-add-options firstQualityProfileId",
    },
  );
  if (!settled) {
    throw new Error(
      `row-05-add-options: GET ${QUALITY_PROFILE_PATH} on ${generation} last answered ${note}, so no add can name a profile.`,
    );
  }
  return value;
}

/**
 * The lookup results a subject is taken from, refusing an empty answer.
 *
 * An unreachable metadata service and a capability the build lacks answer a lookup the same way, so
 * a short answer is raised rather than recorded. It is retried first because the service upstream of
 * the instance refuses a burst with a temporary status, and one of those is not the same fact as a
 * service that cannot be reached at all.
 */
async function lookupSubjects(api, path, generation, wanted) {
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
      label: "row-05-add-options lookupSubjects",
    },
  );
  if (!settled) {
    throw new Error(
      `row-05-add-options: GET ${path} on ${generation} last answered ${note}, and this row needs ${wanted}. Both generations proxy a lookup through the vendor's metadata service, so a short answer here is an unreachable or refusing service rather than a missing capability.`,
    );
  }
  return value;
}

/**
 * Waits out whatever an add set going, and reports the commands it started.
 *
 * Settles early on a search-shaped command, because that answer cannot be improved by waiting. An
 * absence settles only once every command the add started has reached a terminal status AND the
 * dwell has passed, so the window the absence holds over is stated rather than assumed.
 */
async function settleAfterAdd(api, idsBefore) {
  const startedAt = Date.now();
  const { settled, value } = await attemptUntil(
    async (_signal, note) => {
      const roster = await commandRoster(api);
      const fresh = roster.filter((command) => !idsBefore.has(command.id));
      const searching = fresh.filter((command) => SEARCH_COMMAND.test(command.name));
      note(`${fresh.length} new, ${searching.length} search-shaped`);
      if (searching.length > 0) return { value: { fresh, reason: "search-command-observed" } };
      const allTerminal = fresh.every((command) =>
        TERMINAL_COMMAND_STATUSES.has(String(command.status).toLowerCase()),
      );
      const dwelt = Date.now() - startedAt >= SETTLE_DWELL_MS;
      return fresh.length > 0 && allTerminal && dwelt
        ? { value: { fresh, reason: "new-commands-reached-a-terminal-status" } }
        : null;
    },
    {
      timeoutMs: SETTLE_TIMEOUT_MS,
      intervalMs: SETTLE_INTERVAL_MS,
      label: "row-05-add-options settleAfterAdd",
    },
  );
  if (settled) return { ...value, boundedByMs: Date.now() - startedAt };
  const roster = await commandRoster(api);
  return {
    fresh: roster.filter((command) => !idsBefore.has(command.id)),
    reason: "settle-window-elapsed",
    boundedByMs: SETTLE_TIMEOUT_MS,
  };
}

/**
 * One add, with the queue and the command roster read on both sides of it.
 *
 * A read-back of the created entity is what separates a suppression from an add the instance had
 * not processed yet: without it, "no search command appeared" and "nothing happened at all" are the
 * same observation.
 *
 * @param {{api: object, path: string, body: object, request: object, echo: (created: object) => unknown}} options
 */
async function observeAdd({ api, path, body, request, echo }) {
  const rosterBefore = await commandRoster(api);
  const idsBefore = new Set(rosterBefore.map((command) => command.id));
  const queueBefore = await queueTotal(api);

  const created = await api.post(path, body);
  if (created.status !== 201) {
    return {
      request,
      add: { path, status: created.status, contentType: created.contentType },
      verdict: "unmeasured",
      why: `The add was refused, so nothing about the omission default follows from it. The refusal body's first entry named ${created.json?.[0]?.propertyName ?? "no property"}.`,
    };
  }

  const settled = await settleAfterAdd(api, idsBefore);
  const queueAfter = await queueTotal(api);
  const rosterAfter = await commandRoster(api);
  const searchShaped = settled.fresh.filter((command) => SEARCH_COMMAND.test(command.name));
  const readBack = await api.get(`${path}/${created.json.id}`);

  const verdict =
    searchShaped.length > 0
      ? "search-started"
      : readBack.status === 200
        ? "suppressed"
        : "unmeasured";

  return {
    request,
    add: { path, status: created.status, contentType: created.contentType },
    // What the server made of the request, which is where its own default for the field is visible.
    echo: echo(created.json),
    entityReadsBack: readBack.status === 200,
    queue: { totalRecordsBefore: queueBefore, totalRecordsAfter: queueAfter },
    commands: {
      countBefore: rosterBefore.length,
      countAfter: rosterAfter.length,
      newCount: settled.fresh.length,
      newNames: joinNames(settled.fresh.map((command) => command.name)),
      newOutcomes: Object.fromEntries(
        settled.fresh.map((command) => [command.name, `${command.status}/${command.result}`]),
      ),
      searchShapedNewNames: joinNames(searchShaped.map((command) => command.name)),
      matcher: 'a new command whose name contains "search", case-insensitively',
      settledBecause: settled.reason,
      boundedByMs: settled.boundedByMs,
    },
    verdict,
    ...(verdict === "unmeasured"
      ? {
          why: "The add was accepted but the entity did not read back, so no search command having appeared says nothing about the default.",
        }
      : {}),
  };
}

async function probeV3(ctx) {
  const api = ctx.whisparr.apiFor("v3");
  const rootFolderPath = ctx.whisparr.v3.rootFolder;
  const qualityProfileId = await firstQualityProfileId(api, "v3");
  const acquisition = await acquisitionSurface(api, "v3");
  const declared = await declaredAddOptions(api);

  const scenePath = `/api/v3/lookup/scene?term=${SCENE_TERM}`;
  const scenes = await lookupSubjects(api, scenePath, "v3", 3);
  const movieBase = (index) => ({
    ...scenes[index].movie,
    qualityProfileId,
    rootFolderPath,
    // Held true across all three movie adds. Monitoring is the state a caller acting on a Cove event
    // would realistically ask for, and holding it constant is what leaves the add-options handling
    // as the only thing that differs between them.
    monitored: true,
  });
  const movieEcho = (created) => created?.addOptions ?? null;
  const movieSubject = (index) => ({
    source: scenePath,
    index,
    // A lookup result describes a real production. Nothing identifying it is recorded.
    identityRecorded: false,
    monitored: true,
  });

  const explicitFalse = await observeAdd({
    api,
    path: "/api/v3/movie",
    body: { ...movieBase(0), addOptions: { searchForMovie: false } },
    request: { ...movieSubject(0), addOptions: "present", searchForMovie: "false" },
    echo: movieEcho,
  });

  const fieldOmitted = await observeAdd({
    api,
    path: "/api/v3/movie",
    body: { ...movieBase(1), addOptions: {} },
    request: { ...movieSubject(1), addOptions: "present", searchForMovie: "omitted" },
    echo: movieEcho,
  });

  const optionsOmitted = await observeAdd({
    api,
    path: "/api/v3/movie",
    body: movieBase(2),
    request: { ...movieSubject(2), addOptions: "omitted", searchForMovie: "omitted" },
    echo: movieEcho,
  });

  // A studio and a performer carry the whole of their catalogue's movies when they are monitored, so
  // both are added unmonitored: the flag under test is the top-level one, and monitoring is not
  // needed to observe it.
  const studioPath = `/api/v3/lookup/studio?term=${STUDIO_TERM}`;
  const studios = await lookupSubjects(api, studioPath, "v3", 1);
  const studioBody = {
    ...studios[0].studio,
    qualityProfileId,
    rootFolderPath,
    monitored: false,
    moviesMonitored: false,
  };
  delete studioBody.searchOnAdd;
  const studio = await observeAdd({
    api,
    path: "/api/v3/studio",
    body: studioBody,
    request: {
      source: studioPath,
      index: 0,
      identityRecorded: false,
      searchOnAdd: "omitted",
      monitored: false,
      moviesMonitored: false,
    },
    echo: (created) => ({ searchOnAdd: created?.searchOnAdd ?? null }),
  });

  const performerPath = `/api/v3/lookup/performer?term=${PERFORMER_TERM}`;
  const performers = await lookupSubjects(api, performerPath, "v3", 1);
  const performerBody = {
    ...performers[0].performer,
    qualityProfileId,
    rootFolderPath,
    monitored: false,
    moviesMonitored: false,
  };
  delete performerBody.searchOnAdd;
  const performer = await observeAdd({
    api,
    path: "/api/v3/performer",
    body: performerBody,
    request: {
      source: performerPath,
      index: 0,
      identityRecorded: false,
      searchOnAdd: "omitted",
      monitored: false,
      moviesMonitored: false,
    },
    echo: (created) => ({ searchOnAdd: created?.searchOnAdd ?? null }),
  });

  return {
    acquisition,
    declared,
    twoPaths:
      "A movie add takes the flag inside addOptions; a performer or a studio add takes a top-level searchOnAdd and has no add-options schema. A rule stated for one leaves the other unguarded.",
    movie: {
      flag: "addOptions.searchForMovie",
      lineageWarning:
        "addOptions also declares ignoreEpisodesWithFiles and ignoreEpisodesWithoutFiles. They name a season-based entity this resource does not have, so their meaning here is not established by their names.",
      explicitFalse,
      fieldOmitted,
      optionsOmitted,
    },
    studio: { flag: "searchOnAdd", searchOnAddOmitted: studio },
    performer: { flag: "searchOnAdd", searchOnAddOmitted: performer },
  };
}

async function probeV2(ctx) {
  const api = ctx.whisparr.apiFor("v2");
  const rootFolderPath = ctx.whisparr.v2.rootFolder;
  const qualityProfileId = await firstQualityProfileId(api, "v2");
  const acquisition = await acquisitionSurface(api, "v2");

  const seriesPath = `/api/v3/series/lookup?term=${SERIES_TERM}`;
  const wanted = 1 + V2_CANDIDATE_SPELLINGS.length;
  const subjects = await lookupSubjects(api, seriesPath, "v2", wanted);
  const base = (index) => ({
    ...subjects[index],
    qualityProfileId,
    rootFolderPath,
    // Unmonitored throughout: a monitored series is refreshed into as many rows as its catalogue
    // has, and this row's subject is the add-options handling rather than the catalogue.
    monitored: false,
  });
  const echo = (created) => created?.addOptions ?? null;
  const subject = (index) => ({
    source: seriesPath,
    index,
    identityRecorded: false,
    monitored: false,
  });

  const optionsOmitted = await observeAdd({
    api,
    path: "/api/v3/series",
    body: base(0),
    request: { ...subject(0), addOptions: "omitted" },
    echo,
  });

  const spellings = {};
  const echoedProperties = new Set();
  for (const [offset, candidate] of V2_CANDIDATE_SPELLINGS.entries()) {
    const observation = await observeAdd({
      api,
      path: "/api/v3/series",
      body: { ...base(offset + 1), addOptions: { [candidate.name]: true } },
      request: {
        ...subject(offset + 1),
        addOptions: "present",
        [candidate.name]: "true",
        ...(candidate.control
          ? {
              control:
                "the other generation's spelling, sent to fix what a name the model lacks looks like",
            }
          : {}),
      },
      echo,
    });
    for (const property of Object.keys(observation.echo ?? {})) echoedProperties.add(property);
    const carried = Object.hasOwn(observation.echo ?? {}, candidate.name);
    const observableDifference = observation.verdict === "search-started";
    spellings[candidate.name] = {
      ...observation,
      echoCarriesTheProperty: carried,
      observableDifference,
      // Confirmed means observed doing something, never merely accepted: an unknown property is
      // dropped on the way in and the add answers exactly as a known one does.
      spellingStatus: observableDifference ? "confirmed" : "unconfirmed",
      ...(candidate.control ? { isControl: true } : {}),
    };
  }

  return {
    acquisition,
    evidence:
      "This generation publishes no contract, so the property names below are the ones the server echoed back after an add, and a spelling is called confirmed only where sending it changed what the instance did.",
    addOptionsPropertiesEchoed: [...echoedProperties].sort().join(" "),
    optionsOmitted,
    spellings,
  };
}

export const row = {
  id: "row-05-add-options",
  label: "What an omitted search-suppressing field does on each generation, and on each add path",
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

    const paths = {
      "v3 movie, addOptions.searchForMovie explicitly false": v3.movie.explicitFalse.verdict,
      "v3 movie, addOptions present and searchForMovie omitted": v3.movie.fieldOmitted.verdict,
      "v3 movie, addOptions omitted": v3.movie.optionsOmitted.verdict,
      "v3 studio, searchOnAdd omitted": v3.studio.searchOnAddOmitted.verdict,
      "v3 performer, searchOnAdd omitted": v3.performer.searchOnAddOmitted.verdict,
      "v2 series, addOptions omitted": v2.optionsOmitted.verdict,
      ...Object.fromEntries(
        Object.entries(v2.spellings).map(([name, observation]) => [
          `v2 series, addOptions.${name} true`,
          observation.verdict,
        ]),
      ),
    };
    const anyUnmeasured = Object.values(paths).includes("unmeasured");

    return {
      method: {
        verb: "POST",
        path: "/api/v3/movie, /api/v3/studio, /api/v3/performer (v3) and /api/v3/series (v2), each read back through /api/v3/queue and /api/v3/command",
        inputs: {
          sceneTerm: SCENE_TERM,
          studioTerm: STUDIO_TERM,
          performerTerm: PERFORMER_TERM,
          seriesTerm: SERIES_TERM,
        },
      },
      verdict: anyUnmeasured ? "omission-defaults-partly-measured" : "omission-defaults-measured",
      observed: {
        verdictVocabulary: VERDICTS,
        perPathVerdicts: paths,
        derivation:
          "Each verdict comes from that add's own before-and-after read. None is carried across from another add, and an explicit false says nothing about an omission.",
        v3,
        v2,
      },
    };
  },
};
