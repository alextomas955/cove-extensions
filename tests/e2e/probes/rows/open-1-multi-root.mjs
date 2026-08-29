// What is actually true about multiple library roots on each side, and what a path reported by one
// side resolves to on the other.
//
// The spec assumes one shared path and says a multi-root setup is undefined rather than supported.
// That describes the machine this ships on: the owner's own Cove install has more than one library
// root today, so this is a first-run condition rather than a future concern. The COUNT of those
// roots is recorded here for that reason, and nothing else about them: a library path names where
// someone keeps their own media.
//
// Five things are recorded and kept apart, because conflating any two is how this question gets
// answered wrongly — in particular the remote-path-mapping table, which is Whisparr's answer to a
// DIFFERENT question (per-download-client path translation) and not to having several root folders.
//
// Every observation here is an observation. The correspondence RULE — which root owns a path when
// more than one could — is a product decision rather than an external fact, so this row records
// what the two sides do and states plainly that the rule is unsettled. It does not propose one.
import { createApiClient } from "../../lib/apiClient.mjs";
import { installConfigFingerprint, liftLibraryPathCount } from "../../lib/cove-providers.mjs";
import { attemptUntil } from "../../lib/poll.mjs";
import { libraryRootsContaining, registerRootFolder } from "../../lib/whisparr-fixture.mjs";

const COVE_CONFIG_PATH = "/api/system/config";
const ROOT_FOLDER_PATH = "/api/v3/rootfolder";
const REMOTE_PATH_MAPPING_PATH = "/api/v3/remotepathmapping";
const MOVIE_PATH = "/api/v3/movie";
const SERIES_PATH = "/api/v3/series";

// Two roots per generation, both under one of Cove's library roots, which is the third degenerate
// case built as a fixture rather than imagined. They are directories in the Whisparr container; the
// correspondence the spec's same-path model assumes is between path STRINGS, and these containers
// share no filesystem.
const WHISPARR_ROOTS = ["open1-root-a", "open1-root-b"];

// A directory under an existing Cove root, offered to Cove as a library root of its own. Whether
// Cove accepts it is the observation; the nested case cannot be measured on a Cove that refuses it.
const NESTED_ROOT_LEAF = "open1-nested-root";

// The fields an entity response could carry root information in, probed as a set so an absent one
// is recorded as absent.
const ROOT_FIELDS = ["path", "rootFolderPath", "folder"];

const CONFIG_READ_BACK_TIMEOUT_MS = 30_000;
const CONFIG_READ_BACK_INTERVAL_MS = 1_000;

const VERDICT = "observed";

const describeResponse = (response) => ({
  status: response.status,
  contentType: response.contentType,
});

const rootPathsOf = (config) => (config?.covePaths ?? []).map((entry) => entry.path);

/** Cove's own library roots, as Cove states them. */
async function coveSide(api) {
  const response = await api.get(COVE_CONFIG_PATH);
  const roots = rootPathsOf(response.json);
  return {
    ...describeResponse(response),
    source: `${COVE_CONFIG_PATH} -> covePaths[].path, so these are the roots Cove itself reports`,
    sourceIsNotTheHarness:
      "Taken from Cove rather than from the compose environment this same harness set. A check built out of the value the environment owns cannot notice the real one being wrong.",
    roots,
    count: roots.length,
  };
}

/**
 * Registers this row's two roots on one generation and reports what the instance lists.
 *
 * Registration goes through the fixture's own helper, which creates the directory, hands it to the
 * account the application runs as, and refuses a root the instance reports as inaccessible. That
 * refusal is the point: a root reported inaccessible invalidates everything measured against it, so
 * it must raise rather than become a datum.
 */
async function whisparrSide(ctx, generation, roots) {
  const api = ctx.whisparr.apiFor(generation);
  const container = ctx.whisparr[generation].container;
  for (const path of roots) {
    await registerRootFolder(container, api, generation, path);
  }

  const listing = await api.get(ROOT_FOLDER_PATH);
  const listed = Array.isArray(listing.json) ? listing.json : [];
  const registered = roots.map((path) => {
    const entry = listed.find((root) => root.path === path);
    return {
      path,
      id: entry?.id ?? null,
      accessible: entry?.accessible ?? null,
      // The generations carry different extra fields, and which ones they are is part of the answer.
      extraFields: Object.keys(entry ?? {})
        .filter((key) => !["path", "id", "accessible"].includes(key))
        .sort()
        .join(" "),
    };
  });

  const mapping = await api.get(REMOTE_PATH_MAPPING_PATH);
  return {
    ...describeResponse(listing),
    registeredByThisRow: registered,
    listingLength: listed.length,
    listingNote:
      "The listing also carries whatever else the run registered, so only the roots this row created are described.",
    remotePathMapping: {
      ...describeResponse(mapping),
      length: Array.isArray(mapping.json) ? mapping.json.length : null,
    },
  };
}

/**
 * Where an entity says it lives, taken from an entity the instance actually holds.
 *
 * From the response rather than from a schema: a schema says which fields exist, and the question
 * here is what a running instance puts in them.
 */
async function entityPath(api, path, kind) {
  const response = await api.get(path);
  const entity = Array.isArray(response.json) ? response.json[0] : undefined;
  const reported = entity?.path;
  return {
    ...describeResponse(response),
    kind,
    source: `${path}, first entity`,
    path: reported ?? null,
    isAbsolute: typeof reported === "string" ? reported.startsWith("/") : null,
    // Probed and present are kept apart so a field the response does not carry is recorded as
    // absent rather than going unmentioned.
    rootFieldsProbed: ROOT_FIELDS.join(" "),
    rootFieldsPresent: ROOT_FIELDS.filter((field) => entity !== undefined && field in entity).join(
      " ",
    ),
  };
}

/**
 * Offers Cove a library root nested inside one it already has, and puts the configuration back.
 *
 * The nested case cannot be observed on a Cove that has no nested roots, and the fixture's two are
 * siblings. So it is built, measured and undone: the restore is verified by re-reading, because a
 * save that did not land and a save that was rejected read the same from here.
 */
async function nestedCoveRoots(harness, api, parent) {
  const nested = `${parent}/${NESTED_ROOT_LEAF}`;
  const { exitCode } = await harness.exec(["mkdir", "-p", nested], { user: "root" });

  const before = await api.get(COVE_CONFIG_PATH);
  const original = before.json;
  const saved = await api.put(COVE_CONFIG_PATH, {
    ...original,
    covePaths: [...original.covePaths, { ...original.covePaths[0], path: nested }],
  });

  const { settled, value } = await attemptUntil(
    async (_signal, note) => {
      const roots = rootPathsOf((await api.get(COVE_CONFIG_PATH)).json);
      note(`${roots.length} root(s)`);
      return roots.includes(nested) ? { value: roots } : null;
    },
    {
      timeoutMs: CONFIG_READ_BACK_TIMEOUT_MS,
      intervalMs: CONFIG_READ_BACK_INTERVAL_MS,
      label: "open-1 nested root read-back",
    },
  );

  await api.put(COVE_CONFIG_PATH, original);
  const { settled: restored } = await attemptUntil(
    async (_signal, note) => {
      const roots = rootPathsOf((await api.get(COVE_CONFIG_PATH)).json);
      note(`${roots.length} root(s)`);
      return !roots.includes(nested) ? { value: roots } : null;
    },
    {
      timeoutMs: CONFIG_READ_BACK_TIMEOUT_MS,
      intervalMs: CONFIG_READ_BACK_INTERVAL_MS,
      label: "open-1 nested root restore",
    },
  );
  if (!restored) {
    throw new Error(
      `open-1-multi-root: Cove kept the nested library root "${nested}" after the configuration was put back, so this run has left the fixture configured differently from how it found it.`,
    );
  }

  return {
    nested,
    directoryCreated: exitCode === 0,
    saveStatus: saved.status,
    coveReportsIt: settled,
    rootsWhileConfigured: settled ? value : null,
    restored,
    restoredTo: rootPathsOf((await api.get(COVE_CONFIG_PATH)).json),
  };
}

/** One containment question, with the answer and never a rule. */
function resolve(path, roots, question) {
  const matches = libraryRootsContaining(path, roots);
  return { question, path, against: roots, matches, matchCount: matches.length };
}

export const row = {
  id: "open-1-multi-root",
  label:
    "What multiple roots on either side actually look like, and what a reported path resolves to",
  requires: {
    cove: true,
    // Whisparr containers join the Cove instance's own network, so a row asking for one asks for both.
    whisparr: ["v3", "v2"],
    // The entity whose reported path is read is the one the seed puts there, which needs no lookup
    // and reaches nothing outside this machine.
    seedHistory: true,
    support: [],
    rootFolder: false,
    network: false,
    live: false,
  },
  async run(ctx) {
    const fingerprintBefore = installConfigFingerprint();
    const coveApi = createApiClient(
      () => ctx.harness.baseUrl,
      () => ctx.harness.token,
    );

    const cove = await coveSide(coveApi);
    if (cove.roots.length === 0) {
      throw new Error(
        `open-1-multi-root: ${COVE_CONFIG_PATH} reported no library roots, so nothing below can be resolved against Cove's own configuration.`,
      );
    }

    const rootsFor = (generation) =>
      WHISPARR_ROOTS.map((leaf) => `${cove.roots[0]}/${generation}-${leaf}`);
    const whisparr = {
      v3: await whisparrSide(ctx, "v3", rootsFor("v3")),
      v2: await whisparrSide(ctx, "v2", rootsFor("v2")),
    };

    const entities = {
      v3: await entityPath(ctx.whisparr.apiFor("v3"), MOVIE_PATH, "movie"),
      v2: await entityPath(ctx.whisparr.apiFor("v2"), SERIES_PATH, "series"),
    };

    const nested = await nestedCoveRoots(ctx.harness, coveApi, cove.roots[0]);
    const nestedRoots = nested.rootsWhileConfigured ?? cove.roots;
    const nestedResolution = resolve(
      `${nested.nested}/a-file-under-the-nested-root`,
      nestedRoots,
      "A path lying under a Cove library root that is itself inside another Cove library root.",
    );
    const underNothing = entities.v3.path ?? "/there-is-no-cove-root-above-this";

    const degenerate = {
      underNoCoveRoot: {
        ...resolve(
          underNothing,
          cove.roots,
          "A path a Whisparr entity reports that lies under none of Cove's library roots.",
        ),
        result:
          "The path resolves into no Cove root at all, so there is nothing for a same-path model to correspond to. This is the case the import banner exists for.",
      },
      underTwoNestedCoveRoots: {
        ...nestedResolution,
        coveAcceptedTheNestedRoot: nested.coveReportsIt,
        configuration: nested,
        deterministic: nested.coveReportsIt ? nestedResolution.matchCount === 1 : null,
        whichRootWins:
          nested.coveReportsIt && nestedResolution.matchCount === 1
            ? nestedResolution.matches[0]
            : null,
        result: nested.coveReportsIt
          ? "Cove accepts a library root nested inside another and reports both. Containment then matches BOTH, and nothing in either side's data says which of them owns the path — so the resolution is not determined by what was measured. Choosing one is the product decision this row does not make."
          : "Cove did not report the nested root, so the case could not be built on this build and nothing is concluded from it.",
      },
      twoWhisparrRootsIntoOneCoveRoot: {
        ...resolve(
          whisparr.v3.registeredByThisRow[0].path,
          cove.roots,
          "Two registered Whisparr root folders that both lie under one Cove library root.",
        ),
        second: resolve(
          whisparr.v3.registeredByThisRow[1].path,
          cove.roots,
          "The second of the two.",
        ),
        result:
          "Both Whisparr roots resolve into the same single Cove root, so the mapping from Whisparr roots to Cove roots is many-to-one here. What that should MEAN for a sync is not observable; it is decided.",
        sameStringNotSameFilesystem:
          "The Whisparr roots are directories in a Whisparr container and the Cove root is a directory in Cove's. They correspond because the STRINGS do, which is the same-path assumption the spec starts from, and this fixture tests that assumption rather than sharing a volume.",
      },
    };

    const fingerprintAfter = installConfigFingerprint();
    const ownerInstall = liftLibraryPathCount();

    return {
      method: {
        verb: "GET",
        path: `${COVE_CONFIG_PATH} on Cove; ${ROOT_FOLDER_PATH} and ${REMOTE_PATH_MAPPING_PATH} on both Whisparr generations; the resolution done with the harness's own path-containment helper`,
        inputs: { whisparrRootLeaves: WHISPARR_ROOTS.join(" "), nestedRootLeaf: NESTED_ROOT_LEAF },
      },
      verdict: VERDICT,
      observed: {
        verdictVocabulary: `${VERDICT} — and deliberately nothing stronger`,
        whatIsNotSettled:
          "The correspondence RULE. Which root owns a path when more than one could, and what a Whisparr root with no Cove root above it should do, are product decisions and not facts about either system. This row records what both sides do and leaves the rule open; a later step decides it with the owner.",
        coveLibraryRoots: cove,
        whisparrRootFolders: whisparr,
        entityReportedPaths: {
          ...entities,
          note: "Taken from an entity each instance actually holds, so these are the fields a running build puts values in rather than the fields a schema declares. Which of the probed root fields each response carries is recorded per generation rather than stated once for both.",
        },
        remotePathMappingIsADifferentAxis: {
          v3: whisparr.v3.remotePathMapping,
          v2: whisparr.v2.remotePathMapping,
          statement:
            "This table maps a path as a DOWNLOAD CLIENT reports it to a path the application can reach. It is per-download-client, it exists whether or not either side has several roots, and it says nothing about which library root an entity belongs to. It is recorded beside the root folders because listing the two together without separating them is what makes this question look answered when it is not.",
        },
        degenerateCases: degenerate,
        ownerInstall: {
          libraryPathCount: ownerInstall.count,
          skip: ownerInstall.skip,
          pathsRecorded: false,
          why: "A multi-root setup being undefined rather than supported describes the machine this ships on, which is the fact the amendment needs and the fixture cannot show. The count carries it; the paths are personal and are not read out of the lift at all.",
          configFileUnchangedAcrossTheRun:
            fingerprintBefore.sha256 !== null &&
            fingerprintBefore.sha256 === fingerprintAfter.sha256,
          fingerprint: { before: fingerprintBefore, after: fingerprintAfter },
        },
      },
    };
  },
};
