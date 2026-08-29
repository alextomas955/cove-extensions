// Measures the paging, ceiling and ordering behaviour of every metadata provider this machine's Cove
// install is configured with, so a later phase sizes its reads against what the servers enforce
// rather than against what their documents claim.
//
// A ceiling is recorded as the size REQUESTED beside the number of items RETURNED. One request at
// one size cannot tell a cap from a coincidence, so each surface is asked below, at and above the
// ceiling its own document or the spec expects, and a ceiling never reached is recorded as a lower
// bound rather than rounded up into an answer.
//
// Nothing a provider returned is kept as a value. A catalogue here reaches millions of entries, so
// the record carries counts, the total the provider reported with the field it came from, and the
// field NAMES of one element — taken from each surface's own schema document, which needs no result
// fetched at all.
import { liftMetadataServers } from "../../lib/cove-providers.mjs";
import { writeCompanion } from "../lib/record.mjs";
import { bounded, liftProvider, pacer } from "./row-07-tpdb-rest-token.mjs";

// The provider the spec models over REST. Every other configured provider is a stash-box instance
// answering GraphQL at the endpoint it is configured with.
const REST_PROVIDER_NAME = "ThePornDB";
const REST_ORIGIN = "https://api.theporndb.net";

const REQUEST_TIMEOUT_MS = 30_000;

// Below, at, and above the ceiling each surface is expected to enforce. The REST ladder stops lower
// because that surface returns whole entries and cannot be asked for fewer fields, so an uncapped
// request there is paid for in payload.
const GRAPHQL_PAGE_SIZES = [25, 100, 1000];
const REST_PAGE_SIZES = [25, 100, 200];

// The per_page ceiling the spec records for a stash-box surface, and what a gap is measured against.
const STASH_BOX_EXPECTED_CEILING = 100;

// Where the page past the end goes when the surface reported no total to derive one from.
const BEYOND_THE_END_PAGE = 1_000_000;

const UNSUPPORTED_SORT = "NOT_A_VALID_SORT_VALUE";
const UNSUPPORTED_ORDER_BY = "not_a_valid_order_by";

// A value read out of a provider's own schema is interpolated into the next query, so it is held to
// the shape a GraphQL enum name may have before it goes anywhere.
const GRAPHQL_ENUM_NAME = /^[A-Za-z_][A-Za-z0-9_]*$/;

// A provider's error text is worth keeping — it is often where a rejected value says what the
// accepted ones are — but a record is read by a person.
const MAX_MESSAGE_CHARS = 200;

const trimMessage = (text) =>
  typeof text === "string" && text.length > 0 ? text.slice(0, MAX_MESSAGE_CHARS) : null;

/**
 * One HTTP call, paced, with an unreachable host named rather than swallowed.
 *
 * A host this machine cannot reach must not read as a provider that answered something, which is the
 * failure the reachability assumption behind this row would otherwise produce silently.
 */
async function request(url, { method = "GET", headers, body, pace, label }) {
  await pace.wait();
  let response;
  try {
    response = await fetch(url, {
      method,
      headers,
      body,
      signal: AbortSignal.timeout(REQUEST_TIMEOUT_MS),
    });
  } catch (cause) {
    throw new Error(`${new URL(url).host} could not be reached for ${label}: ${cause.message}`, {
      cause,
    });
  }

  const text = await response.text();
  let json;
  try {
    json = JSON.parse(text);
  } catch {
    json = undefined;
  }
  return {
    status: response.status,
    contentType: response.headers.get("content-type") ?? "",
    retryAfter: response.headers.get("retry-after"),
    rateLimitRemaining: response.headers.get("x-ratelimit-remaining"),
    json,
    text,
  };
}

/**
 * The page that lies past the end of what the surface itself reported, at one item to a page.
 *
 * Derived from the total rather than fixed, because a page number large enough to sit past one
 * catalogue sits inside a larger one, and a page that landed INSIDE the catalogue would be recorded
 * as a page past the end that returned items.
 */
const pageBeyondTheEnd = (reportedTotal) =>
  typeof reportedTotal === "number" && reportedTotal > 0 ? reportedTotal + 2 : BEYOND_THE_END_PAGE;

/** Every rate-limit refusal the run met, which is the only evidence a configured rate is too fast. */
function rateLimitNote(answer, label) {
  return answer.status === 429
    ? { label, status: answer.status, retryAfter: answer.retryAfter }
    : null;
}

// ---- stash-box GraphQL ----

const graphqlBody = (query) => JSON.stringify({ query });

const scenePageQuery = (fields) =>
  `query ProbePage { queryScenes(input: { ${fields} }) { count scenes { id } } }`;

const SCHEMA_QUERY = `query ProbeSchema {
  queryType: __type(name: "Query") { fields { name } }
  sceneInput: __type(name: "SceneQueryInput") { inputFields { name } }
  sceneSort: __type(name: "SceneSortEnum") { enumValues { name } }
  sortDirection: __type(name: "SortDirectionEnum") { enumValues { name } }
  scene: __type(name: "Scene") { fields { name } }
}`;

const graphqlErrors = (answer) =>
  Array.isArray(answer.json?.errors)
    ? answer.json.errors.map((error) => trimMessage(error?.message)).filter(Boolean)
    : [];

async function graphqlCall(endpoint, query, { apiKey, pace, label }) {
  return request(endpoint, {
    method: "POST",
    headers: { "Content-Type": "application/json", ApiKey: apiKey },
    body: graphqlBody(query),
    pace,
    label,
  });
}

/** What the endpoint's own schema says about scene paging, ordering and the shape of one scene. */
function readSchema(answer) {
  const data = answer.json?.data ?? {};
  const names = (list) =>
    Array.isArray(list) ? list.map((item) => item?.name).filter(Boolean) : [];
  const queryFields = names(data.queryType?.fields);
  const inputFields = names(data.sceneInput?.inputFields);
  return {
    httpStatus: answer.status,
    errors: bounded(graphqlErrors(answer)),
    servesQueryScenes: queryFields.includes("queryScenes"),
    queryFieldCount: queryFields.length,
    sceneInputFields: bounded(inputFields),
    paging: {
      mechanism: inputFields.includes("page") ? "page number" : "not a page number",
      hasPage: inputFields.includes("page"),
      hasPerPage: inputFields.includes("per_page"),
      hasCursor: inputFields.some((field) => /cursor|after|before/i.test(field)),
    },
    sortValues: bounded(names(data.sceneSort?.enumValues)),
    directionValues: bounded(names(data.sortDirection?.enumValues)),
    // The field names of one element, taken from the schema so no result has to be fetched to learn
    // them and no provider value enters the process.
    representativeElement: { type: "Scene", fields: bounded(names(data.scene?.fields)) },
  };
}

async function observeGraphqlPage(endpoint, { apiKey, pace, page, perPage, sort, direction }) {
  const parts = [`page: ${page}`, `per_page: ${perPage}`];
  if (sort !== undefined) parts.push(`sort: ${sort}`);
  if (direction !== undefined) parts.push(`direction: ${direction}`);
  const label = `queryScenes page ${page} per_page ${perPage}`;
  const answer = await graphqlCall(endpoint, scenePageQuery(parts.join(", ")), {
    apiKey,
    pace,
    label,
  });
  const result = answer.json?.data?.queryScenes;
  const scenes = Array.isArray(result?.scenes) ? result.scenes : null;
  return {
    label,
    requestedPage: page,
    requestedPerPage: perPage,
    ...(sort === undefined ? {} : { requestedSort: sort, requestedDirection: direction ?? null }),
    httpStatus: answer.status,
    returned: scenes === null ? null : scenes.length,
    reportedTotal: typeof result?.count === "number" ? result.count : null,
    totalField: "queryScenes.count",
    errors: bounded(graphqlErrors(answer)),
    rateLimited: rateLimitNote(answer, label),
  };
}

async function probeStashBox(entry, pace) {
  const endpoint = entry.endpoint;
  const apiKey = entry.apiKey;
  const schema = readSchema(
    await graphqlCall(endpoint, SCHEMA_QUERY, { apiKey, pace, label: "schema introspection" }),
  );

  if (!schema.servesQueryScenes) {
    return {
      kind: "stash-box GraphQL",
      endpoint,
      schema,
      verdict: "inconclusive",
      notObservable: `${endpoint} declares no queryScenes field, so paging, ceiling and ordering were not asked of it.`,
    };
  }

  const ceiling = [];
  for (const perPage of GRAPHQL_PAGE_SIZES) {
    ceiling.push(await observeGraphqlPage(endpoint, { apiKey, pace, page: 1, perPage }));
  }

  const reportedTotal = ceiling[0]?.reportedTotal ?? null;
  const beyondTheEnd = await observeGraphqlPage(endpoint, {
    apiKey,
    pace,
    page: pageBeyondTheEnd(reportedTotal),
    perPage: 1,
  });

  const acceptedSort = schema.sortValues.values.find((value) => GRAPHQL_ENUM_NAME.test(value));
  const acceptedDirection = schema.directionValues.values.find((value) =>
    GRAPHQL_ENUM_NAME.test(value),
  );
  const supportedSort =
    acceptedSort === undefined
      ? null
      : await observeGraphqlPage(endpoint, {
          apiKey,
          pace,
          page: 1,
          perPage: 1,
          sort: acceptedSort,
          direction: acceptedDirection,
        });
  const unsupportedSort = await observeGraphqlPage(endpoint, {
    apiKey,
    pace,
    page: 1,
    perPage: 1,
    sort: UNSUPPORTED_SORT,
  });

  return {
    kind: "stash-box GraphQL",
    endpoint,
    schema,
    ceiling: {
      declared: null,
      declaredBy: "this surface publishes no per_page maximum in its schema",
      expectedBySpec: STASH_BOX_EXPECTED_CEILING,
      observations: ceiling,
      ...summariseCeiling(ceiling),
    },
    paging: {
      mechanism: schema.paging.mechanism,
      reportedTotal,
      totalField: "queryScenes.count",
      beyondTheEnd: {
        ...beyondTheEnd,
        behaviour: describeBeyondTheEnd(beyondTheEnd),
      },
      ...judgeReportedTotal(reportedTotal, beyondTheEnd),
    },
    ordering: {
      sortValues: schema.sortValues,
      directionValues: schema.directionValues,
      supportedSort,
      unsupportedSort: {
        ...unsupportedSort,
        // A rejection is the safe answer. A value accepted and then ignored is the dangerous one,
        // and it only shows up because the row asked for a value that cannot be honoured.
        behaviour: unsupportedSort.errors.count > 0 ? "rejected" : "accepted and ignored",
      },
    },
  };
}

// ---- ThePornDB REST ----

const restUrl = (path) => `${REST_ORIGIN}${path}`;

/** The `/scenes` contract as the provider's own document declares it. */
function readOpenApi(document) {
  const deref = (node) =>
    node?.$ref
      ? node.$ref
          .split("/")
          .slice(1)
          .reduce((held, key) => held?.[key], document)
      : node;
  const parameters = (document?.paths?.["/scenes"]?.get?.parameters ?? []).map(deref);
  const byName = (name) => parameters.find((parameter) => parameter?.name === name);
  const perPage = deref(byName("per_page")?.schema);
  const page = deref(byName("page")?.schema);
  const orderBy = deref(byName("orderBy")?.schema);
  const names = parameters.map((parameter) => parameter?.name).filter(Boolean);
  return {
    version: typeof document?.info?.version === "string" ? document.info.version : null,
    parameterCount: names.length,
    parameterNames: bounded(names),
    declaresYearFilter: names.includes("year"),
    declaresOrderBy: names.includes("orderBy"),
    perPage: { maximum: perPage?.maximum ?? null, minimum: perPage?.minimum ?? null },
    page: { minimum: page?.minimum ?? null, default: page?.default ?? null },
    orderByValues: bounded(Array.isArray(orderBy?.enum) ? orderBy.enum : []),
  };
}

async function observeRestPage({ token, pace, page, perPage, orderBy }) {
  const query =
    `page=${page}&per_page=${perPage}` +
    (orderBy === undefined ? "" : `&orderBy=${encodeURIComponent(orderBy)}`);
  const label = `scenes page ${page} per_page ${perPage}${orderBy === undefined ? "" : ` orderBy ${orderBy}`}`;
  const answer = await request(restUrl(`/scenes?${query}`), {
    headers: { Accept: "application/json", Authorization: `Bearer ${token}` },
    pace,
    label,
  });
  const data = Array.isArray(answer.json?.data) ? answer.json.data : null;
  const meta = answer.json?.meta;
  return {
    label,
    query,
    requestedPage: page,
    requestedPerPage: perPage,
    ...(orderBy === undefined ? {} : { requestedOrderBy: orderBy }),
    httpStatus: answer.status,
    returned: data === null ? null : data.length,
    reportedTotal: typeof meta?.total === "number" ? meta.total : null,
    totalField: "meta.total",
    serverPerPage: meta?.per_page ?? null,
    lastPage: typeof meta?.last_page === "number" ? meta.last_page : null,
    metaFields: bounded(meta !== null && typeof meta === "object" ? Object.keys(meta) : []),
    // Held for a comparison and never recorded: whether the first item moved is what tells a sort
    // that was honoured from one that was accepted and dropped.
    firstId: data?.[0]?.id ?? null,
    elementFields: bounded(
      data?.[0] !== null && typeof data?.[0] === "object" ? Object.keys(data[0]) : [],
    ),
    error: trimMessage(answer.json?.message ?? (answer.status >= 400 ? answer.text : null)),
    rateLimited: rateLimitNote(answer, label),
  };
}

// Held for a comparison or summarised elsewhere, and stripped here so neither reaches the record by
// being forgotten.
const HELD_OUT_OF_RECORD = new Set(["firstId", "elementFields"]);

const forRecord = (observation) =>
  observation === null
    ? null
    : Object.fromEntries(
        Object.entries(observation).filter(([field]) => !HELD_OUT_OF_RECORD.has(field)),
      );

async function probeRest(entry, pace, outDir) {
  const token = entry.apiKey;
  const documentAnswer = await request(restUrl("/openapi.json"), {
    headers: { Accept: "application/json" },
    pace,
    label: "openapi document",
  });
  const declared =
    documentAnswer.json === undefined
      ? { version: null, note: `the document answered ${documentAnswer.status} and did not parse` }
      : readOpenApi(documentAnswer.json);

  // The document is the provider's own contract and is far too large for a record, so it is kept
  // beside one, keyed by the version it reported so two runs leave two files rather than one.
  const companionName =
    declared.version === null
      ? "theporndb-openapi.json"
      : `theporndb-openapi-${declared.version.toLowerCase().replaceAll(/[^a-z0-9._-]/g, "-")}.json`;
  const companion =
    outDir === undefined || documentAnswer.json === undefined
      ? null
      : writeCompanion(outDir, companionName, documentAnswer.text);

  const ceiling = [];
  for (const perPage of REST_PAGE_SIZES) {
    ceiling.push(await observeRestPage({ token, pace, page: 1, perPage }));
  }

  const reportedTotal = ceiling[0]?.reportedTotal ?? null;
  const beyondTheEnd = await observeRestPage({
    token,
    pace,
    page: pageBeyondTheEnd(reportedTotal),
    perPage: 1,
  });

  const acceptedOrderBy = declared.orderByValues?.values?.[0];
  const supportedSort =
    acceptedOrderBy === undefined
      ? null
      : await observeRestPage({ token, pace, page: 1, perPage: 1, orderBy: acceptedOrderBy });
  const unsupportedSort = await observeRestPage({
    token,
    pace,
    page: 1,
    perPage: 1,
    orderBy: UNSUPPORTED_ORDER_BY,
  });

  const baselineFirstId = ceiling[0]?.firstId ?? null;
  const unsupportedRejected = unsupportedSort.httpStatus >= 400;

  return {
    kind: "REST",
    endpoint: REST_ORIGIN,
    declared,
    declaredDocument: { companion, name: companionName, httpStatus: documentAnswer.status },
    ceiling: {
      declared: declared.perPage?.maximum ?? null,
      declaredBy: "the provider's own openapi document, per_page.maximum",
      expectedBySpec: declared.perPage?.maximum ?? null,
      observations: ceiling.map(forRecord),
      ...summariseCeiling(ceiling),
    },
    paging: {
      mechanism: "page number",
      reportedTotal,
      totalField: "meta.total",
      beyondTheEnd: {
        ...forRecord(beyondTheEnd),
        behaviour: describeBeyondTheEnd(beyondTheEnd),
      },
      ...judgeReportedTotal(reportedTotal, beyondTheEnd),
    },
    ordering: {
      sortValues: declared.orderByValues ?? bounded([]),
      sortValuesFrom: "the provider's own openapi document, orderBy enum",
      directionValues: bounded([]),
      directionNote:
        "this surface carries direction inside each orderBy value rather than as its own parameter",
      supportedSort: forRecord(supportedSort),
      supportedSortChangedFirstItem:
        supportedSort === null || baselineFirstId === null
          ? null
          : supportedSort.firstId !== baselineFirstId,
      unsupportedSort: {
        ...forRecord(unsupportedSort),
        behaviour: unsupportedRejected
          ? "rejected"
          : unsupportedSort.firstId === baselineFirstId
            ? "accepted and ignored"
            : "accepted and honoured as something",
      },
    },
    representativeElement: { type: "scene", fields: ceiling[0]?.elementFields ?? bounded([]) },
  };
}

// ---- Shared judgment ----

/**
 * What the ladder settled: the largest page the server actually returned, and whether that number is
 * a cap it enforced or only the largest size the run thought to ask for.
 *
 * A surface enforces a ceiling in one of two ways, and the difference matters to a caller: it can
 * TRUNCATE, answering a too-large request with a smaller page, or it can REFUSE, answering with an
 * error and no page at all. A ladder whose top step came back whole has found neither, so what it
 * found is a floor under the ceiling and is recorded as one.
 */
function summariseCeiling(observations) {
  const answered = observations.filter((observation) => typeof observation.returned === "number");
  const largestReturned = answered.reduce(
    (highest, observation) => Math.max(highest, observation.returned),
    0,
  );
  const largestRequested = observations.reduce(
    (highest, observation) => Math.max(highest, observation.requestedPerPage ?? 0),
    0,
  );
  const truncated = answered.some(
    (observation) => (observation.requestedPerPage ?? 0) > observation.returned,
  );
  const refused = observations.some(
    (observation) =>
      observation.returned === null && (observation.requestedPerPage ?? 0) > largestReturned,
  );
  const enforcement = truncated ? "truncates" : refused ? "refuses" : "not reached";
  return {
    largestRequested,
    largestReturned,
    enforcement,
    enforcedCeiling: enforcement === "not reached" ? null : largestReturned,
    lowerBound: enforcement === "not reached" ? largestReturned : null,
  };
}

function describeBeyondTheEnd(observation) {
  if (observation.httpStatus >= 400) return "errors";
  if (observation.errors?.count > 0) return "errors";
  if (observation.returned === 0) return "returns empty";
  return observation.returned === null ? "no page in the answer" : "returns items";
}

/**
 * Whether the number a surface reports as its total is the end of what it will serve.
 *
 * A surface that still serves items on a page past its own total has reported a lower bound rather
 * than a count, and the two are rendered differently: one is a figure, the other is a figure with a
 * plus after it.
 */
function judgeReportedTotal(reportedTotal, beyondTheEnd) {
  if (typeof reportedTotal !== "number") {
    return { itemsBeyondReportedTotal: null, reportedTotalReads: "the surface reported no total" };
  }
  if (beyondTheEnd.returned === null) {
    return {
      itemsBeyondReportedTotal: null,
      reportedTotalReads: "unsettled: the page past the total did not answer with a page",
    };
  }
  return beyondTheEnd.returned > 0
    ? {
        itemsBeyondReportedTotal: true,
        reportedTotalReads:
          "a lower bound: the surface served items on a page past the point its own total ends at",
      }
    : {
        itemsBeyondReportedTotal: false,
        reportedTotalReads: "the end of what the surface serves",
      };
}

/**
 * A surface's verdict, and the gap candidates it earns.
 *
 * A ceiling that differs from what the surface's own document or the spec expects, a total that is
 * not the end of what the surface serves, and a sort value accepted without being honoured are all
 * facts a later phase would otherwise design around without knowing.
 */
function judgeSurface(surface, name, gapNumber) {
  if (surface.verdict !== undefined) return { ...surface, gapCandidates: [] };

  const gapCandidates = [];
  const expected = surface.ceiling.expectedBySpec;
  const enforced = surface.ceiling.enforcedCeiling;
  const lowerBound = surface.ceiling.lowerBound;
  const observedDate = new Date().toISOString().slice(0, 10);
  const recheckTrigger = `a change to what ${surface.endpoint} enforces; the row is re-answered by re-running it with --live.`;
  const observation = { date: observedDate, provider: name, endpoint: surface.endpoint };

  const ceilingBelowExpected = expected !== null && enforced !== null && enforced !== expected;
  const ceilingAboveExpected = expected !== null && lowerBound !== null && lowerBound > expected;

  if (ceilingBelowExpected || ceilingAboveExpected) {
    const bound = ceilingBelowExpected ? enforced : lowerBound;
    gapCandidates.push({
      id: `GAP-${gapNumber + gapCandidates.length}`,
      observed: observation,
      axis: "configuration",
      surface: `${surface.endpoint} scene paging, per_page`,
      expected: `a page size ceiling of ${expected}`,
      observedBehaviour: ceilingBelowExpected
        ? `requesting ${surface.ceiling.largestRequested} returned ${enforced}; the surface ${surface.ceiling.enforcement} above that.`
        : `requesting ${surface.ceiling.largestRequested} returned ${lowerBound} whole, so the ceiling was never reached and ${lowerBound} is a lower bound under it.`,
      blastRadius:
        "any later read that sizes its pages from the expected ceiling, and any count acceptance derived from a page it assumed it received whole.",
      acceptanceAdjustment: `Bound a page request to ${bound} and assert that what returns is no larger than what was asked for, rather than asserting the expected size arrives.`,
      recheckTrigger,
      axisNote:
        "configuration, not generation: the ceiling belongs to this deployment of the provider and can move without the surface changing.",
    });
  }

  if (surface.paging?.itemsBeyondReportedTotal === true) {
    gapCandidates.push({
      id: `GAP-${gapNumber + gapCandidates.length}`,
      observed: observation,
      axis: "configuration",
      surface: `${surface.endpoint} scene paging, ${surface.paging.totalField}`,
      expected: "the total a surface reports is the count of what it will serve.",
      observedBehaviour: `the surface reported ${surface.paging.reportedTotal} and then served items on a page past that point.`,
      blastRadius:
        "any figure rendered from the reported total, and any paged read that stops when it reaches it.",
      acceptanceAdjustment: `Treat ${surface.paging.reportedTotal} as a lower bound, render it as one, and bound a paged read by an empty page rather than by the reported total.`,
      recheckTrigger,
      axisNote:
        "configuration, not generation: the point a surface stops counting at is a deployment setting and moves without its API changing.",
    });
  }

  if (surface.ordering?.unsupportedSort?.behaviour === "accepted and ignored") {
    gapCandidates.push({
      id: `GAP-${gapNumber + gapCandidates.length}`,
      observed: observation,
      axis: "configuration",
      surface: `${surface.endpoint} scene ordering, an undeclared sort value`,
      expected: "a sort value the surface does not accept is refused.",
      observedBehaviour:
        "the request succeeded and returned the same first item as an unsorted one, so the value was dropped without being reported.",
      blastRadius:
        "any later read that relies on an ordering it asked for, including a paged read whose page boundaries assume a stable order.",
      acceptanceAdjustment:
        "Assert ordering from the returned sequence rather than from the request being accepted, and bound a paged read to the sort values the surface declares.",
      recheckTrigger,
      axisNote:
        "configuration, not generation: a surface that starts refusing the value later changes this row without changing the API it serves.",
    });
  }

  const answered = surface.ceiling.observations.some(
    (observation) => typeof observation.returned === "number",
  );
  return {
    ...surface,
    verdict: answered ? "measured" : "inconclusive",
    ...(answered
      ? {}
      : {
          notObservable: `no page request against ${surface.endpoint} returned a page, so its ceiling, paging and ordering are unsettled.`,
        }),
    gapCandidates,
  };
}

export const row = {
  id: "row-08-provider-paging",
  label: "Paging, ceilings, ordering and the configured rate, per configured metadata provider",
  requires: {
    cove: false,
    whisparr: [],
    seedHistory: false,
    support: [],
    rootFolder: false,
    network: true,
    // Reaches services this project does not own, with real personal credentials.
    live: true,
  },
  async run(ctx) {
    const configured = liftMetadataServers();
    const surfaces = {};
    const skipped = [];
    const paces = [];
    let gapNumber = 4;

    for (const declared of configured.servers) {
      const name = typeof declared?.name === "string" ? declared.name : "<unnamed>";
      const { entry, skip } = liftProvider(name);
      if (skip !== null) {
        skipped.push(skip);
        continue;
      }

      const pace = pacer({
        requestsPerMinute: entry.maxRequestsPerMinute,
        label: `${name} as this install configures it`,
      });
      const measured =
        name.toLowerCase() === REST_PROVIDER_NAME.toLowerCase()
          ? await probeRest(entry, pace, ctx.outDir)
          : await probeStashBox(entry, pace);

      const judged = judgeSurface(measured, name, gapNumber);
      gapNumber += judged.gapCandidates.length;
      surfaces[name] = {
        ...judged,
        configuredRequestsPerMinute: entry.maxRequestsPerMinute ?? null,
        pace: pace.report(),
      };
      paces.push(pace.report());
    }

    if (configured.skip !== null) skipped.push(configured.skip);

    const verdicts = Object.values(surfaces).map((surface) => surface.verdict);
    return {
      method: {
        verb: "GET and POST",
        path: "each configured metadata server's own endpoint",
        inputs: {
          graphqlPageSizes: GRAPHQL_PAGE_SIZES,
          restPageSizes: REST_PAGE_SIZES,
          beyondTheEndPage: BEYOND_THE_END_PAGE,
        },
      },
      verdict:
        verdicts.length === 0
          ? "skipped"
          : verdicts.every((verdict) => verdict === "measured")
            ? "measured"
            : "partially-measured",
      ...(skipped.length === 0 ? {} : { skip: skipped.join("; ") }),
      observed: {
        providersConfigured: configured.servers.length,
        skipped: bounded(skipped),
        surfaces,
        totalCalls: paces.reduce((sum, report) => sum + report.calls, 0),
        gapCandidates: Object.values(surfaces).flatMap((surface) => surface.gapCandidates ?? []),
      },
    };
  },
};
