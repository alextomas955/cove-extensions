// Wires an indexer and a download client into a running Whisparr, and relaxes the two gates that
// would refuse a hermetic fixture.
//
// Everything goes through Whisparr's own /api/v3. The indexer and download-client hosts are the
// container aliases, which is what Whisparr can reach over the shared network — never a mapped host
// port, and never host.docker.internal.

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
