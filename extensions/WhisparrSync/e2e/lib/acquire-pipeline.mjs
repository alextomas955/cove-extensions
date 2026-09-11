// Wires an indexer and a download client into a running Whisparr, and relaxes the two gates that
// would refuse a hermetic fixture.
//
// Everything goes through Whisparr's own /api/v3. The indexer and download-client hosts are the
// container aliases, which is what Whisparr can reach over the shared network — never a mapped host
// port, and never host.docker.internal.
import { join } from "node:path";

/** The database each generation keeps its catalogue in, as the image lays it out. */
const DATABASES = { v3: "/config/whisparr3.db", v2: "/config/whisparr2.db" };

// A committed file copied in, never a string assembled in the shell. A script written through a
// heredoc carries CRLF into every path it handles, and the failure then blames the path.
const DATER_SOURCE = join(import.meta.dirname, "date-seeded-scene.py");
const DATER_TARGET = "/tmp/date-seeded-scene.py";

/** The app's own user, which a copied file has to be handed to before the app can run it. */
const APP_USER = "1000:1000";

/**
 * The date this suite's scenes are dated, and the site they belong to.
 *
 * One pair, because the indexer stub echoes whatever date the query carries and the instance matches
 * a release to a scene on the two together. Two specs disagreeing about either would each search for
 * something the other's stub does not answer.
 */
export const SCENE_RELEASE_DATE = "2025-01-01";
export const SCENE_SITE = "Tushy Raw";

/**
 * Gives a seeded scene the site and date its instance searches by.
 *
 * The shared entity seeder writes only the columns the schema declares NOT NULL, which leaves a scene
 * with neither. This generation builds its search query from those two and nothing else, so without
 * them the interactive search answers an empty list having asked no indexer anything.
 */
export async function dateSeededScene(container, generation, foreignId) {
  const database = DATABASES[generation];
  if (database === undefined) {
    throw new Error(`dateSeededScene: no database is declared for generation "${generation}".`);
  }

  await container.copyFilesToContainer([{ source: DATER_SOURCE, target: DATER_TARGET }]);
  await container.exec(["chown", APP_USER, DATER_TARGET], { user: "root" });

  // As the app's own user, so the write-ahead and shared-memory siblings it touches keep belonging to
  // the process that goes on using them.
  const written = await container.exec(
    [
      "python3",
      DATER_TARGET,
      "--db",
      database,
      "--foreign-id",
      foreignId,
      "--release-date",
      SCENE_RELEASE_DATE,
      "--studio-title",
      SCENE_SITE,
    ],
    { user: APP_USER },
  );
  if (written.exitCode !== 0) {
    throw new Error(`dateSeededScene: ${written.output.trim()}`);
  }

  // The columns it set and the columns the build declares. A caller prints this when an import went
  // unannounced: a name from the other lineage is skipped rather than written, and a skipped column
  // looks exactly like one that was written.
  return written.output.trim();
}

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
      movieCategory: qbit.category,
    },
  );

  await relaxQualityGates(whisparrApi);

  return { indexerId: indexer.id, downloadClientId: client.id };
}
