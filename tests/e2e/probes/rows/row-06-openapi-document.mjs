// Settles whether each Whisparr generation publishes a contract about itself, and where.
//
// The content type is the whole of this row. A generation that serves its single-page frontend for
// every unmatched path answers several of the candidates below with HTML, and a status-only probe
// records all of them as documents. The candidate list therefore carries a path no build can serve,
// recorded beside the rest.
//
// A document that is found is saved beside the record, keyed by the build that served it, so runs
// against different images accumulate files a later phase can diff. The saved copy is local output
// and is not committed.
import { writeCompanion } from "../lib/record.mjs";

const CANDIDATES = [
  "/docs/v3/openapi.json",
  "/docs/openapi.json",
  "/docs/swagger.json",
  "/docs/v1/openapi.json",
  "/docs/index.html",
  "/docs",
  "/api-docs",
  "/swagger",
  // The control. Anything this answers, it answers to a path that cannot exist.
  "/zz-no-such-document-here",
];

const JSON_CONTENT_TYPE = /^application\/json/i;

/** Whether a response is a document: a 200, a JSON content type, and a body carrying openapi and paths. */
function isDocument(response) {
  return (
    response.status === 200 &&
    JSON_CONTENT_TYPE.test(response.contentType) &&
    typeof response.json?.openapi === "string" &&
    typeof response.json?.paths === "object"
  );
}

/** A filename carrying the build that served the document, reduced to what a filename may hold. */
function companionName(generation, version) {
  const build = String(version)
    .toLowerCase()
    .replaceAll(/[^a-z0-9.]/g, "-");
  return `openapi-${generation}-${build}.json`;
}

async function probeGeneration(ctx, generation) {
  const api = ctx.whisparr.apiFor(generation);
  const version = ctx.builds.whisparr[generation]?.version ?? "unknown";
  const candidates = [];
  let found = null;

  for (const path of CANDIDATES) {
    const response = await api.get(path);
    const document = isDocument(response);
    candidates.push({
      path,
      status: response.status,
      contentType: response.contentType,
      byteLength: response.text.length,
      isDocument: document,
      ...(path === CANDIDATES.at(-1) ? { control: "a path this build cannot serve" } : {}),
    });
    if (document && found === null) found = { path, response };
  }

  if (found === null) {
    return {
      image: ctx.builds.whisparr[generation]?.image ?? null,
      version,
      verdict: "none",
      candidates,
      candidatesTried: CANDIDATES.length,
      document: null,
    };
  }

  const savedTo =
    ctx.outDir === undefined
      ? null
      : writeCompanion(ctx.outDir, companionName(generation, version), found.response.text);

  return {
    image: ctx.builds.whisparr[generation]?.image ?? null,
    version,
    verdict: "document",
    candidates,
    candidatesTried: CANDIDATES.length,
    document: {
      path: found.path,
      openapi: found.response.json.openapi,
      infoTitle: found.response.json.info?.title ?? null,
      infoVersion: found.response.json.info?.version ?? null,
      pathCount: Object.keys(found.response.json.paths).length,
      schemaCount: Object.keys(found.response.json.components?.schemas ?? {}).length,
      byteLength: found.response.text.length,
      savedTo,
    },
  };
}

export const row = {
  id: "row-06-openapi-document",
  label: "Which Whisparr generation publishes an OpenAPI document about itself, and at which path",
  requires: {
    // Whisparr containers join the Cove instance's own network, so a row asking for one asks for both.
    cove: true,
    whisparr: ["v3", "v2"],
    seedHistory: false,
    support: [],
    network: false,
    live: false,
  },
  async run(ctx) {
    const generations = {};
    for (const generation of ctx.whisparr.generations) {
      generations[generation] = await probeGeneration(ctx, generation);
    }

    const documented = Object.entries(generations)
      .filter(([, observed]) => observed.verdict === "document")
      .map(([generation]) => generation);

    return {
      method: {
        verb: "GET",
        path: "each candidate below, on both generations",
        inputs: {
          candidates: CANDIDATES.length,
          note: "The client follows redirects, so a candidate that redirects is recorded as what it arrived at.",
        },
      },
      verdict: documented.length === 0 ? "none" : `documented-on-${documented.join(",")}`,
      observed: {
        criterion:
          "A candidate counts as a document only when it answers 200 with a JSON content type and parses into an object carrying openapi and paths. A 200 alone counts for nothing.",
        // How many candidates a status-only probe would have called present on each generation.
        // The control is among them.
        statusOnlyWouldMisreport: Object.fromEntries(
          Object.entries(generations).map(([generation, observed]) => [
            generation,
            observed.candidates.filter(
              (candidate) => candidate.status === 200 && !candidate.isDocument,
            ).length,
          ]),
        ),
        generations,
        savedDocuments: Object.values(generations)
          .map((observed) => observed.document?.savedTo)
          .filter((path) => path != null),
      },
    };
  },
};
