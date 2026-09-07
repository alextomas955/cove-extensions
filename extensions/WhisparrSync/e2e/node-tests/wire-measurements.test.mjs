// The wire answers, read off a live seeded instance and committed with the conditions each was taken
// under. What is ASSERTED and what is merely RECORDED differ deliberately:
//
//   - Response SHAPES are asserted, through committed artifacts that fail the build on drift. A verdict
//     nothing re-checks decays into a document, which is what these answers exist to replace.
//   - Elapsed TIMES are recorded and never asserted. A millisecond threshold inside a containerized
//     suite gates the test runner's scheduling rather than Whisparr, so it would fail for reasons that
//     say nothing about the thing under measurement. What the cache-flag legs DO assert is their
//     conditions: the flag value read back off the instance, that a restart actually happened, and the
//     seeded set size.
//
// The restart leg runs last within this file because it disturbs the instance every earlier measurement
// was taken on.
import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import { startWireInstance, recordArtifact } from '../lib/wire-measure.mjs';
import {
  seedWireCorpus,
  SEED_IDS,
  generatedSceneRecordings,
  generatedSceneId,
  seedGeneratedScenes,
} from '../lib/wire-seed.mjs';
import { runFilterControl, describeReport } from '../lib/wire-control.mjs';

const HERE = dirname(fileURLToPath(import.meta.url));
const SEED_DIR = join(HERE, '..', 'fixtures', 'wire-seed');

let instance;
let seed;
const figures = [];

before(async () => {
  instance = await startWireInstance({ version: 'v3' });
  seed = await seedWireCorpus(instance);
}, { timeout: 600_000 });

after(async () => {
  // Timings are the phase's output even though no assertion reads them; print them so a re-run can be
  // transcribed into fixtures/wire/README.md without re-deriving the conditions.
  if (figures.length > 0) {
    process.stdout.write(`\nCACHE-FLAG FIGURES (${instance?.instanceVersion}, seeded set ${seed?.setSize}):\n`);
    for (const f of figures) {
      process.stdout.write(`  ${f.leg.padEnd(22)} ${f.endpoint.padEnd(26)} flagReadBack=${String(f.flagReadBack).padEnd(5)} ${String(f.elapsedMs).padStart(5)} ms  rows=${f.rows}\n`);
    }
  }
  await instance?.stop();
}, { timeout: 180_000 });

// Values that legitimately change between two identical runs. Normalized to a placeholder rather than
// dropped: key PRESENCE is the measurement, so removing a key would erase the answer being recorded.
const VOLATILE_KEYS = new Set(['added', 'traceId', 'lastSearchTime', 'sizeOnDisk', 'freeSpace']);

function normalize(value) {
  if (Array.isArray(value)) return value.map(normalize);
  if (value && typeof value === 'object') {
    return Object.fromEntries(
      Object.entries(value).map(([key, inner]) => [key, VOLATILE_KEYS.has(key) ? '<normalized>' : normalize(inner)]),
    );
  }
  return value;
}

async function capture(path) {
  const res = await instance.api('GET', path);
  return { request: path, status: res.status, contentType: res.contentType, body: normalize(res.json) };
}

test('GATE-07: one movie row, verbatim, paired with the metadata that produced it', async () => {
  const index = await instance.api('GET', '/api/v3/movie');
  const row = (index.json ?? []).find((m) => m.foreignId === SEED_IDS.sceneOne);
  assert.ok(row, 'the seeded scene row must be present in the movie index');

  // The pairing is the whole point: without the served input, an absent studioForeignId cannot be told
  // apart from a studio that was never supplied.
  const servedMetadata = JSON.parse(readFileSync(join(SEED_DIR, 'scene-one.json'), 'utf8'));

  recordArtifact('movie-row.json', {
    what: 'one GET /api/v3/movie row captured verbatim, beside the metadata record the stub served for it',
    instanceVersion: instance.instanceVersion,
    seedSize: seed.setSize,
    capturedRow: normalize(row),
    servedMetadata,
  });

  // The four fields the per-entity rewrites depend on, asserted as present-with-value so a build fails
  // if a later Whisparr stops emitting one.
  assert.ok('studioForeignId' in row, 'the row must carry a studioForeignId key');
  assert.equal(row.studioForeignId, SEED_IDS.studioWithMovies);
  assert.equal(row.studioTitle, 'Studio Aurora');
  assert.deepEqual(row.performerForeignIds, [SEED_IDS.performerWithMovies]);
  assert.deepEqual(row.performerNames, ['Ava Bennett']);
});

// The two routes a per-entity catalogue read needs. Matched case-insensitively against the document's own
// path keys: Servarr routes are case-insensitive, and the key spelling the document uses is exactly what is
// recorded here so the decision in the extension is taken from a read answer rather than from a guess.
const SIBLING_ROUTE_NEEDLES = ['listbystudioforeignid', 'listbyperformerforeignid'];

test('the instance\'s own OpenAPI document, and whether it declares the two narrow catalogue routes', async () => {
  // Fetched with NO X-Api-Key header: whether the document is readable unauthenticated is part of the
  // answer, not an assumption the extension may carry.
  const res = await fetch(`${instance.baseUrlFromHost}/docs/v3/openapi.json`);
  const contentType = res.headers.get('content-type');
  const document = await res.json();
  const pathKeys = Object.keys(document.paths ?? {});

  const routes = SIBLING_ROUTE_NEEDLES.map((needle) => {
    const key = pathKeys.find((candidate) => candidate.toLowerCase().includes(needle));
    return { needle, declared: key !== undefined, key: key ?? null };
  });

  recordArtifact('openapi-capability.json', {
    what: 'the generated OpenAPI document the instance serves, reduced to the path-key evidence a narrow-catalogue capability is decided from',
    instanceVersion: instance.instanceVersion,
    seedSize: seed.setSize,
    documentPath: '/docs/v3/openapi.json',
    status: res.status,
    contentType,
    readWithoutApiKey: true,
    totalPathCount: pathKeys.length,
    routes,
  });

  assert.equal(res.status, 200, 'the document must be readable with no API key');
  assert.ok(contentType?.includes('json'), `the document must be served as JSON, got ${contentType}`);
  assert.ok(pathKeys.length > 0, 'the document must declare at least one path');
  for (const route of routes) {
    assert.ok(route.declared, `this build is expected to declare a ${route.needle} route`);
  }
});

test('GATE-08: unknown entity versus known entity with zero movies, both siblings', async () => {
  const captured = {
    what: 'listbystudioforeignid and listbyperformerforeignid, for an unknown entity and for a known entity holding zero movies',
    instanceVersion: instance.instanceVersion,
    seedSize: seed.setSize,
    siblings: {
      studioUnknown: await capture(`/api/v3/movie/listbystudioforeignid?studioForeignId=${SEED_IDS.studioUnknown}`),
      studioKnownWithNoMovies: await capture(`/api/v3/movie/listbystudioforeignid?studioForeignId=${SEED_IDS.studioWithNoMovies}`),
      performerUnknown: await capture(`/api/v3/movie/listbyperformerforeignid?performerForeignId=${SEED_IDS.performerUnknown}`),
      performerKnownWithNoMovies: await capture(`/api/v3/movie/listbyperformerforeignid?performerForeignId=${SEED_IDS.performerWithNoMovies}`),
    },
    // Recorded because the pair above collapses: a caller that cannot tell the two apart would let an
    // add-all-missing pass register an entity's entire catalogue.
    discriminator: {
      studioUnknown: await capture(`/api/v3/studio/${SEED_IDS.studioUnknown}`),
      studioKnownWithNoMovies: await capture(`/api/v3/studio/${SEED_IDS.studioWithNoMovies}`),
      studioKnownWithMovies: await capture(`/api/v3/studio/${SEED_IDS.studioWithMovies}`),
      performerUnknown: await capture(`/api/v3/performer/${SEED_IDS.performerUnknown}`),
      performerKnownWithNoMovies: await capture(`/api/v3/performer/${SEED_IDS.performerWithNoMovies}`),
      performerKnownWithMovies: await capture(`/api/v3/performer/${SEED_IDS.performerWithMovies}`),
    },
    siblingWithMovies: await capture(`/api/v3/movie/listbystudioforeignid?studioForeignId=${SEED_IDS.studioWithMovies}`),
  };
  recordArtifact('sibling-endpoints.json', captured);

  const { siblings, discriminator } = captured;

  // The verdict, asserted so a Whisparr version that starts discriminating fails the build rather than
  // leaving a stale "indistinguishable" note in the record.
  assert.deepEqual(
    [siblings.studioUnknown.status, siblings.studioUnknown.body],
    [siblings.studioKnownWithNoMovies.status, siblings.studioKnownWithNoMovies.body],
    'the studio sibling is expected to answer identically for unknown and known-with-zero',
  );
  assert.deepEqual(
    [siblings.performerUnknown.status, siblings.performerUnknown.body],
    [siblings.performerKnownWithNoMovies.status, siblings.performerKnownWithNoMovies.body],
    'the performer sibling is expected to answer identically for unknown and known-with-zero',
  );
  assert.deepEqual(siblings.studioUnknown.body, []);
  assert.deepEqual(siblings.performerUnknown.body, []);

  // The named mechanism a caller must use instead: 404 for an entity the instance does not know, 200 for
  // one it does, whether or not that entity holds any movies.
  assert.equal(discriminator.studioUnknown.status, 404);
  assert.equal(discriminator.studioKnownWithNoMovies.status, 200);
  assert.equal(discriminator.studioKnownWithMovies.status, 200);
  assert.equal(discriminator.performerUnknown.status, 404);
  assert.equal(discriminator.performerKnownWithNoMovies.status, 200);
  assert.equal(discriminator.performerKnownWithMovies.status, 200);

  // The sibling answers movie IDS, not movie resources — a caller needs a second read to get rows.
  assert.ok(
    captured.siblingWithMovies.body.every((entry) => Number.isInteger(entry)),
    'the sibling is expected to answer an array of integer movie ids',
  );
});

test('the ?stashId= predicate selects on ForeignId, over a differing-id row and a duplicate pair', async () => {
  const byStashId = (id) => capture(`/api/v3/movie?stashId=${id}`);

  const captured = {
    what: 'what GET /api/v3/movie?stashId= returns for rows deliberately shaped to separate ForeignId from StashId',
    instanceVersion: instance.instanceVersion,
    seedSize: seed.setSize,
    // The movie row carries StashId 20000000-… and ForeignId 999001. Asking by its StashDB id finds
    // nothing; asking by its foreign id finds it.
    byTheDifferingRowsStashId: await byStashId(SEED_IDS.movieDifferingStashId),
    byTheDifferingRowsForeignId: await byStashId(SEED_IDS.movieDifferingForeignId),
    // Two rows carry StashId 10000000-…0001: the scene (whose ForeignId is also that value) and the
    // duplicate movie (whose ForeignId is 999002).
    byTheDuplicatedStashId: await byStashId(SEED_IDS.sceneOne),
    byTheDuplicateRowsForeignId: await byStashId(SEED_IDS.movieDuplicateForeignId),
    // An empty filter value is not a no-op filter — it reads the whole library.
    byAnEmptyValue: await byStashId(''),
    // The undeduped credit join: the same performer credited twice on one row.
    twiceCreditedPerformerCatalogue: await capture(
      `/api/v3/movie/listbyperformerforeignid?performerForeignId=${SEED_IDS.performerCreditedTwice}`,
    ),
    twiceCreditedRow: await capture(`/api/v3/movie?stashId=${SEED_IDS.sceneTwo}`),
  };
  recordArtifact('stashid-predicate.json', captured);

  assert.deepEqual(captured.byTheDifferingRowsStashId.body, [], 'a row found only by its StashDB id is not found at all');
  assert.equal(captured.byTheDifferingRowsForeignId.body.length, 1);
  assert.equal(captured.byTheDuplicatedStashId.body.length, 1, 'the duplicated StashDB id resolves to exactly one row');
  assert.equal(captured.byTheDuplicatedStashId.body[0].foreignId, SEED_IDS.sceneOne, 'and it is the row whose ForeignId matches');
  assert.equal(captured.byAnEmptyValue.body.length, seed.setSize, 'an empty filter value reads the whole set');
});

// The studios the delta is reported PER. Named rather than derived, so a count below names an entity and a
// disagreement names a row — an averaged single "difference" number would describe none of them.
const NAMED_STUDIOS = {
  aurora: SEED_IDS.studioWithMovies,
  cirrus: SEED_IDS.studioDivergentTitle,
  echo: SEED_IDS.studioNoTitle,
  borealis: SEED_IDS.studioWithNoMovies,
};

// The two predicates, computed here exactly as the extension computes them: the title comparison requires a
// non-empty studio title and is case-insensitive; the identity comparison is case-insensitive on the studio's
// own foreign id. A harness that computed something else would measure itself.
const attributedByTitle = (studio) => (movie) =>
  typeof studio?.title === 'string' &&
  studio.title.length > 0 &&
  typeof movie.studioTitle === 'string' &&
  movie.studioTitle.toLowerCase() === studio.title.toLowerCase();

const attributedByIdentity = (studio) => (movie) =>
  typeof studio?.foreignId === 'string' &&
  studio.foreignId.length > 0 &&
  typeof movie.studioForeignId === 'string' &&
  movie.studioForeignId.toLowerCase() === studio.foreignId.toLowerCase();

test('the studio title predicate and the studio identity predicate, measured against each other per studio', async () => {
  // ONE whole-set read, both predicates over the same rows: two reads could differ for a reason that is not
  // the predicate.
  const index = await instance.api('GET', '/api/v3/movie');
  const rows = index.json ?? [];
  assert.equal(rows.length, seed.setSize, 'the measurement must run over the whole seeded set');

  const perStudio = {};
  for (const [name, remoteId] of Object.entries(NAMED_STUDIOS)) {
    const res = await instance.api('GET', `/api/v3/studio?stashId=${remoteId}`);
    const studio = (res.json ?? [])[0] ?? null;

    const byTitle = rows.filter(attributedByTitle(studio));
    const byIdentity = rows.filter(attributedByIdentity(studio));
    const identity = (movie) => ({
      foreignId: movie.foreignId,
      title: movie.title,
      studioTitle: movie.studioTitle ?? null,
      studioForeignId: movie.studioForeignId ?? null,
    });

    perStudio[name] = {
      remoteId,
      studioKnownToWhisparr: studio !== null,
      studioTitle: studio?.title ?? null,
      studioForeignId: studio?.foreignId ?? null,
      titlePredicateCount: byTitle.length,
      identityPredicateCount: byIdentity.length,
      // Both directions, separately and by row identity. A single signed difference would hide a studio that
      // gains one row and loses another.
      gainedByIdentity: byIdentity.filter((m) => !byTitle.includes(m)).map(identity),
      lostByIdentity: byTitle.filter((m) => !byIdentity.includes(m)).map(identity),
    };
  }

  // The population rate, over the same read. A numerator and a denominator, never a bare percentage: a
  // percentage of a synthetic corpus reads like a claim about a real library.
  const nonEmpty = (v) => typeof v === 'string' && v.length > 0;
  const population = {
    totalRows: rows.length,
    rowsWithStudioTitle: rows.filter((m) => nonEmpty(m.studioTitle)).length,
    rowsWithStudioForeignId: rows.filter((m) => nonEmpty(m.studioForeignId)).length,
    rowsWithTitleButNoForeignId: rows.filter((m) => nonEmpty(m.studioTitle) && !nonEmpty(m.studioForeignId)).length,
  };

  // Each row the corpus was extended to create, paired with the studio the stub SERVED for it. Without the
  // served input, a row carrying no studio cannot be told apart from a row whose studio was never supplied —
  // and that distinction is the whole reason the lossy direction reads the way it does.
  const deltaClassRows = Object.fromEntries(
    [
      ['studioTitleServedWithoutIdentity', SEED_IDS.sceneAuroraNoStudioIdentity, 'scene-aurora-no-identity.json'],
      ['studioIdentityServedWithDivergentTitle', SEED_IDS.sceneDivergentStudioTitle, 'scene-cirrus.json'],
      ['studioServedWithNoTitle', SEED_IDS.sceneNoStudioTitle, 'scene-echo.json'],
    ].map(([name, foreignId, record]) => {
      const row = rows.find((m) => m.foreignId === foreignId);
      const served = JSON.parse(readFileSync(join(SEED_DIR, record), 'utf8'));
      return [name, {
        foreignId,
        servedStudioTitle: served.Studio?.Title ?? null,
        servedStudioForeignId: served.Studio?.ForeignIds?.StashId ?? null,
        rowPresent: row !== undefined,
        rowStudioTitle: row?.studioTitle ?? null,
        rowStudioForeignId: row?.studioForeignId ?? null,
      }];
    }),
  );

  recordArtifact('studio-attribution-delta.json', {
    what: 'the shipped title predicate and the proposed identity predicate, computed over one whole-set read, per named seeded studio and in both directions',
    instanceVersion: instance.instanceVersion,
    seedSize: seed.setSize,
    corpus: 'wholly synthetic (fixtures/wire-seed) — establishes the delta CLASSES and their direction, never a real library\'s population rate',
    // Which of the two columns the extension actually implements. titlePredicateCount is the BEFORE column
    // and identityPredicateCount the AFTER; this line is what says which one ships, and the git history of
    // this artifact is where the two readings sit side by side.
    shippedPredicate: 'the studio foreign id, case-insensitive',
    perStudio,
    deltaClassRows,
    population,
  });

  // The corpus must actually express the classes it was extended for, or the numbers above are agreement
  // measured against nothing.
  assert.ok(
    Object.values(perStudio).some((s) => s.gainedByIdentity.length > 0 || s.lostByIdentity.length > 0),
    'the corpus is expected to make at least one disagreement between the two predicates observable',
  );
});

// ---- The narrowed per-entity read, and the missing-set delta repointing to it implies ----

// The grain the extension chunks its hydration at. Restated here so the recorded request count can be read
// against the `1 + ceil(k/CHUNK)` formula without opening the C# source.
const HYDRATION_CHUNK = 1000;

const SIBLING = {
  studio: { route: 'listbystudioforeignid', param: 'studioForeignId', existence: 'studio' },
  performer: { route: 'listbyperformerforeignid', param: 'performerForeignId', existence: 'performer' },
};

// The per-entity read exactly as the extension performs it: sibling ids → client-side dedup → chunked by-id
// hydration, with the existence read issued ONLY when the id list came back empty. A harness that read it any
// other way would be measuring itself rather than the shipped shape.
async function narrowEntityRead(kind, foreignId) {
  const { route, param, existence } = SIBLING[kind];
  const requests = [];

  const sibling = await instance.api('GET', `/api/v3/movie/${route}?${param}=${foreignId}`);
  requests.push({ method: 'GET', path: `/api/v3/movie/${route}`, status: sibling.status });
  const raw = Array.isArray(sibling.json) ? sibling.json : [];
  const ids = [...new Set(raw)];

  if (ids.length === 0) {
    const probe = await instance.api('GET', `/api/v3/${existence}/${foreignId}`);
    requests.push({ method: 'GET', path: `/api/v3/${existence}/{foreignId}`, status: probe.status });
    return {
      requests,
      rows: [],
      siblingIds: raw,
      dedupedIds: ids,
      existenceStatus: probe.status,
      state: probe.status === 404 ? 'entityUnknown' : 'known',
      maxHydrationBytes: 0,
      maxHydrationRows: 0,
    };
  }

  const rows = [];
  let maxHydrationBytes = 0;
  let maxHydrationRows = 0;
  for (let offset = 0; offset < ids.length; offset += HYDRATION_CHUNK) {
    const chunk = ids.slice(offset, offset + HYDRATION_CHUNK);
    const hydrated = await instance.api('POST', '/api/v3/movie/bulk', chunk);
    requests.push({ method: 'POST', path: '/api/v3/movie/bulk', status: hydrated.status, ids: chunk.length });
    const bytes = Buffer.byteLength(hydrated.text ?? '', 'utf8');
    if (bytes > maxHydrationBytes) {
      maxHydrationBytes = bytes;
      maxHydrationRows = (hydrated.json ?? []).length;
    }
    rows.push(...(hydrated.json ?? []));
  }

  return {
    requests,
    rows,
    siblingIds: raw,
    dedupedIds: ids,
    existenceStatus: null,
    state: 'known',
    maxHydrationBytes,
    maxHydrationRows,
  };
}

const identityOf = (row) => row.foreignId;

test('the four-part control over the narrowed per-entity read, against the un-narrowed one it replaces', async () => {
  const index = await instance.api('GET', '/api/v3/movie');
  const allRows = index.json ?? [];

  // What the whole-set attribution predicate selects for Studio Aurora, computed here exactly as the extension
  // computes it. It is the identity SET part 2 must return — a count would agree with a filter that answered
  // some other five rows.
  const auroraIdentities = allRows
    .filter((row) => typeof row.studioForeignId === 'string'
      && row.studioForeignId.toLowerCase() === SEED_IDS.studioWithMovies.toLowerCase())
    .map(identityOf);

  const wholeSetRows = async () => allRows;
  const narrowRows = async (id) => (await narrowEntityRead('studio', id)).rows;

  const expected = {
    seedSize: seed.setSize,
    presentId: SEED_IDS.studioWithMovies,
    absentId: SEED_IDS.studioUnknown,
    expectedIdentities: auroraIdentities,
  };

  // Part 4 asks the SAME endpoint each leg reads, carrying a parameter it does not bind — the property that
  // makes parts 2 and 3 meaningful. For the sibling route that is the sibling route itself, so whatever it
  // answers is recorded as the leg's own answer rather than borrowed from the movie index.
  const unnarrowed = await runFilterControl(
    { setSize: async () => allRows.length, byId: wholeSetRows, unrecognisedParam: wholeSetRows, identityOf },
    expected,
  );
  const bogusSibling = await instance.api(
    `GET`, `/api/v3/movie/listbystudioforeignid?bogusParam=${SEED_IDS.studioWithMovies}`);
  const bogusRows = Array.isArray(bogusSibling.json) ? bogusSibling.json : [];
  const indexIds = new Set(allRows.map((row) => row.id));
  const bogusProjection = bogusRows.map((row) => ({
    id: row.id ?? null,
    foreignId: row.foreignId ?? null,
    title: row.title ?? null,
    studioForeignId: row.studioForeignId ?? null,
    inTheMovieIndex: indexIds.has(row.id),
  }));
  const narrowed = await runFilterControl(
    {
      setSize: async () => allRows.length,
      byId: narrowRows,
      unrecognisedParam: async () => (Array.isArray(bogusSibling.json) ? bogusSibling.json : []),
      identityOf,
    },
    expected,
  );

  const aurora = await narrowEntityRead('studio', SEED_IDS.studioWithMovies);
  const borealis = await narrowEntityRead('studio', SEED_IDS.studioWithNoMovies);
  const unknownStudio = await narrowEntityRead('studio', SEED_IDS.studioUnknown);
  const ivy = await narrowEntityRead('performer', SEED_IDS.performerWithNoMovies);
  const unknownPerformer = await narrowEntityRead('performer', SEED_IDS.performerUnknown);
  const twiceCredited = await narrowEntityRead('performer', SEED_IDS.performerCreditedTwice);

  const formula = (k) => 1 + (k === 0 ? 1 : Math.ceil(k / HYDRATION_CHUNK));
  const cost = (name, read) => ({
    entity: name,
    k: read.dedupedIds.length,
    requests: read.requests,
    requestCount: read.requests.length,
    formula: `1 + ceil(k/${HYDRATION_CHUNK})${read.dedupedIds.length === 0 ? ' + 1 existence read' : ''}`,
    formulaValue: formula(read.dedupedIds.length),
    state: read.state,
    existenceStatus: read.existenceStatus,
  });

  recordArtifact('entity-catalogue-narrow.json', {
    what: 'the four-part control applied to the narrowed per-entity read and to the un-narrowed read it replaces, plus that read\'s request sequence, its empty-case discrimination, and its largest response',
    instanceVersion: instance.instanceVersion,
    seedSize: seed.setSize,
    corpus: 'wholly synthetic (fixtures/wire-seed)',
    chunkGrain: HYDRATION_CHUNK,
    control: {
      unnarrowed: { passedParts: unnarrowed.passedParts, failedParts: unnarrowed.failedParts, parts: unnarrowed.parts },
      narrowed: { passedParts: narrowed.passedParts, failedParts: narrowed.failedParts, parts: narrowed.parts },
      // Part 4 fails on the narrow leg, and that failure is an ANSWER rather than a defect: this route does
      // NOT return the whole set for a parameter it cannot bind, the way the movie index does. What it does
      // return is recorded verbatim rather than characterised — a first reading of it as "the rows carrying no
      // studio identity" was wrong, and the row below is what settles it.
      unrecognisedParamOnTheSiblingRoute: {
        status: bogusSibling.status,
        rows: bogusRows.length,
        wholeSet: allRows.length,
        returned: bogusProjection,
      },
    },
    cost: [
      cost('studio Aurora (holds rows)', aurora),
      cost('studio Borealis (known, holds nothing)', borealis),
      cost('studio unknown to Whisparr', unknownStudio),
      cost('performer Ivy (known, holds nothing)', ivy),
      cost('performer unknown to Whisparr', unknownPerformer),
    ],
    emptyCaseDiscrimination: {
      studioKnownWithNoMovies: { existenceStatus: borealis.existenceStatus, state: borealis.state },
      studioUnknown: { existenceStatus: unknownStudio.existenceStatus, state: unknownStudio.state },
      performerKnownWithNoMovies: { existenceStatus: ivy.existenceStatus, state: ivy.state },
      performerUnknown: { existenceStatus: unknownPerformer.existenceStatus, state: unknownPerformer.state },
    },
    // The undeduped upstream Credit join: recorded as OBSERVED, so a Whisparr build that starts emitting the
    // duplicate fails the build rather than being silently absorbed by the client-side dedup.
    twiceCreditedPerformer: {
      siblingIds: twiceCredited.siblingIds,
      dedupedIds: twiceCredited.dedupedIds,
      siblingAnsweredADuplicate: twiceCredited.siblingIds.length !== twiceCredited.dedupedIds.length,
    },
    largestHydration: {
      entity: 'studio Aurora',
      rows: aurora.maxHydrationRows,
      responseBytes: aurora.maxHydrationBytes,
      bytesPerRow: aurora.maxHydrationRows === 0
        ? null
        : Math.round(aurora.maxHydrationBytes / aurora.maxHydrationRows),
    },
  });

  // The discrimination, asserted: the un-narrowed read fails exactly the two parts an ignored filter cannot
  // fail, and the narrowed one passes them.
  assert.deepEqual(unnarrowed.failedParts, [2, 3], `un-narrowed leg\n${describeReport(unnarrowed)}`);
  assert.ok(narrowed.parts[1].passed, `narrow part 2\n${describeReport(narrowed)}`);
  assert.ok(narrowed.parts[2].passed, `narrow part 3\n${describeReport(narrowed)}`);

  // The narrow leg's part-4 answer, asserted as the only thing it establishes: this route does NOT read the
  // whole set for a parameter it cannot bind. A build that starts doing so fails here rather than quietly
  // leaving the client-edge blank guard as the only thing between an unset id and a library-wide read.
  assert.notEqual(bogusRows.length, allRows.length, 'an unbound studioForeignId must not read the whole set');

  assert.equal(aurora.requests.length, formula(aurora.dedupedIds.length));
  assert.equal(borealis.requests.length, formula(0));
  assert.equal(borealis.state, 'known');
  assert.equal(unknownStudio.state, 'entityUnknown');
  assert.equal(ivy.state, 'known');
  assert.equal(unknownPerformer.state, 'entityUnknown');
});

// What Cove is taken to OWN under each seeded entity. Named here rather than derived, because the delta below
// is a statement about a diff between two id sets and the Cove side of it must be stated to be read.
// `10000000-…0011` is the row that makes the GAINING class observable: Whisparr holds it, but it carries no
// studio, so it is in the whole set and NOT in Aurora's catalogue.
const COVE_OWNED = {
  'studio Aurora': {
    kind: 'studio',
    foreignId: SEED_IDS.studioWithMovies,
    ownedIds: [SEED_IDS.sceneOne, SEED_IDS.sceneTwo, SEED_IDS.sceneAuroraNoStudioIdentity, SEED_IDS.sceneAbsent],
  },
  'studio Borealis': {
    kind: 'studio',
    foreignId: SEED_IDS.studioWithNoMovies,
    ownedIds: [SEED_IDS.sceneBorealis],
  },
  'studio Echo': {
    kind: 'studio',
    foreignId: SEED_IDS.studioNoTitle,
    ownedIds: [SEED_IDS.sceneNoStudioTitle, SEED_IDS.sceneOne],
  },
  'performer Ava': {
    kind: 'performer',
    foreignId: SEED_IDS.performerWithMovies,
    ownedIds: [SEED_IDS.sceneOne, SEED_IDS.sceneThree],
  },
  'performer Ivy': {
    kind: 'performer',
    foreignId: SEED_IDS.performerWithNoMovies,
    ownedIds: [SEED_IDS.sceneOne],
  },
};

test('the missing set computed the whole-set way and the narrow way, per entity and never averaged', async () => {
  const index = await instance.api('GET', '/api/v3/movie');
  const wholeSetIds = new Set((index.json ?? []).map(identityOf));

  const perEntity = {};
  for (const [name, { kind, foreignId, ownedIds }] of Object.entries(COVE_OWNED)) {
    const narrow = await narrowEntityRead(kind, foreignId);
    const narrowIds = new Set(narrow.rows.map(identityOf));

    // The shipped diff today: an owned scene is missing when its id indexes no row in the ENTIRE movie set.
    const missingWholeSet = ownedIds.filter((id) => !wholeSetIds.has(id));
    // What the repoint performs: an owned scene is missing when its id indexes no row in THIS entity's catalogue.
    const missingNarrow = ownedIds.filter((id) => !narrowIds.has(id));

    perEntity[name] = {
      kind,
      foreignId,
      catalogueState: narrow.state,
      ownedIds,
      catalogueIds: [...narrowIds],
      missingUnderWholeSetDiff: missingWholeSet,
      missingUnderNarrowDiff: missingNarrow,
      // GAINED: a row the narrow diff would register that the whole-set diff would not. This is the direction
      // with teeth — the row exists in Whisparr under a DIFFERENT entity, so the re-registration is answered
      // "already added", which this extension counts as a success and the reader never learns about.
      gained: missingNarrow.filter((id) => !missingWholeSet.includes(id)),
      lost: missingWholeSet.filter((id) => !missingNarrow.includes(id)),
    };
  }

  recordArtifact('missing-set-delta.json', {
    what: 'the missing set for each seeded entity computed two ways over one instance — the whole-set diff shipped today and the per-entity narrow diff that replaces it',
    instanceVersion: instance.instanceVersion,
    seedSize: seed.setSize,
    corpus: 'wholly synthetic (fixtures/wire-seed); the Cove-owned id set per entity is stated in the spec, not read from a Cove instance',
    consequenceOfTheGainingDirection:
      'a row Whisparr holds under a DIFFERENT entity is not missing under the whole-set diff and IS missing under the narrow one, so the narrow diff re-registers it; Whisparr answers already-added and this extension counts that as a success, so the reader is never told',
    shapesThatProduceAGain: [
      'a row Whisparr attributes to no studio at all (a scene whose metadata carried no studio identity)',
      'a row Whisparr attributes to a different studio than Cove does',
      'a performer credit Whisparr does not carry on the row that Cove does',
    ],
    perEntity,
  });

  // The corpus must actually express the gaining class, or the table above is agreement measured against
  // nothing.
  assert.ok(
    Object.values(perEntity).some((entity) => entity.gained.length > 0),
    'the corpus is expected to make at least one gained row observable',
  );
});

// ---- READ-05: the two contested reads — /movie/list on both verbs, and the cover parameter ----

// Probed on BOTH verbs because which one a build serves is not a detail: a POST to a GET-only MVC route
// answers 405 with no JSON content type, and the shared send loop classifies that as "not a Whisparr
// instance" — so guessing the verb reaches the user as a wrong diagnosis of their setup.
const MOVIE_LIST_PATH = '/api/v3/movie/list';

// The two paths nothing in this extension may read. What the document declares about them is recorded as
// an observation and used nowhere.
const OFF_PATH_ROUTES = ['/api/v3/movie/paged', '/api/v3/movie/stats'];

// The five members the status projection reads per movie. A shape missing any of them cannot answer the
// toolbar summary, whatever else it carries.
const SUMMARY_MEMBERS = ['stashId', 'foreignId', 'itemType', 'monitored', 'hasFile'];

// The members that make the full movie row the size it is. Presence of any one of them is what separates a
// full row from a narrow projection — read off the row rather than inferred from its length.
const HEAVY_MEMBERS = ['images', 'alternateTitles', 'ratings', 'statistics', 'searchCredits'];

const bytesOf = (res) => Buffer.byteLength(res.text ?? '', 'utf8');

// One of exactly four labels, each with the evidence it was drawn from. The evidence travels with the
// label because "fullRows" and "narrowRows" differ only by which members a row happens to carry, and a
// later build can move that line without moving anything else.
function classifyShape(request, res) {
  const rows = Array.isArray(res.json) ? res.json : null;
  const base = {
    request,
    status: res.status,
    contentType: res.contentType,
    bodyBytes: bytesOf(res),
    elementCount: rows?.length ?? null,
  };

  if (res.status < 200 || res.status >= 300 || !res.contentType?.includes('json') || rows === null) {
    return { ...base, label: 'notServed', evidence: 'the answer is not a 2xx JSON array', firstElement: null, missingSummaryMembers: SUMMARY_MEMBERS };
  }
  if (rows.length === 0) {
    return { ...base, label: 'notServed', evidence: 'answered an empty array, so it carries no element to classify', firstElement: null, missingSummaryMembers: SUMMARY_MEMBERS };
  }

  const first = rows[0];
  if (first === null || typeof first !== 'object') {
    return {
      ...base,
      label: 'bareIds',
      evidence: `elements are ${typeof first}, not objects, so a row carries none of ${SUMMARY_MEMBERS.join('/')}`,
      firstElement: normalize(first),
      missingSummaryMembers: SUMMARY_MEMBERS,
    };
  }

  const heavyPresent = HEAVY_MEMBERS.filter((member) => member in first);
  const missingSummaryMembers = SUMMARY_MEMBERS.filter((member) => !(member in first));
  return {
    ...base,
    label: heavyPresent.length > 0 ? 'fullRows' : 'narrowRows',
    evidence:
      `the first element carries ${Object.keys(first).length} members; ` +
      `heavy members present ${JSON.stringify(heavyPresent)}; ` +
      `summary members missing ${JSON.stringify(missingSummaryMembers)}`,
    firstElement: normalize(first),
    missingSummaryMembers,
  };
}

// Read once, describing what a caller pays and what identities came back. The images member is described
// rather than dumped: whether it is present, emptied or shrunk is the whole cover question.
async function readMovieIndex(query) {
  const res = await instance.api('GET', `/api/v3/movie${query}`);
  const rows = Array.isArray(res.json) ? res.json : [];
  return {
    request: `/api/v3/movie${query}`,
    status: res.status,
    rows: rows.length,
    bodyBytes: bytesOf(res),
    identities: rows.map(identityOf).sort(),
    images: {
      rowsCarryingTheMember: rows.filter((row) => 'images' in row).length,
      entries: rows.reduce((total, row) => total + (Array.isArray(row.images) ? row.images.length : 0), 0),
      entryMembers: [...new Set(rows.flatMap((row) => (row.images ?? []).flatMap((image) => Object.keys(image))))].sort(),
    },
  };
}

const sameIdentitySet = (left, right) =>
  left.length === right.length && left.every((value, index) => value === right[index]);

test('READ-05: what /movie/list answers on each verb, and whether excludeLocalCovers is honoured', async () => {
  // Leg A — the path the requirement names, on both verbs, plus the by-foreign-ids body the research
  // attributes to the POST reading.
  const legA = {
    get: classifyShape(
      `GET ${MOVIE_LIST_PATH}`,
      await instance.api('GET', MOVIE_LIST_PATH),
    ),
    postEmptyBody: classifyShape(
      `POST ${MOVIE_LIST_PATH} []`,
      await instance.api('POST', MOVIE_LIST_PATH, []),
    ),
    postTwoForeignIds: classifyShape(
      `POST ${MOVIE_LIST_PATH} [two seeded foreign ids]`,
      await instance.api('POST', MOVIE_LIST_PATH, [SEED_IDS.sceneOne, SEED_IDS.sceneTwo]),
    ),
  };

  // Leg B — decided by a three-way rule, never by a 200. The plain read is taken twice around the
  // narrowed one so a member that merely settles between two reads cannot be attributed to the parameter.
  const plainBefore = await readMovieIndex('');
  const narrowed = await readMovieIndex('?excludeLocalCovers=true');
  const plainAfter = await readMovieIndex('');
  const plainIsStable = plainBefore.bodyBytes === plainAfter.bodyBytes
    && sameIdentitySet(plainBefore.identities, plainAfter.identities);
  const identitiesAgree = sameIdentitySet(plainBefore.identities, narrowed.identities);
  const coverLabel = !identitiesAgree
    ? 'rejected'
    : narrowed.bodyBytes < plainBefore.bodyBytes
      ? 'honoured'
      : 'ignored';

  // Leg C — the instance's own description of the same path, and of the two this extension may not read.
  const document = await fetch(`${instance.baseUrlFromHost}/docs/v3/openapi.json`).then((r) => r.json());
  const pathKeys = Object.keys(document.paths ?? {});
  const declaredFor = (path) => {
    const key = pathKeys.find((candidate) => candidate.toLowerCase() === path.toLowerCase());
    return { path, declared: key !== undefined, verbs: key === undefined ? [] : Object.keys(document.paths[key]).sort() };
  };
  const legC = {
    movieList: declaredFor(MOVIE_LIST_PATH),
    offPathRoutes: OFF_PATH_ROUTES.map(declaredFor),
    // Where the document and a live call disagree, the live call is what this extension is built against.
    agreesWithTheLiveCalls:
      declaredFor(MOVIE_LIST_PATH).verbs.includes('get') === (legA.get.label !== 'notServed')
      && declaredFor(MOVIE_LIST_PATH).verbs.includes('post') === (legA.postTwoForeignIds.label !== 'notServed'),
  };

  // Leg D — the four-part control on both verbs, so a verb that quietly answers the whole set cannot be
  // recorded as a narrowing. The GET verb binds no id at all, so its read ignores the argument, which is
  // exactly the un-narrowed signature the control exists to name.
  const wholeSetRows = await instance.api('GET', '/api/v3/movie');
  const allRows = wholeSetRows.json ?? [];
  const getListControl = await runFilterControl(
    {
      setSize: async () => (await instance.api('GET', MOVIE_LIST_PATH)).json?.length ?? 0,
      byId: async () => (await instance.api('GET', MOVIE_LIST_PATH)).json ?? [],
      unrecognisedParam: async () => (await instance.api('GET', `${MOVIE_LIST_PATH}?bogusParam=1`)).json ?? [],
      identityOf: (row) => String(row),
    },
    {
      seedSize: seed.setSize,
      presentId: SEED_IDS.sceneOne,
      absentId: SEED_IDS.sceneAbsent,
      expectedIdentities: [String(allRows.find((row) => row.foreignId === SEED_IDS.sceneOne)?.id)],
    },
  );
  const postListControl = await runFilterControl(
    {
      setSize: async () => allRows.length,
      byId: async (id) => (await instance.api('POST', MOVIE_LIST_PATH, [id])).json ?? [],
      unrecognisedParam: async () => (await instance.api('POST', `${MOVIE_LIST_PATH}?bogusParam=1`, [])).json ?? [],
      identityOf,
    },
    { seedSize: seed.setSize, presentId: SEED_IDS.sceneOne, absentId: SEED_IDS.sceneAbsent },
  );

  // Leg E — what each read that answered costs per row, plus the exclusion read the summary also pays.
  const exclusions = await instance.api('GET', '/api/v3/exclusions');
  const perRow = (bytes, rows) => (rows === 0 ? null : Math.round((bytes / rows) * 100) / 100);
  const legE = {
    plainMovieIndex: { rows: plainBefore.rows, bodyBytes: plainBefore.bodyBytes, bytesPerRow: perRow(plainBefore.bodyBytes, plainBefore.rows) },
    movieIndexWithoutLocalCovers: coverLabel === 'honoured'
      ? { rows: narrowed.rows, bodyBytes: narrowed.bodyBytes, bytesPerRow: perRow(narrowed.bodyBytes, narrowed.rows) }
      : null,
    getMovieList: { rows: legA.get.elementCount, bodyBytes: legA.get.bodyBytes, bytesPerRow: perRow(legA.get.bodyBytes, legA.get.elementCount ?? 0) },
    postMovieListByForeignIds: { rows: legA.postTwoForeignIds.elementCount, bodyBytes: legA.postTwoForeignIds.bodyBytes, bytesPerRow: perRow(legA.postTwoForeignIds.bodyBytes, legA.postTwoForeignIds.elementCount ?? 0) },
    exclusionSet: { rows: (exclusions.json ?? []).length, bodyBytes: bytesOf(exclusions) },
  };

  const servesAWholeSetNarrowRow =
    legA.get.label === 'narrowRows' && legA.get.missingSummaryMembers.length === 0;

  recordArtifact('movie-list-probe.json', {
    what: 'GET and POST /api/v3/movie/list answered verbatim and labelled, the excludeLocalCovers honour check decided three ways, what the instance\'s own document declares for all three paths, the four-part control on each verb, and bytes per row for every read that answered',
    instanceVersion: instance.instanceVersion,
    seedSize: seed.setSize,
    corpus: 'wholly synthetic (fixtures/wire-seed)',
    movieList: legA,
    excludeLocalCovers: {
      label: coverLabel,
      plainRead: plainBefore,
      narrowedRead: narrowed,
      // The control on the control: two plain reads around the narrowed one, so a member that settles
      // between reads cannot be read as the parameter's doing.
      plainReadRepeatedAfter: plainAfter,
      plainReadIsStableAcrossTwoReads: plainIsStable,
      identitySetsAgree: identitiesAgree,
      byteDelta: narrowed.bodyBytes - plainBefore.bodyBytes,
    },
    declaredByTheInstance: legC,
    filterControl: {
      getVerb: { passedParts: getListControl.passedParts, failedParts: getListControl.failedParts, parts: getListControl.parts },
      postVerb: { passedParts: postListControl.passedParts, failedParts: postListControl.failedParts, parts: postListControl.parts },
    },
    costPerRow: legE,
    whyTheOtherShapesDoNotServeTheSummary: {
      bareIds: 'an id carries neither the monitored flag nor the file flag, so a summary built from ids would have to hydrate every one of them — the same rows in more requests',
      byForeignIdsBatch: 'a by-foreign-ids POST needs the id list up front, and the summary has none: it counts Cove videos, so producing that list means one id per Cove video',
    },
    decision: {
      summaryRead: servesAWholeSetNarrowRow ? 'movieListNarrowRows' : 'streamedMovieIndex',
      sendExcludeLocalCovers: coverLabel === 'honoured',
    },
  });

  // The plain read must be stable across two reads or the cover label is measuring the clock.
  assert.ok(plainIsStable, `two plain reads disagreed: ${plainBefore.bodyBytes} then ${plainAfter.bodyBytes} bytes`);
  // The corpus must be able to express the difference the label claims to have measured.
  assert.ok(plainBefore.images.entries > 0, 'the corpus must serve at least one cover, or the cover label is a statement about the corpus');
  // A parameter this extension will send must not change WHICH rows come back.
  assert.ok(identitiesAgree, 'excludeLocalCovers must not change the row set');
  // Both verbs are served here, and the document says so too — a disagreement is a finding, not a pass.
  assert.ok(legC.movieList.declared, `${MOVIE_LIST_PATH} must be declared by the instance's own document`);
  assert.ok(legC.agreesWithTheLiveCalls, 'the document and the live calls must agree on which verbs answer');
});

// ---- GATE-09: three labelled figures per sibling, each carrying the flag value read back ----

async function measure(leg, flagReadBack, path, endpoint) {
  const started = performance.now();
  const res = await instance.api('GET', path);
  const elapsedMs = Math.round(performance.now() - started);
  assert.equal(res.status, 200, `${endpoint} answered ${res.status} on the ${leg} leg`);
  const figure = { leg, endpoint, flagReadBack, elapsedMs, rows: res.json.length, seedSize: seed.setSize, instanceVersion: instance.instanceVersion };
  figures.push(figure);
  return figure;
}

const performerPath = `/api/v3/movie/listbyperformerforeignid?performerForeignId=${SEED_IDS.performerWithMovies}`;
const studioPath = `/api/v3/movie/listbystudioforeignid?studioForeignId=${SEED_IDS.studioWithMovies}`;

test('GATE-09 leg 1: whisparrCacheMovieAPI OFF, the fresh-instance default', async () => {
  const config = await instance.readHostConfig();
  assert.equal(config.whisparrCacheMovieAPI, false, 'a fresh instance is expected to default the flag off');

  await measure('off', config.whisparrCacheMovieAPI, performerPath, 'listByPerformerForeignId');
  await measure('off', config.whisparrCacheMovieAPI, studioPath, 'listByStudioForeignId');
});

test('GATE-09 leg 2: whisparrCacheMovieAPI ON, warm', async () => {
  const config = await instance.writeHostConfig({ whisparrCacheMovieAPI: true });
  assert.equal(config.whisparrCacheMovieAPI, true, 'the flag must read back changed off the instance, not merely have been sent');

  // Warm means the resource cache the flag switches the read onto is already populated.
  await instance.api('GET', '/api/v3/movie');

  await measure('on-warm', config.whisparrCacheMovieAPI, performerPath, 'listByPerformerForeignId');
  await measure('on-warm', config.whisparrCacheMovieAPI, studioPath, 'listByStudioForeignId');
});

test('GATE-09 leg 3: whisparrCacheMovieAPI ON, cold after a restart', async () => {
  const beforeRestart = await instance.whisparr.container.getId();
  await instance.restartInstance();

  const config = await instance.readHostConfig();
  assert.equal(config.whisparrCacheMovieAPI, true, 'the flag must survive the restart, or this is not the on-cold leg');
  assert.equal(await instance.whisparr.container.getId(), beforeRestart, 'the restart must be of the same container, not a replacement');

  // The FIRST call after the restart is the measurement — a second one would be warm again.
  const performer = await measure('on-cold-after-restart', config.whisparrCacheMovieAPI, performerPath, 'listByPerformerForeignId');
  await measure('on-cold-after-restart', config.whisparrCacheMovieAPI, studioPath, 'listByStudioForeignId');
  assert.equal(performer.rows, 3, 'the cold leg must read the same catalogue every other leg read');

  // Whether the instance emitted its cold-cache lock warnings is recorded either way: an absence at this
  // seed size is itself the answer, since that lock is the backpressure limit a real library would hit.
  const logs = await readContainerLog(instance.whisparr.container);
  const lockWarnings = logs
    .split('\n')
    .filter((line) => /\[Warn\]|\[Error\]/.test(line) && /lock|timed? ?out|cache/i.test(line));
  process.stdout.write(`\nCOLD-CACHE LOCK WARNINGS: ${lockWarnings.length}\n${lockWarnings.slice(0, 10).join('\n')}\n`);
});

async function readContainerLog(container) {
  const stream = await container.logs();
  let buffer = '';
  stream.on('data', (chunk) => {
    buffer += chunk.toString();
  });
  await new Promise((resolve) => setTimeout(resolve, 3000));
  return buffer;
}

// ---- READ-05: what the summary read costs on the wire, at two corpus sizes ----

// Tenfold apart, with the larger past a hundred rows so the array envelope cannot dominate the per-row
// figure. They run on their OWN instance: growing the shared corpus would move every reading above.
const COST_SIZES = [12, 120];

// How far the per-row figures may disagree between the two sizes. Agreement is what proves there is no fixed
// term hiding in the number and no cap truncating the larger read; the slack covers only the array envelope
// and the row-index digits.
const BYTES_PER_ROW_TOLERANCE = 0.05;

async function readWithBytes(instance, path) {
  const res = await instance.api('GET', path);
  const rows = Array.isArray(res.json) ? res.json : [];
  return {
    request: path,
    status: res.status,
    rows: rows.length,
    bodyBytes: Buffer.byteLength(res.text ?? '', 'utf8'),
    bytesPerRow: rows.length === 0 ? null : Math.round((Buffer.byteLength(res.text ?? '', 'utf8') / rows.length) * 100) / 100,
  };
}

test('READ-05: the summary read\'s wire cost at two corpus sizes tenfold apart', async () => {
  const cost = await startWireInstance({
    version: 'v3',
    extraRecordings: generatedSceneRecordings(COST_SIZES[1]),
  });

  try {
    const measurements = [];
    let seeded = 0;
    for (const size of COST_SIZES) {
      const seedResult = await seedGeneratedScenes(cost, seeded + 1, size);
      seeded = size;
      // The wall-clock the seeding cost is PRINTED rather than recorded: it varies run to run, and pinning it
      // in an artifact that fails on drift would make a container's scheduling a gate. It belongs beside the
      // other conditions in fixtures/wire/README.md.
      process.stdout.write(
        `\nSEEDING: +${seedResult.added} rows to ${seedResult.setSize} in ${seedResult.elapsedMs} ms\n`);
      measurements.push({
        corpusSize: seedResult.setSize,
        plainMovieIndex: await readWithBytes(cost, '/api/v3/movie'),
        movieIndexWithoutLocalCovers: await readWithBytes(cost, '/api/v3/movie?excludeLocalCovers=true'),
        exclusionSet: await readWithBytes(cost, '/api/v3/exclusions'),
      });
    }

    const [small, large] = measurements;

    // The shipped summary read, under the same four-part control every narrowed read faces — and expected RED
    // at parts 2 and 3, because it is a whole-set read and the cover parameter narrows the payload rather than
    // the row set. Recording it green would be recording a narrowing that is not one.
    const shippedRows = async () =>
      (await cost.api('GET', '/api/v3/movie?excludeLocalCovers=true')).json ?? [];
    const shippedReadControl = await runFilterControl(
      {
        setSize: async () => (await cost.api('GET', '/api/v3/movie')).json?.length ?? 0,
        byId: shippedRows,
        unrecognisedParam: shippedRows,
        identityOf,
      },
      { seedSize: large.corpusSize, presentId: generatedSceneId(1), absentId: SEED_IDS.sceneAbsent },
    );

    // The per-entity read that IS a narrowing, re-run at the larger corpus so its green is not an artefact of
    // the eight-row set it was first taken on.
    const narrowRead = async (id) => (await cost.api('GET', `/api/v3/movie?stashId=${id}`)).json ?? [];
    const narrowReadControl = await runFilterControl(
      {
        setSize: async () => (await cost.api('GET', '/api/v3/movie')).json?.length ?? 0,
        byId: narrowRead,
        unrecognisedParam: async () => (await cost.api('GET', '/api/v3/movie?bogusParam=1')).json ?? [],
        identityOf,
      },
      { seedSize: large.corpusSize, presentId: generatedSceneId(1), absentId: SEED_IDS.sceneAbsent },
    );

    const agreement = (a, b) => Math.abs(a - b) / Math.max(a, b);

    recordArtifact('summary-read-cost.json', {
      what: 'the toolbar summary\'s wire cost measured at two corpus sizes tenfold apart, with the four-part control re-run at the larger one against both the shipped whole-set read and the narrow per-entity read',
      instanceVersion: cost.instanceVersion,
      corpus: 'generated per run (lib/wire-seed.mjs seedGeneratedScenes) — one studio, no credits, one cover per row so the cover parameter has something to act on at every size',
      sizes: COST_SIZES,
      measurements,
      perRowAgreement: {
        plainMovieIndex: Math.round(agreement(small.plainMovieIndex.bytesPerRow, large.plainMovieIndex.bytesPerRow) * 10000) / 10000,
        movieIndexWithoutLocalCovers: Math.round(agreement(small.movieIndexWithoutLocalCovers.bytesPerRow, large.movieIndexWithoutLocalCovers.bytesPerRow) * 10000) / 10000,
        tolerance: BYTES_PER_ROW_TOLERANCE,
        whyItIsTheAssertion:
          'agreement across a tenfold size change is what shows the per-row figure carries no fixed term and that the larger read was not truncated; the absolute byte counts are machine- and build-specific and are recorded rather than asserted',
      },
      coverParameterSaving: measurements.map((m) => ({
        corpusSize: m.corpusSize,
        rows: m.plainMovieIndex.rows,
        bytesSaved: m.plainMovieIndex.bodyBytes - m.movieIndexWithoutLocalCovers.bodyBytes,
        bytesSavedPerRow: Math.round(((m.plainMovieIndex.bodyBytes - m.movieIndexWithoutLocalCovers.bodyBytes) / m.plainMovieIndex.rows) * 100) / 100,
        // It removes each image entry's locally-cached path and nothing else, so the saving is per COVER and
        // not per row: a corpus whose rows carry no cover would show zero here and say nothing about the
        // parameter.
        coversPerRow: 1,
      })),
      requestCount: {
        formula: '1 whole-set movie read + 1 exclusion read, at any corpus size',
        assertedIn: 'WhisparrSync.Tests/SceneStatus/SummaryIndexEquivalenceTests',
        note: 'the payload narrowed, the scope did not — the request instrument still classifies the summary\'s movie read as whole-set',
      },
      filterControl: {
        shippedSummaryRead: {
          passedParts: shippedReadControl.passedParts,
          failedParts: shippedReadControl.failedParts,
          parts: shippedReadControl.parts,
          reading: 'the whole-set signature, deliberately: excludeLocalCovers reduces bytes and returns every row',
        },
        narrowPerEntityRead: {
          passedParts: narrowReadControl.passedParts,
          failedParts: narrowReadControl.failedParts,
          parts: narrowReadControl.parts,
        },
      },
      retainedMemory: {
        measuredBy: 'WhisparrSync.Tests/SceneStatus/SummaryReadCostTests',
        whyNotHere:
          'a managed-heap figure is machine- and runtime-specific, so it is asserted as a SHAPE in process and transcribed into fixtures/wire/README.md rather than pinned byte-exactly here, the same rule the elapsed-time figures already follow',
      },
    });

    // The per-row figure must agree across a tenfold size change, or there is a fixed term in it — or a cap.
    for (const key of ['plainMovieIndex', 'movieIndexWithoutLocalCovers']) {
      assert.ok(
        agreement(small[key].bytesPerRow, large[key].bytesPerRow) < BYTES_PER_ROW_TOLERANCE,
        `${key} bytes/row disagree across the two sizes: ${small[key].bytesPerRow} at ${small.corpusSize} rows, ` +
          `${large[key].bytesPerRow} at ${large.corpusSize}`,
      );
      // And the larger read must actually have returned the larger set: a cap would satisfy the ratio above.
      assert.equal(large[key].rows, large.corpusSize);
    }

    assert.equal(large.corpusSize, COST_SIZES[1]);
    assert.ok(large.corpusSize >= small.corpusSize * 10, 'the two sizes must be at least tenfold apart');
    assert.deepEqual(shippedReadControl.failedParts, [2, 3], `shipped read\n${describeReport(shippedReadControl)}`);
    assert.deepEqual(narrowReadControl.failedParts, [], `narrow read\n${describeReport(narrowReadControl)}`);
  } finally {
    await cost.stop();
  }
}, { timeout: 900_000 });
