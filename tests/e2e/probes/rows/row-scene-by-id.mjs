// Settles whether the pinned v3 build serves a scene by its own id, which is the call a consumer
// makes once it holds an id and nothing else.
//
// A scene is addressed as a MOVIE carrying `itemType: "scene"` — this generation publishes no
// `scene` area at all — so the route under test is the movie one.
//
// The 200 is not the answer. v3 answers an unmatched path with its single-page frontend and a 200,
// so a JSON body whose `id` is the one that was asked for is the weakest assertion that means
// anything here. The same call against an id nothing holds is recorded beside it, so the record
// states what an absence looks like on this build rather than leaving a reader to assume a 404.
const MOVIE_PATH = "/api/v3/movie";

// Above anything the history seed creates, so it addresses nothing on a freshly booted instance.
const ABSENT_ID = 987_654_321;

const JSON_CONTENT_TYPE = /^application\/(json|problem\+json)/i;

const observeResponse = (response) => ({
  status: response.status,
  contentType: response.contentType,
  isJson: JSON_CONTENT_TYPE.test(response.contentType),
});

/**
 * An id the instance itself reports holding, and where it came from.
 *
 * The history seed writes its parent library row first, so one exists; it is read back off the
 * instance rather than assumed, because an id this row invented would make every result below a
 * statement about a number instead of about the route.
 */
async function resolveSeededId(ctx, api) {
  const fromHistory = ctx.whisparr.v3.history?.readBack?.json?.records?.[0]?.movieId;
  if (typeof fromHistory === "number") {
    return { id: fromHistory, source: "history read-back", catalogue: null };
  }
  const listed = await api.get(MOVIE_PATH);
  const entries = Array.isArray(listed.json) ? listed.json : [];
  return {
    id: entries[0]?.id ?? null,
    source: `GET ${MOVIE_PATH}`,
    // This route pages nothing and its row count grows with the library, so it is recorded as a
    // length and one element.
    catalogue: {
      ...observeResponse(listed),
      length: entries.length,
      first: entries[0] === undefined ? null : { id: entries[0].id, itemType: entries[0].itemType },
    },
  };
}

export const row = {
  id: "row-scene-by-id",
  label:
    "Does the pinned v3 build answer GET /api/v3/movie/{id} with the entity that was asked for",
  requires: {
    // Whisparr containers join the Cove instance's own network, so a row asking for one asks for both.
    cove: true,
    whisparr: ["v3"],
    seedHistory: true,
    support: [],
    network: false,
    live: false,
  },
  async run(ctx) {
    const api = ctx.whisparr.apiFor("v3");
    const { id, source, catalogue } = await resolveSeededId(ctx, api);
    if (id === null) {
      throw new Error(
        `row-scene-by-id: neither the history read-back nor ${MOVIE_PATH} reported an entity to address, so there is nothing to fetch by id.`,
      );
    }

    const present = await api.get(`${MOVIE_PATH}/${id}`);
    const absent = await api.get(`${MOVIE_PATH}/${ABSENT_ID}`);
    const body = present.json;
    const isObject = body !== null && typeof body === "object" && !Array.isArray(body);
    const answered = observeResponse(present).isJson && isObject && body.id === id;

    return {
      method: {
        verb: "GET",
        path: `${MOVIE_PATH}/{id}`,
        inputs: { id, idSource: source, absentId: ABSENT_ID },
      },
      verdict: answered ? "available" : "unavailable",
      observed: {
        entityIdSource: source,
        catalogue,
        present: {
          ...observeResponse(present),
          requestedId: id,
          idMatchesRequest: isObject && body.id === id,
          itemType: isObject ? (body.itemType ?? null) : null,
          hasStashId: isObject && body.stashId !== undefined && body.stashId !== null,
          hasForeignId: isObject && body.foreignId !== undefined && body.foreignId !== null,
        },
        absent: {
          ...observeResponse(absent),
          requestedId: ABSENT_ID,
          bodyShape: Array.isArray(absent.json)
            ? "array"
            : absent.json !== null && typeof absent.json === "object"
              ? "object"
              : "non-json",
        },
      },
    };
  },
};
