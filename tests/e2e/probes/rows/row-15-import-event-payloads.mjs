// What each Whisparr generation sends when it IMPORTS a file, taken from a delivery it really made.
//
// Row 3 captured the `Test` delivery, which is the one that arrives on save and therefore the only
// one anyone has seen. Everything a consumer of the import event reads - the path, the root folder,
// the size, the remote identifier - lives in a body no run has ever produced, so a handler written
// against Row 3 is a handler written against the wrong event.
//
// Three questions, answered per generation and recorded whatever the answer is: what an import body
// looks like, which literal `eventType` string it carries, and whether it names the instance's own
// root folder. The third is the first input to resolving a reported path against a Cove library, so
// its absence changes a design rather than a detail.
//
// An import cannot be provoked by saving a connection, so this row has to cause one: it adds an
// entity, places a real media file in the instance's own container, and drives the instance's own
// manual-import command over it. A step that fails is recorded as not-obtained with what it
// answered. Nothing here synthesises a body - a hand-written payload presented as a capture would be
// worse than the gap, because every later field read would rest on it.
import { join } from "node:path";

import { attemptUntil } from "../../lib/poll.mjs";
import { APP_USER } from "../../lib/whisparr-images.mjs";
import { writeCompanion } from "../lib/record.mjs";

const SCHEMA_PATH = "/api/v3/notification/schema";
const NOTIFICATION_PATH = "/api/v3/notification";
const COMMAND_PATH = "/api/v3/command";
const MANUAL_IMPORT_PATH = "/api/v3/manualimport";
const QUALITY_PROFILE_PATH = "/api/v3/qualityprofile";
const INDEXER_PATH = "/api/v3/indexer";
const DOWNLOAD_CLIENT_PATH = "/api/v3/downloadclient";

// Obviously synthetic, and it authorises nothing. A capture is written to a record that outlives the
// run, so nothing resembling a credential may enter the request in the first place.
const SYNTHETIC_URL_SECRET = "row15-not-a-real-secret";

// The folder the placed file is imported FROM, inside the instance's own container. Outside the
// library root on purpose: an import moves a file out of a download folder and into the library, so
// a source already inside the root is not the operation this row is provoking.
const SOURCE_FOLDER = "/probe-import";

const MEDIA_FIXTURE = join(
  import.meta.dirname,
  "..",
  "..",
  "lib",
  "fixtures-media",
  "test-video.mp4",
);

// The `Test` delivery fires the moment the connection is saved, so the import delivery is never the
// first one this row's listener sees. It is told apart by its own eventType rather than by arrival
// order.
const TEST_EVENT_TYPE = "Test";

const CAPTURE_TIMEOUT_MS = 120_000;
const COMMAND_TIMEOUT_MS = 120_000;
const COMMAND_INTERVAL_MS = 2_000;
const LOOKUP_TIMEOUT_MS = 90_000;
const LOOKUP_INTERVAL_MS = 5_000;
const PROFILE_TIMEOUT_MS = 60_000;
const PROFILE_INTERVAL_MS = 2_000;
const REFRESH_TIMEOUT_MS = 120_000;
const REFRESH_INTERVAL_MS = 3_000;

const TERMINAL_COMMAND_STATUSES = new Set(["completed", "failed", "aborted", "cancelled"]);

// The lookups reach the vendor's metadata service. The terms name businesses rather than people, and
// nothing a lookup returns about its subject is recorded: the row needs a subject to import against,
// not a subject to publish.
const SCENE_TERM = "brazzers";
const SERIES_TERM = "teen";

// Long enough for a representative value, short enough that no single field can carry a page of
// third-party text into the record.
const VALUE_CHARS = 64;

// A header name, and nothing that could be a name-and-value pair. The record carries names only.
const HEADER_NAME = /^[A-Za-z0-9][A-Za-z0-9-]*$/;

/** One level of depth: what a value IS, plus its key names or one representative value. */
function shapeOf(value) {
  if (Array.isArray(value)) {
    const first = value[0];
    return {
      type: "array",
      length: value.length,
      itemKeys:
        first !== null && typeof first === "object" && !Array.isArray(first)
          ? Object.keys(first)
          : null,
    };
  }
  if (value !== null && typeof value === "object") {
    return { type: "object", keys: Object.keys(value) };
  }
  const rendered = String(value);
  return {
    type: value === null ? "null" : typeof value,
    value: rendered.length <= VALUE_CHARS ? rendered : `<${rendered.length} chars>`,
  };
}

/**
 * The header NAMES a delivery carried, refusing anything that could be a value.
 *
 * Row 3 records this delivery's headers whole, which is safe for a `Test` body it also causes. An
 * import delivery is caused by an entity add against the vendor's own service, so a value here could
 * carry something the row did not choose to publish. The names are the whole of what this row needs
 * - a consumer's question is which headers it sees before a body - and the assertion is what keeps a
 * later edit from widening that.
 */
export function headerNames(headers) {
  const names = Object.keys(headers ?? {}).sort();
  const offending = names.filter((name) => !HEADER_NAME.test(name));
  if (offending.length > 0) {
    throw new Error(
      `row-15-import-event-payloads: refusing to record ${offending.join(", ")} as a header name; a recorded header entry must be a name and never a value.`,
    );
  }
  return names;
}

/**
 * The connection's own trigger-flag names, read off the running schema.
 *
 * The `supports*` twins are excluded: they state what the connection CAN subscribe to, not what a
 * registration is subscribing to, and sending one back does nothing.
 */
const triggerFlagNames = (webhook) =>
  Object.entries(webhook)
    .filter(([key, value]) => typeof value === "boolean" && !key.startsWith("supports"))
    .map(([key]) => key);

/**
 * Every trigger enabled except the ones a running instance raises by itself.
 *
 * At least one must be on or the connection is disabled and the save sends nothing. Health and
 * update triggers are left off because an instance fires those on its own schedule, and a delivery
 * this row did not cause is one it would have to tell apart from the one it did.
 */
const enabledTriggers = (webhook) =>
  Object.fromEntries(
    triggerFlagNames(webhook).map((name) => [name, !/health|applicationupdate/i.test(name)]),
  );

async function registerListener(api, listener, generation) {
  const schema = await api.get(SCHEMA_PATH);
  const webhook = schema.json?.find?.((entry) => entry.implementation === "Webhook");
  if (webhook === undefined) {
    return {
      status: schema.status,
      registered: false,
      why: `GET ${SCHEMA_PATH} answered ${schema.status} ${schema.contentType} and declared no Webhook entry.`,
    };
  }

  const triggers = enabledTriggers(webhook);
  const path = `/row15/${generation}?s=${SYNTHETIC_URL_SECRET}`;
  const created = await api.post(NOTIFICATION_PATH, {
    ...triggers,
    name: `cove-probe-row15-${generation}`,
    implementation: webhook.implementation,
    implementationName: webhook.implementationName,
    configContract: webhook.configContract,
    tags: [],
    fields: [
      { name: "url", value: listener.url(path) },
      { name: "method", value: 1 },
    ],
  });

  return {
    status: created.status,
    registered: created.status === 201,
    url: listener.url(path),
    syntheticUrlSecret: SYNTHETIC_URL_SECRET,
    triggerFlagsEnabled: Object.entries(triggers)
      .filter(([, enabled]) => enabled)
      .map(([flag]) => flag),
    triggerFlagsLeftOff: Object.entries(triggers)
      .filter(([, enabled]) => !enabled)
      .map(([flag]) => flag),
    ...(created.status === 201 ? {} : { why: created.text.slice(0, 400) }),
  };
}

/**
 * What the instance could acquire with, which bounds everything this row does to it.
 *
 * Throws rather than recording a non-empty surface: this row adds a monitored entity, and a search
 * that reached a configured indexer would make the add acquisitive.
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
      `row-15-import-event-payloads: ${generation} reported ${JSON.stringify(counts)} for ${INDEXER_PATH} and ${DOWNLOAD_CLIENT_PATH}. This row adds a monitored entity, and it only does so against an instance that can reach nothing.`,
    );
  }
  return counts;
}

/** A profile an add can name, polled because a fresh instance seeds its defaults after it answers. */
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
      label: "row-15 firstQualityProfileId",
    },
  );
  if (!settled) {
    throw new Error(
      `row-15-import-event-payloads: GET ${QUALITY_PROFILE_PATH} on ${generation} last answered ${note}, so no add can name a profile.`,
    );
  }
  return value;
}

/** The lookup results a subject is taken from, refusing an answer too short to use. */
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
      label: "row-15 lookupSubjects",
    },
  );
  if (!settled) {
    throw new Error(
      `row-15-import-event-payloads: GET ${path} on ${generation} last answered ${note}, and this row needs ${wanted}. Both generations proxy a lookup through the vendor's metadata service, so a short answer here is an unreachable or refusing service rather than a missing capability.`,
    );
  }
  return value;
}

/** Puts the fixture media file inside the instance's container, owned by the app. */
async function placeMediaFile(container, name) {
  const target = `${SOURCE_FOLDER}/${name}`;
  await container.exec(["mkdir", "-p", SOURCE_FOLDER], { user: "root" });
  await container.copyFilesToContainer([{ source: MEDIA_FIXTURE, target }]);
  await container.exec(["chown", "-R", APP_USER, SOURCE_FOLDER], { user: "root" });
  const listed = await container.exec(["ls", "-l", target]);
  return { path: target, listing: listed.output.trim(), exitCode: listed.exitCode };
}

/** One command run to a terminal status, or the last status it was seen in. */
async function runCommandToCompletion(api, body, label) {
  const started = await api.post(COMMAND_PATH, body);
  if (started.status !== 201) {
    return {
      name: body.name,
      status: started.status,
      accepted: false,
      why: started.text.slice(0, 400),
    };
  }

  const { settled, value, note } = await attemptUntil(
    async (_signal, record) => {
      const read = await api.get(`${COMMAND_PATH}/${started.json.id}`);
      const state = String(read.json?.status ?? "").toLowerCase();
      record(`${state || "unknown"}/${read.json?.result ?? "unknown"}`);
      return TERMINAL_COMMAND_STATUSES.has(state) ? { value: read.json } : null;
    },
    { timeoutMs: COMMAND_TIMEOUT_MS, intervalMs: COMMAND_INTERVAL_MS, label },
  );

  return {
    name: body.name,
    status: started.status,
    accepted: true,
    reachedTerminal: settled,
    commandStatus: settled ? value.status : null,
    commandResult: settled ? value.result : null,
    commandException: settled ? (value.exception ?? null) : null,
    lastSeen: note,
  };
}

/** What the instance's own manual-import route makes of the placed file. */
async function manualImportCandidates(api, folder) {
  const response = await api.get(
    `${MANUAL_IMPORT_PATH}?folder=${encodeURIComponent(folder)}&filterExistingFiles=false`,
  );
  return {
    status: response.status,
    contentType: response.contentType,
    count: Array.isArray(response.json) ? response.json.length : null,
    items: Array.isArray(response.json) ? response.json : [],
    ...(Array.isArray(response.json) ? {} : { why: response.text.slice(0, 400) }),
  };
}

/**
 * The one delivery this row's import caused, told apart from the save's own `Test` delivery.
 *
 * Returns null rather than throwing when none arrives: an import that produced no delivery is one of
 * this row's possible answers, and it has to reach the record rather than end the run.
 */
async function awaitImportDelivery(listener, generation) {
  try {
    const captures = await listener.waitForCaptures(1, {
      timeoutMs: CAPTURE_TIMEOUT_MS,
      match: (delivery) => {
        if (!delivery.path.startsWith(`/row15/${generation}`)) return false;
        try {
          return JSON.parse(delivery.body).eventType !== TEST_EVENT_TYPE;
        } catch {
          return false;
        }
      },
    });
    return captures[captures.length - 1];
  } catch {
    return null;
  }
}

/**
 * Where a root folder is named in a captured body, at the top level or one object down.
 *
 * The key name is reported as the instance spelled it, not normalised: the whole question is which
 * literal name a consumer would have to read.
 */
function rootFolderReading(body) {
  const matches = [];
  const looksLikeRoot = (key) => /rootfolder/i.test(key);
  for (const [key, value] of Object.entries(body)) {
    if (looksLikeRoot(key)) matches.push({ at: key, key, value });
    if (value !== null && typeof value === "object" && !Array.isArray(value)) {
      for (const [innerKey, innerValue] of Object.entries(value)) {
        if (looksLikeRoot(innerKey)) {
          matches.push({ at: `${key}.${innerKey}`, key: innerKey, value: innerValue });
        }
      }
    }
  }
  return {
    present: matches.length > 0,
    found: matches,
    searched: "every top-level key and every key one object below it",
  };
}

/** Every path-shaped member of a captured body, which is what a consumer reads to locate the file. */
function pathReading(body) {
  const matches = [];
  const looksLikePath = (key) => /path|folder|file/i.test(key);
  for (const [key, value] of Object.entries(body)) {
    if (looksLikePath(key) && typeof value === "string") matches.push({ at: key, value });
    if (value !== null && typeof value === "object" && !Array.isArray(value)) {
      for (const [innerKey, innerValue] of Object.entries(value)) {
        if (looksLikePath(innerKey) && typeof innerValue === "string") {
          matches.push({ at: `${key}.${innerKey}`, value: innerValue });
        }
      }
    }
  }
  return matches;
}

/** The capture, described. `null` in, an explicit not-obtained marker out. */
function describeDelivery(capture, ctx, generation) {
  if (capture === null) {
    return {
      obtained: false,
      why: `No delivery other than the ${TEST_EVENT_TYPE} one arrived at the listener within ${CAPTURE_TIMEOUT_MS}ms of the import being driven.`,
      eventType: null,
      rootFolder: { present: null, why: "no body was captured" },
      savedTo: null,
    };
  }

  let body;
  try {
    body = JSON.parse(capture.body);
  } catch {
    return {
      obtained: false,
      why: `The delivery carried ${capture.body.length} byte(s) that did not parse as JSON.`,
      eventType: null,
      rootFolder: { present: null, why: "the body did not parse" },
      savedTo: null,
    };
  }

  return {
    obtained: true,
    arrivedAt: capture.ts,
    verb: capture.verb,
    path: capture.path,
    // Names only. What a consumer sees before it reads a body is the question; a value is not.
    headerNames: headerNames(capture.headers),
    bodyByteLength: capture.body.length,
    topLevelKeys: Object.keys(body),
    eventType: body.eventType ?? null,
    shapes: Object.fromEntries(Object.entries(body).map(([key, value]) => [key, shapeOf(value)])),
    rootFolder: rootFolderReading(body),
    pathMembers: pathReading(body),
    savedTo:
      ctx.outDir === undefined
        ? null
        : writeCompanion(
            ctx.outDir,
            `webhook-import-payload-${generation}-${ctx.builds.whisparr[generation].version}.json`,
            capture.body,
          ),
  };
}

async function driveV3(ctx, listener) {
  const api = ctx.whisparr.apiFor("v3");
  const acquisition = await acquisitionSurface(api, "v3");
  const registration = await registerListener(api, listener, "v3");
  const qualityProfileId = await firstQualityProfileId(api, "v3");

  const scenePath = `/api/v3/lookup/scene?term=${SCENE_TERM}`;
  const [subject] = await lookupSubjects(api, scenePath, "v3", 1);
  const created = await api.post("/api/v3/movie", {
    ...subject.movie,
    qualityProfileId,
    rootFolderPath: ctx.whisparr.v3.rootFolder,
    monitored: true,
    addOptions: { searchForMovie: false },
  });
  if (created.status !== 201) {
    return {
      acquisition,
      registration,
      entity: { added: false, status: created.status, why: created.text.slice(0, 400) },
      delivery: describeDelivery(null, ctx, "v3"),
    };
  }

  const movieId = created.json.id;
  const placed = await placeMediaFile(
    ctx.whisparr.v3.container,
    "Cove.Probe.Row15.2024.1080p.WEB-DL.mp4",
  );
  const candidates = await manualImportCandidates(api, SOURCE_FOLDER);
  const candidate = candidates.items[0];

  const command = await runCommandToCompletion(
    api,
    {
      name: "ManualImport",
      importMode: "copy",
      files: [
        {
          path: placed.path,
          movieId,
          quality: candidate?.quality ?? null,
          languages: candidate?.languages ?? null,
          releaseGroup: candidate?.releaseGroup ?? null,
          indexerFlags: candidate?.indexerFlags ?? 0,
        },
      ],
    },
    "row-15 v3 ManualImport",
  );

  const capture = await awaitImportDelivery(listener, "v3");
  const readBack = await api.get(`/api/v3/movie/${movieId}`);

  return {
    acquisition,
    registration,
    entity: {
      added: true,
      // A lookup result describes a real production. Nothing identifying it is recorded.
      identityRecorded: false,
      rootFolderPath: ctx.whisparr.v3.rootFolder,
      hasFileAfterImport: readBack.json?.hasFile ?? null,
    },
    placedFile: placed,
    manualImport: {
      status: candidates.status,
      candidateCount: candidates.count,
      candidateKeys: candidate === undefined ? null : Object.keys(candidate),
      candidateRejections: candidate?.rejections ?? null,
      ...(candidates.why === undefined ? {} : { why: candidates.why }),
    },
    command,
    delivery: describeDelivery(capture, ctx, "v3"),
  };
}

async function driveV2(ctx, listener) {
  const api = ctx.whisparr.apiFor("v2");
  const acquisition = await acquisitionSurface(api, "v2");
  const registration = await registerListener(api, listener, "v2");
  const qualityProfileId = await firstQualityProfileId(api, "v2");

  const seriesPath = `/api/v3/series/lookup?term=${SERIES_TERM}`;
  const [subject] = await lookupSubjects(api, seriesPath, "v2", 1);
  const created = await api.post("/api/v3/series", {
    ...subject,
    qualityProfileId,
    rootFolderPath: ctx.whisparr.v2.rootFolder,
    // Monitored, unlike the add-options row: an unmonitored site is not refreshed into the scene
    // rows an import has to attach to, and there is nothing to import against without one.
    monitored: true,
    addOptions: { searchForMissingEpisodes: false },
  });
  if (created.status !== 201) {
    return {
      acquisition,
      registration,
      entity: { added: false, status: created.status, why: created.text.slice(0, 400) },
      delivery: describeDelivery(null, ctx, "v2"),
    };
  }

  const seriesId = created.json.id;
  const episodes = await awaitEpisodes(api, seriesId);
  const placed = await placeMediaFile(
    ctx.whisparr.v2.container,
    "Cove.Probe.Row15.S01E01.1080p.WEB-DL.mp4",
  );
  const candidates = await manualImportCandidates(api, SOURCE_FOLDER);
  const candidate = candidates.items[0];

  const command =
    episodes.length === 0
      ? {
          name: "ManualImport",
          accepted: false,
          why: "The added site was refreshed into no scene rows, so no import could name one.",
        }
      : await runCommandToCompletion(
          api,
          {
            name: "ManualImport",
            importMode: "copy",
            files: [
              {
                path: placed.path,
                seriesId,
                episodeIds: [episodes[0].id],
                quality: candidate?.quality ?? null,
                languages: candidate?.languages ?? null,
                releaseGroup: candidate?.releaseGroup ?? null,
              },
            ],
          },
          "row-15 v2 ManualImport",
        );

  const capture = episodes.length === 0 ? null : await awaitImportDelivery(listener, "v2");

  return {
    acquisition,
    registration,
    entity: {
      added: true,
      identityRecorded: false,
      rootFolderPath: ctx.whisparr.v2.rootFolder,
      episodeCount: episodes.length,
    },
    placedFile: placed,
    manualImport: {
      status: candidates.status,
      candidateCount: candidates.count,
      candidateKeys: candidate === undefined ? null : Object.keys(candidate),
      candidateRejections: candidate?.rejections ?? null,
      ...(candidates.why === undefined ? {} : { why: candidates.why }),
    },
    command,
    delivery: describeDelivery(capture, ctx, "v2"),
  };
}

/**
 * The scene rows a site add was refreshed into, polled.
 *
 * The add returns before the refresh that creates them has run, so a single read finds none and an
 * import then has nothing to attach to.
 */
async function awaitEpisodes(api, seriesId) {
  const { settled, value } = await attemptUntil(
    async (_signal, record) => {
      const response = await api.get(`/api/v3/episode?seriesId=${seriesId}`);
      const rows = Array.isArray(response.json) ? response.json : [];
      record(`${response.status} with ${rows.length} row(s)`);
      return rows.length > 0 ? { value: rows } : null;
    },
    {
      timeoutMs: REFRESH_TIMEOUT_MS,
      intervalMs: REFRESH_INTERVAL_MS,
      label: "row-15 awaitEpisodes",
    },
  );
  return settled ? value : [];
}

export const row = {
  id: "row-15-import-event-payloads",
  label: "What each Whisparr generation sends when it imports a file, captured verbatim",
  requires: {
    // Whisparr and the listener both join the Cove instance's own network, so a row asking for
    // either asks for cove too.
    cove: true,
    whisparr: ["v3", "v2"],
    seedHistory: false,
    support: ["webhook-listener"],
    // Both generations refuse an add whose destination is not a registered library root, and an
    // import has to have somewhere to import to.
    rootFolder: true,
    // Every subject is found through a lookup, and both generations proxy those to the vendor.
    network: true,
    live: false,
  },
  async run(ctx) {
    const listener = ctx.support["webhook-listener"];
    const v3 = await driveV3(ctx, listener);
    const v2 = await driveV2(ctx, listener);

    const obtained = [v3, v2].filter((generation) => generation.delivery.obtained).length;

    return {
      method: {
        verb: "POST",
        path: `${NOTIFICATION_PATH} then an entity add, a placed file and ${COMMAND_PATH} ManualImport, with the listener's own capture`,
        inputs: {
          listener: listener.url("/row15/<generation>"),
          urlSecret: SYNTHETIC_URL_SECRET,
          sourceFolder: SOURCE_FOLDER,
          sceneTerm: SCENE_TERM,
          seriesTerm: SERIES_TERM,
        },
      },
      verdict:
        obtained === 2
          ? "import-payloads-captured"
          : obtained === 1
            ? "import-payload-captured-on-one-generation"
            : "no-import-payload-obtained",
      observed: {
        questions: {
          statement:
            "Three questions, answered per generation. An answer this run could not obtain is recorded as not-obtained with what the step answered, never left absent and never synthesised.",
          bodyShape: {
            v3: v3.delivery.obtained ? v3.delivery.topLevelKeys : null,
            v2: v2.delivery.obtained ? v2.delivery.topLevelKeys : null,
          },
          eventTypeString: {
            note: "The literal string the BODY carries, which is a different vocabulary from the trigger-flag name that subscribed to it.",
            v3: v3.delivery.eventType,
            v2: v2.delivery.eventType,
          },
          rootFolderInTheBody: {
            note: "Present-or-absent, and where. This is the first input to resolving a reported path against a Cove library root, so an absence changes the ingest design rather than a detail of it.",
            v3: v3.delivery.rootFolder,
            v2: v2.delivery.rootFolder,
          },
        },
        v3,
        v2,
      },
    };
  },
};
