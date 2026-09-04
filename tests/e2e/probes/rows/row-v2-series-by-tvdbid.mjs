// Settles whether the pinned v2 build filters GET /api/v3/series by the entity id, or accepts the
// parameter and answers with its whole catalogue anyway.
//
// This generation publishes no contract, so the question cannot be read off a document (GAP-2). It
// also cannot be inferred from the newer generation's: this phase already measured one query-shaped
// trap on this exact build, where `tpdb:<uuid>` answers 200 with an empty array while a bare uuid
// resolves, so the more-correct-looking form failed with no error at all. A parameter that is simply
// ignored is the same failure wearing different clothes, and it is the one a consumer cannot see: the
// answer parses, the entry is found, and the read still carries the whole catalogue.
//
// The load-bearing half is the absent id, not the held one. A one-entity instance answers a filtered
// and an unfiltered read identically, so an assertion made against the held id alone reports an
// ignored parameter as honoured. The read for an id the instance holds nothing under is what tells
// them apart: a filtering build answers it empty, and an ignoring one answers it with everything.
const SERIES_PATH = "/api/v3/series";
const SERIES_LOOKUP_PATH = "/api/v3/series/lookup";
const QUALITY_PROFILE_PATH = "/api/v3/qualityprofile";

// The field this generation answers an entity's numeric id in. It is misnamed after an unrelated
// television database and names no such thing here.
const ENTITY_ID_QUERY = "tvdbId";

// A ThePornDB uuid the owner's own Cove holds, read out of `studio_remote_ids` for the studio named
// "Vixen" under the `https://theporndb.net/graphql` endpoint on 2026-09-04, rather than invented. It
// is one of the four D-07 recorded resolving to exactly one site on this build, and the site it
// names is `tvdbId` 3372. The bare uuid is the term: `tpdb:<uuid>` answers 200 with an empty array.
const HELD_ENTITY_UUID = "1dafafd3-da8f-47f3-aca2-e6bb9f354292";

// Above anything a freshly booted instance holds, and above anything the add below creates.
const ABSENT_ENTITY_ID = 987_654_321;

const JSON_CONTENT_TYPE = /^application\/(json|problem\+json)/i;

const observeResponse = (response) => ({
  status: response.status,
  contentType: response.contentType,
  isJson: JSON_CONTENT_TYPE.test(response.contentType),
});

/** What one listing answer was, described so a reader can tell a narrowed read from a whole one. */
function observeListing(response) {
  const entries = Array.isArray(response.json) ? response.json : [];
  return {
    ...observeResponse(response),
    isArray: Array.isArray(response.json),
    length: entries.length,
    entityIds: entries.map((entry) => entry?.[ENTITY_ID_QUERY] ?? null),
  };
}

/**
 * The entity a term names on this generation, or a refusal to record instead of an entity.
 *
 * The answer never echoes the term, so exactly one result is the whole of what the correspondence
 * rests on and more than one is refused rather than picked from.
 */
async function resolveEntity(api) {
  const lookup = await api.get(
    `${SERIES_LOOKUP_PATH}?term=${encodeURIComponent(HELD_ENTITY_UUID)}`,
  );
  const answered = Array.isArray(lookup.json) ? lookup.json : [];
  const site = answered.length === 1 ? answered[0] : null;
  return {
    observed: {
      ...observeResponse(lookup),
      term: HELD_ENTITY_UUID,
      length: answered.length,
      resolved:
        site === null
          ? null
          : {
              entityId: site[ENTITY_ID_QUERY] ?? null,
              title: site.title ?? null,
              titleSlug: site.titleSlug ?? null,
            },
    },
    site,
  };
}

/**
 * The add body, composed field for field the way `V2BodyProjector.AddStudio` composes one.
 *
 * Both acquisition-suppressing spellings are present and false, so this measurement cannot make the
 * container download anything. Omission is not relied on: an absent flag's default belongs to the
 * instance and was never measured.
 */
const addBodyFor = ({ entityId, title, titleSlug, qualityProfileId, rootFolderPath }) => ({
  tvdbId: entityId,
  title,
  titleSlug,
  qualityProfileId,
  rootFolderPath,
  monitored: true,
  monitorNewItems: "all",
  seriesType: "standard",
  seasons: [],
  tags: [],
  addOptions: {
    monitor: "all",
    searchForMissingEpisodes: false,
    searchForCutoffUnmetEpisodes: false,
  },
});

export const row = {
  id: "row-v2-series-by-tvdbid",
  label:
    "Does the pinned v2 build filter GET /api/v3/series by tvdbId, or ignore the parameter and answer with its whole catalogue",
  requires: {
    // Whisparr containers join the Cove instance's own network, so a row asking for one asks for both.
    cove: true,
    whisparr: ["v2"],
    seedHistory: false,
    rootFolder: true,
    support: [],
    // This generation resolves an identifier through the vendor's own metadata service, so the
    // lookup below cannot answer offline.
    network: true,
    live: false,
  },
  async run(ctx) {
    const api = ctx.whisparr.apiFor("v2");
    const rootFolderPath = ctx.whisparr.v2.rootFolder;

    const lookup = await resolveEntity(api);
    if (lookup.site === null) {
      throw new Error(
        `row-v2-series-by-tvdbid: the lookup for ${HELD_ENTITY_UUID} answered ${lookup.observed.length} result(s) at HTTP ${lookup.observed.status}, so no entity was named and there is nothing to hold or to filter for.`,
      );
    }

    const profiles = await api.get(QUALITY_PROFILE_PATH);
    const qualityProfileId = (Array.isArray(profiles.json) ? profiles.json : [])[0]?.id ?? null;
    if (qualityProfileId === null) {
      throw new Error(
        `row-v2-series-by-tvdbid: ${QUALITY_PROFILE_PATH} offered no profile at HTTP ${profiles.status}, and this generation refuses an add carrying a zero profile with a validation failure.`,
      );
    }

    const entityId = lookup.site[ENTITY_ID_QUERY];
    const added = await api.post(
      SERIES_PATH,
      addBodyFor({
        entityId,
        title: lookup.site.title,
        titleSlug: lookup.site.titleSlug,
        qualityProfileId,
        rootFolderPath,
      }),
    );

    const unfiltered = await api.get(SERIES_PATH);
    const held = await api.get(`${SERIES_PATH}?${ENTITY_ID_QUERY}=${entityId}`);
    const absent = await api.get(`${SERIES_PATH}?${ENTITY_ID_QUERY}=${ABSENT_ENTITY_ID}`);

    const heldEntries = Array.isArray(held.json) ? held.json : [];
    const absentEntries = Array.isArray(absent.json) ? absent.json : [];
    // Never computed from the unfiltered length: a one-entity instance makes the two answers
    // identical, which is why the absent read is here and is the half that decides this.
    const filters =
      heldEntries.length === 1 &&
      heldEntries[0]?.[ENTITY_ID_QUERY] === entityId &&
      absentEntries.length === 0;

    return {
      method: {
        verb: "GET",
        path: `${SERIES_PATH}?${ENTITY_ID_QUERY}={id}`,
        inputs: {
          term: HELD_ENTITY_UUID,
          entityId,
          absentEntityId: ABSENT_ENTITY_ID,
          rootFolderPath,
          qualityProfileId,
        },
      },
      verdict: filters ? "available" : "unavailable",
      observed: {
        // The measured answer, as one boolean. `observed` is where a row's own facts go: the record
        // shape `probes/lib/record.mjs` writes carries provenance, the method, the verdict and this,
        // and drops any other key a row returns.
        filters,
        lookup: lookup.observed,
        add: {
          ...observeResponse(added),
          entityId: added.json?.id ?? null,
          requestedEntityId: entityId,
          answeredEntityId: added.json?.[ENTITY_ID_QUERY] ?? null,
        },
        unfiltered: observeListing(unfiltered),
        held: {
          ...observeListing(held),
          requestedEntityId: entityId,
          bodyText: held.text,
        },
        absent: {
          ...observeListing(absent),
          requestedEntityId: ABSENT_ENTITY_ID,
          bodyText: absent.text,
        },
      },
    };
  },
};
