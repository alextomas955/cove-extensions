// What a generation calls a thing, and how a spec reads that generation's state off the instance.
//
// WHAT THIS IS FOR. A scenario written once and collected once per generation has to reach the
// handful of places the two differ without naming either of them in its body. Those places are
// spellings and read routes, not actions, so they all live here.
//
// WHAT IT NEVER DOES. It never performs the action under test. Every member below either names a
// constant or reads the instance's own rows; a member that called one of this extension's routes
// would make a spec's evidence come from the thing the spec is about.
//
// WHY EVERY READER REFUSES. A refused listing and an instance holding nothing are different facts.
// Read as empty data the first reads as the second, and a scenario then reports that the walk
// imported nothing when what really happened is that it never saw a page.
import { STASHDB_ENDPOINT, THEPORNDB_ENDPOINT } from "./contract.mjs";
import { deliveredRemoteId } from "./steps.mjs";

// What both generations render an import as. Transcribed rather than read off the instance: the walk
// selects on this string, so a reader deriving it would select on whatever the walk selects on. The
// integer behind it is the instance's own and differs between the two, which is what
// historyEventTypeId reads back.
const IMPORTED_EVENT_TYPE = "downloadFolderImported";

async function listRows(api, route) {
  const answered = await api.get(route);
  if (!Array.isArray(answered.json)) {
    throw new Error(
      `generation adapter: ${route} answered ${String(answered.status)} with no list: ${String(answered.text).slice(0, 300)}`,
    );
  }
  return answered.json;
}

async function historyPage(api, pageSize) {
  const route = `/api/v3/history?page=1&pageSize=${String(pageSize)}&sortKey=date&sortDirection=descending`;
  const answered = await api.get(route);
  if (!Array.isArray(answered.json?.records) || !Number.isInteger(answered.json?.totalRecords)) {
    throw new Error(
      `generation adapter: ${route} answered ${String(answered.status)} with no history page: ${String(answered.text).slice(0, 300)}`,
    );
  }
  return answered.json;
}

function oneIdentifier(declared, generation, where) {
  if (declared.length !== 1) {
    throw new Error(
      `generation adapter: ${where} on ${generation} declares ${String(declared.length)} scene identifier(s), so there is nothing single for a channel to agree on: ${JSON.stringify(declared)}`,
    );
  }
  return String(declared[0]);
}

const GENERATIONS = {
  v3: {
    identityEndpoint: STASHDB_ENDPOINT,
    storedKey: "V3",

    async declaredSceneIdentifier(api) {
      const movies = await listRows(api, "/api/v3/movie");
      return oneIdentifier(
        movies.map((movie) => movie.stashId).filter(Boolean),
        "v3",
        "/api/v3/movie",
      );
    },
  },

  v2: {
    identityEndpoint: THEPORNDB_ENDPOINT,
    storedKey: "V2",

    // This generation carries a scene as an episode of a site, so the identifier is one level down
    // from the row a catalogue listing answers with.
    async declaredSceneIdentifier(api) {
      const sites = await listRows(api, "/api/v3/series");
      const declared = [];
      for (const site of sites) {
        const episodes = await listRows(api, `/api/v3/episode?seriesId=${String(site.id)}`);
        declared.push(...episodes.map((episode) => episode.tvdbId).filter(Boolean));
      }
      return oneIdentifier(declared, "v2", "/api/v3/episode");
    },
  },
};

/**
 * The per-generation members a shared scenario reads through.
 *
 * One object literal, so both generations answer to the same member names by construction rather
 * than by a reviewer noticing that one of them grew a member the other did not.
 *
 * @param {"v2"|"v3"} generation
 */
export function adapterFor(generation) {
  const own = GENERATIONS[generation];
  if (own === undefined) {
    throw new Error(
      `adapterFor: no adapter is written for the generation "${generation}"; written are ${Object.keys(GENERATIONS).join(", ")}.`,
    );
  }

  return {
    /** The source this generation stamps an imported item's identity under. */
    identityEndpoint: own.identityEndpoint,

    /** The scene identifier the captured delivery for this generation names. */
    deliveredIdentity: deliveredRemoteId(generation),

    /** The scene identifier the instance itself declares, read off a route this extension never calls. */
    declaredSceneIdentifier: (api) => own.declaredSceneIdentifier(api),

    /** This generation's half of the extension's stored options blob. */
    storedSection: (options) => options?.[own.storedKey],

    /**
     * The stored event type integer this instance renders an import under.
     *
     * The two generations number their event types differently, and a row seeded under the other
     * one's number is a row the walk correctly ignores.
     */
    historyEventTypeId: (whisparr) => {
      const rendered = whisparr[generation]?.history?.eventTypeNames;
      const found = Object.entries(rendered === undefined ? {} : rendered).find(
        ([, name]) => name === IMPORTED_EVENT_TYPE,
      );
      const id = Number(found?.[0]);
      if (!Number.isInteger(id)) {
        throw new Error(
          `generation adapter: no seeded row on ${generation} rendered as ${IMPORTED_EVENT_TYPE}; the instance rendered ${JSON.stringify(rendered)}`,
        );
      }
      return id;
    },

    /** The history records the instance answers with, newest first. */
    historyRows: async (api, { pageSize = 50 } = {}) => (await historyPage(api, pageSize)).records,

    /** What the instance itself holds: how many notifications, and how many history records. */
    instanceState: async (api) => {
      const notifications = await listRows(api, "/api/v3/notification");
      const page = await historyPage(api, 1);
      return { notifications: notifications.length, historyRecords: page.totalRecords };
    },
  };
}
