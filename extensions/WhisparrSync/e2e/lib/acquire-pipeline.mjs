// Wires an indexer and a download client into a running Whisparr, and relaxes the two gates that
// would refuse a hermetic fixture.
//
// Everything goes through Whisparr's own /api/v3. The indexer and download-client hosts are the
// container aliases, which is what Whisparr can reach over the shared network — never a mapped host
// port, and never host.docker.internal.
//
// It also seeds the catalogue entry a grab lands on. A scene is one row on one generation and two
// on the other, and each addresses a release search by a different entity, so the seeding is a
// table here rather than a branch in the spec that grabs.
import { randomUUID } from "node:crypto";

import { pollUntil } from "@cove-extensions/e2e/poll";

import { dateSeededScene, SCENE_SITE, seedV2Scene } from "./seed-scene.mjs";

const CATALOGUE_BUDGET_MS = 60_000;

/**
 * Posts a schema-derived configuration, filling the named fields.
 *
 * Derived from the instance's own schema rather than written out here: each generation declares its
 * own field set, and a body assembled from a list in this file would be a second declaration of one
 * the instance already publishes.
 */
async function addFromSchema(whisparrApi, resource, implementation, overrides, fieldValues) {
  const schemas = await whisparrApi.get(`/api/v3/${resource}/schema`);
  const schema = (schemas.json ?? []).find((one) => one.implementation === implementation);
  if (!schema) {
    throw new Error(
      `provisionAcquirePipeline: this instance publishes no ${resource} schema for ${implementation}.`,
    );
  }

  const created = await whisparrApi.post(`/api/v3/${resource}?forceSave=true`, {
    ...schema,
    ...overrides,
    fields: schema.fields.map((field) =>
      field.name in fieldValues ? { ...field, value: fieldValues[field.name] } : field,
    ),
  });
  if (!created.ok) {
    throw new Error(
      `provisionAcquirePipeline: adding ${implementation} answered ${created.status}: ${created.text?.slice(0, 400)}`,
    );
  }
  return created.json;
}

/**
 * Relaxes the two gates that refuse a tiny fixture, so what a spec measures is the import rather than
 * a quality decision.
 *
 * Both were live blockers. `AcceptableSizeSpecification` reads an unknown runtime as a feature-length
 * one and expects gigabytes, so a sub-megabyte file is "too small"; every quality definition's floor
 * goes to zero and its ceiling is lifted. `DetectSample` reads a near-zero runtime as a sample and
 * holds the import in `importPending`; storing no media info leaves it no runtime to judge.
 */
async function relaxQualityGates(whisparrApi) {
  const media = await whisparrApi.get("/api/v3/config/mediamanagement");
  if (media.ok) {
    await whisparrApi.put("/api/v3/config/mediamanagement", {
      ...media.json,
      enableMediaInfo: false,
    });
  }

  const definitions = await whisparrApi.get("/api/v3/qualitydefinition");
  if (definitions.ok) {
    await whisparrApi.put(
      "/api/v3/qualitydefinition/update",
      (definitions.json ?? []).map((definition) => ({
        ...definition,
        minSize: 0,
        maxSize: null,
        preferredSize: null,
      })),
    );
  }
}

/**
 * Adds the Torznab indexer and the qBittorrent download client, and relaxes the quality gates.
 *
 * @param {{ whisparrApi: object, fakeIndexer: object, qbit: object }} options
 * @returns {Promise<{ indexerId: number, downloadClientId: number }>}
 */
export async function provisionAcquirePipeline({ whisparrApi, fakeIndexer, qbit }) {
  // enableInteractiveSearch must be true: the schema default is false, and an interactive search then
  // reports "0 active indexers". The categories match the caps mapping the stub publishes, or the
  // release is dropped as "no results in the configured categories".
  const indexer = await addFromSchema(
    whisparrApi,
    "indexer",
    "Torznab",
    {
      name: "FakeIndexer",
      enableRss: false,
      enableInteractiveSearch: true,
      enableAutomaticSearch: true,
      priority: 1,
    },
    {
      baseUrl: fakeIndexer.urlFromWhisparr,
      apiPath: fakeIndexer.apiPath,
      apiKey: "",
      categories: [6000, 2000],
    },
  );

  const client = await addFromSchema(
    whisparrApi,
    "downloadclient",
    "QBittorrent",
    { name: "qBittorrent", enable: true, priority: 1 },
    {
      host: "qbittorrent",
      port: 8080,
      useSsl: false,
      username: "admin",
      password: "",
      // Both spellings. The two generations name the same field for their own entity, and only the
      // one the schema declares is replaced, so the other is ignored rather than rejected.
      movieCategory: qbit.category,
      tvCategory: qbit.category,
    },
  );

  await relaxQualityGates(whisparrApi);

  return { indexerId: indexer.id, downloadClientId: client.id };
}

/**
 * How each generation is given one scene an indexer can answer for, and how a release for it is
 * asked for and grabbed.
 *
 * Seeded into the instance's datastore rather than added through its API: an add resolves the
 * foreign id against a hosted metadata service, which a sealed run does not reach.
 */
const ACQUIRABLE = {
  async v3({ whisparr, rootFolder, run }) {
    const instance = whisparr.apiFor("v3");
    const remoteId = randomUUID();
    await whisparr.seedEntity("v3", {
      kind: "scene",
      foreignId: remoteId,
      title: `Acquire ${run}`,
      rootFolderPath: rootFolder,
      monitored: true,
    });

    // The two facts this generation searches by. Without them the interactive search answers an
    // empty list having asked no indexer anything, which reads exactly like an indexer that is not
    // working.
    await dateSeededScene(whisparr.v3.container, "v3", remoteId);

    const rows = await pollUntil(
      async () => (await instance.get("/api/v3/movie")).json,
      (movies) => (movies ?? []).some((one) => one.foreignId === remoteId),
      { timeoutMs: CATALOGUE_BUDGET_MS, label: "the seeded scene is a row the instance lists" },
    );
    const movie = rows.find((one) => one.foreignId === remoteId);
    return {
      entryId: movie.id,
      folder: movie.path,
      releaseQuery: `movieId=${String(movie.id)}`,
      // The whole release resource back with the entity named on it. The controller parses the
      // title to find one when none is given, and refuses a release whose title it cannot map.
      // Naming it is what the interactive search does when a person picks a row.
      grabFields: { movieId: movie.id },
    };
  },

  async v2({ whisparr, rootFolder, run }) {
    const instance = whisparr.apiFor("v2");
    const seeded = await seedV2Scene(whisparr.v2.container, instance, {
      siteId: Math.floor(Math.random() * 1_000_000) + 1,
      siteTitle: SCENE_SITE,
      rootFolderPath: rootFolder,
      sceneExternalId: randomUUID(),
      sceneTitle: `Acquire ${run}`,
    });

    const rows = await pollUntil(
      async () => (await instance.get("/api/v3/series")).json,
      (sites) => (sites ?? []).some((one) => one.id === seeded.seriesId),
      { timeoutMs: CATALOGUE_BUDGET_MS, label: "the seeded site is a row the instance lists" },
    );
    const site = rows.find((one) => one.id === seeded.seriesId);
    return {
      entryId: seeded.seriesId,
      folder: site.path,
      releaseQuery: `episodeId=${String(seeded.episodeId)}`,
      grabFields: { episodeId: seeded.episodeId, seriesId: seeded.seriesId },
    };
  },
};

/**
 * Seeds one scene this generation's indexer query can answer for.
 *
 * @param {{ generation: "v2"|"v3", whisparr: object, rootFolder: string, run: string }} options
 * @returns {Promise<{ entryId: number, folder: string, releaseQuery: string, grabFields: object }>}
 */
export async function seedAcquirableScene({ generation, whisparr, rootFolder, run }) {
  if (!Object.hasOwn(ACQUIRABLE, generation)) {
    throw new Error(
      `seedAcquirableScene: no seed is written for the generation "${generation}"; written are ${Object.keys(ACQUIRABLE).join(", ")}.`,
    );
  }
  const seed = ACQUIRABLE[generation];
  return seed({ whisparr, rootFolder, run });
}
