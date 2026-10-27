// Whether a Whisparr v2 instance serves the three routes the reflect-owned role sends on.
//
// The outbound client sends all three through the v3 generated client with no v2 arm, and no code
// here or upstream has ever driven them against a v2 instance. Reading cannot settle it: a
// capability array is a declaration, and a generated client is a schema, and neither is the instance.
//
// The three, as the product sends them:
//
//   1. GET api/v3/config/mediamanagement        - the hardlink setting
//   2. GET api/v3/manualimport                  - folder, filterExistingFiles=false
//   3. POST api/v3/command                      - { name: ManualImport, importMode: copy, files }
//
// v3 is driven in the same run as the CONTROL. A v2 status alone cannot separate "v2 does not serve
// it" from "this probe composed the request wrongly", and the generation known to serve these routes
// is the only thing that tells those apart.
//
// Each route's answer is judged on what the PRODUCT reads out of it, not on the status alone. A 200
// carrying a document without `copyUsingHardlinks` is a different fact from a 200 carrying it, and
// an answer of the first kind is neither a clean serve nor a clean absence.
//
// No API key and no header value is recorded: a record outlives the run that produced it.

const MEDIA_MANAGEMENT_PATH = "/api/v3/config/mediamanagement";
const MANUAL_IMPORT_PATH = "/api/v3/manualimport";
const COMMAND_PATH = "/api/v3/command";

// The member the product reads out of the hardlink document.
const HARDLINK_MEMBER = "copyUsingHardlinks";

// What ReflectOwnedPlanner.Command composes, restated here because a probe sends bytes rather than
// calling into the extension. Only the name and the import mode are fixed; the files are the rows
// the listing answered with.
const COMMAND_NAME = "ManualImport";
const IMPORT_MODE = "copy";

// A 4xx body that names the request itself as unknown, which is the command endpoint existing while
// the command does not. Any OTHER 4xx stays indeterminate rather than being read as an absence.
const UNKNOWN_REQUEST =
  /unknown command|not a (known|valid) command|does not exist|no such command/i;

// How much of a refusal body is kept. Enough to read what the instance objected to, bounded because
// a record is read by a person.
const BODY_EXCERPT_CHARS = 300;

// The closed set a reader may find in `verdict`. The last is what a run that established neither
// half is called, so a run that measured nothing cannot read as one that measured an absence.
const VERDICTS = [
  "v2-serves-all-three-reflect-owned-routes",
  "v2-serves-none-of-the-three-reflect-owned-routes",
  "inconclusive",
];

const ROUTE_NAMES = ["hardlinkSetting", "importableFiles", "attachOwnedFiles"];

/**
 * What one route's answer says about whether the instance serves it.
 *
 * `answersWhatTheProductReads` is deliberately not inferred from the status: the routes here are
 * read by a caller that takes a named member out of the body, so an answer arriving without it
 * settles nothing either way.
 *
 * @param {{status: number, transportError?: string, answersWhatTheProductReads?: boolean,
 *          namesTheRequestAsUnknown?: boolean}} observation
 * @returns {"served"|"not-served"|"indeterminate"}
 */
export function classifyRoute(observation) {
  if (observation.transportError) return "indeterminate";
  if (observation.status === 0) return "indeterminate";
  if (observation.namesTheRequestAsUnknown === true) return "not-served";
  if (observation.status === 404 || observation.status === 405) return "not-served";
  if (observation.status >= 200 && observation.status < 300) {
    return observation.answersWhatTheProductReads === true ? "served" : "indeterminate";
  }
  return "indeterminate";
}

/**
 * The verdict the two generations' answers support.
 *
 * A control that did not itself come out clean leaves the run inconclusive whatever v2 said, because
 * the run has not shown it composes these requests correctly at all. So does a mixed v2, and so does
 * any single route v2's answer did not settle: the split this measurement feeds is a binary choice,
 * and a partial answer is not one of its two branches.
 *
 * @param {{v2: Record<string, {classification: string}>, v3: Record<string, {classification: string}>}} observed
 * @returns {"v2-serves-all-three-reflect-owned-routes"|"v2-serves-none-of-the-three-reflect-owned-routes"|"inconclusive"}
 */
export function judgeReflectOwnedOnV2({ v2, v3 }) {
  const classifications = (perGeneration) =>
    ROUTE_NAMES.map((name) => perGeneration?.[name]?.classification);

  if (classifications(v3).some((one) => one !== "served")) return "inconclusive";

  const v2Classifications = classifications(v2);
  if (v2Classifications.every((one) => one === "served")) {
    return "v2-serves-all-three-reflect-owned-routes";
  }
  if (v2Classifications.every((one) => one === "not-served")) {
    return "v2-serves-none-of-the-three-reflect-owned-routes";
  }
  return "inconclusive";
}

/** One answer, summarised to what a reader needs and nothing a record must not outlive. */
function describeAnswer(response) {
  return {
    status: response.status,
    contentType: response.contentType,
    byteLength: response.text.length,
    bodyExcerpt: response.ok ? "" : response.text.slice(0, BODY_EXCERPT_CHARS),
  };
}

/** A call that may not connect at all, kept apart from one the instance refused. */
async function attempt(send) {
  try {
    return { response: await send(), transportError: "" };
  } catch (cause) {
    return { response: null, transportError: cause.message };
  }
}

function unreached(path, verb, transportError) {
  return {
    request: { verb, path },
    status: 0,
    contentType: "",
    byteLength: 0,
    bodyExcerpt: "",
    transportError,
    classification: classifyRoute({ status: 0, transportError }),
  };
}

async function driveHardlinkSetting(api) {
  const { response, transportError } = await attempt(() => api.get(MEDIA_MANAGEMENT_PATH));
  if (response === null) return unreached(MEDIA_MANAGEMENT_PATH, "GET", transportError);

  const document = response.json;
  const carriesTheMember =
    document !== null && typeof document === "object" && Object.hasOwn(document, HARDLINK_MEMBER);
  const observation = {
    ...describeAnswer(response),
    transportError: "",
    answersWhatTheProductReads: carriesTheMember,
  };

  return {
    request: { verb: "GET", path: MEDIA_MANAGEMENT_PATH },
    ...observation,
    member: HARDLINK_MEMBER,
    carriesTheMember,
    memberValue: carriesTheMember ? document[HARDLINK_MEMBER] : null,
    classification: classifyRoute(observation),
  };
}

async function driveImportableFiles(api, folder) {
  const path = `${MANUAL_IMPORT_PATH}?folder=${encodeURIComponent(folder)}&filterExistingFiles=false`;
  const { response, transportError } = await attempt(() => api.get(path));
  if (response === null) return unreached(path, "GET", transportError);

  // An empty listing with a success status is the route being served. How many files it found is not
  // the question, and a registered root with nothing in it is what this run offers it.
  const rows = Array.isArray(response.json) ? response.json : null;
  const observation = {
    ...describeAnswer(response),
    transportError: "",
    answersWhatTheProductReads: rows !== null,
  };

  return {
    request: { verb: "GET", path, inputs: { folder, filterExistingFiles: false } },
    ...observation,
    answeredAnArray: rows !== null,
    rowCount: rows === null ? null : rows.length,
    classification: classifyRoute(observation),
  };
}

async function driveAttachOwnedFiles(api, files) {
  const body = { name: COMMAND_NAME, files, importMode: IMPORT_MODE };
  const { response, transportError } = await attempt(() => api.post(COMMAND_PATH, body));
  if (response === null) return unreached(COMMAND_PATH, "POST", transportError);

  // Whether the instance ACCEPTED the command is the question, so the command is not then polled. A
  // generation answering a not-found, or naming the command as one it does not have, has settled it.
  const accepted =
    response.ok &&
    response.json !== null &&
    typeof response.json === "object" &&
    Object.hasOwn(response.json, "name");
  const namesTheRequestAsUnknown = !response.ok && UNKNOWN_REQUEST.test(response.text);
  const observation = {
    ...describeAnswer(response),
    transportError: "",
    answersWhatTheProductReads: accepted,
    namesTheRequestAsUnknown,
  };

  return {
    request: {
      verb: "POST",
      path: COMMAND_PATH,
      inputs: { name: COMMAND_NAME, importMode: IMPORT_MODE, fileCount: files.length },
    },
    ...observation,
    acceptedTheCommand: accepted,
    acknowledgedName: accepted ? (response.json.name ?? null) : null,
    acknowledgedStatus: accepted ? (response.json.status ?? null) : null,
    classification: classifyRoute(observation),
  };
}

/** All three routes against one generation, in the order the product sends them. */
async function driveGeneration(api, folder) {
  const hardlinkSetting = await driveHardlinkSetting(api);
  const importableFiles = await driveImportableFiles(api, folder);
  // The command carries whatever the listing answered with, which on a registered root holding
  // nothing is an empty array. That is the body the product would compose from the same listing.
  const attachOwnedFiles = await driveAttachOwnedFiles(api, []);
  return { hardlinkSetting, importableFiles, attachOwnedFiles };
}

const statusLine = (perGeneration) =>
  ROUTE_NAMES.map((name) => `${name} ${perGeneration[name].status}`).join(", ");

export const row = {
  id: "v2-reflect-owned-routes",
  label:
    "Whether Whisparr v2 serves the three routes the reflect-owned role sends on, with v3 as the control",
  requires: {
    // The Whisparr containers join the Cove instance's own network, so a row asking for either
    // generation asks for cove too.
    cove: true,
    whisparr: ["v3", "v2"],
    seedHistory: false,
    // The listing is asked about a real folder. Both generations refuse a root they never registered,
    // and a refusal naming a validator would read here as a route that is not served.
    rootFolder: true,
    network: false,
    live: false,
  },
  async run(ctx) {
    const v3 = await driveGeneration(ctx.whisparr.apiFor("v3"), ctx.whisparr.v3.rootFolder);
    const v2 = await driveGeneration(ctx.whisparr.apiFor("v2"), ctx.whisparr.v2.rootFolder);

    const verdict = judgeReflectOwnedOnV2({ v2, v3 });

    return {
      method: {
        verb: "GET",
        path: `${MEDIA_MANAGEMENT_PATH}, ${MANUAL_IMPORT_PATH} and ${COMMAND_PATH} on each generation`,
        inputs: {
          commandName: COMMAND_NAME,
          importMode: IMPORT_MODE,
          hardlinkMember: HARDLINK_MEMBER,
          v3Folder: ctx.whisparr.v3.rootFolder,
          v2Folder: ctx.whisparr.v2.rootFolder,
        },
      },
      verdict,
      observed: {
        verdictVocabulary: VERDICTS.join(" | "),
        keyPolicy:
          "Statuses, content types, bounded refusal excerpts and the routes' own inputs. No API key and no header value.",
        control: {
          statement:
            "v3 is the generation known to serve these routes. A control that did not come out clean on all three leaves the run inconclusive whatever v2 answered, because the run has then not shown it composes these requests correctly at all.",
          v3Clean: ROUTE_NAMES.every((name) => v3[name].classification === "served"),
        },
        statuses: { v3: statusLine(v3), v2: statusLine(v2) },
        v3,
        v2,
        limits:
          "Each route is driven once, with the body the product composes and an empty file set. It settles whether the instance serves the route, not whether an import it accepted would complete.",
      },
    };
  },
};
