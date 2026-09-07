// The read-path ledger: what each Whisparr-facing path costs, per generation, with every figure carrying the
// run, test class or artifact that produced it.
//
// This file measures the WIRE columns — response bytes and row counts — off its own seeded instance, at two
// corpus sizes a tenfold apart. It brings up a private instance rather than sharing the corpus every other
// wire spec is measured against: growing that corpus would move every one of those readings.
//
// What is asserted and what is merely recorded differ, following the rule the sibling wire specs already
// hold: response SHAPES and per-row constants are asserted, absolute byte counts and elapsed times are
// recorded. A per-row figure agreeing across a tenfold size change is what shows the number carries no fixed
// term; the larger read's row count is asserted separately, because agreement alone is satisfied by a
// truncated response.
//
// Managed-heap figures are deliberately absent from the committed artifact. They vary a few percent between
// runs, so they are asserted as a shape in process and transcribed into fixtures/wire/README.md beside their
// conditions — the same rule the elapsed-time figures follow.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { startWireInstance, recordArtifact } from '../lib/wire-measure.mjs';
import {
  GENERATED_STUDIO_ID,
  generatedSceneRecordings,
  seedGeneratedScenes,
} from '../lib/wire-seed.mjs';

// Tenfold apart to the row, with the larger past a hundred so the array envelope cannot dominate the per-row
// figure. Raise the pair if a bring-up sustains it; never lower it.
const CORPUS_SIZES = [12, 120];

// How far the per-row figures may disagree across the two sizes before the number is carrying a fixed term.
// The slack covers the array envelope and the row-index digits, nothing else.
const BYTES_PER_ROW_TOLERANCE = 0.05;

// Whether a read's per-row byte figure is a CONSTANT the size change can test. A response whose rows are
// bare integer ids has a per-row cost equal to the id's decimal width, so it grows logarithmically with the
// id space and disagrees across a tenfold change for a reason that is not a fixed term. Recording that
// figure is honest; asserting agreement on it would be asserting the wrong thing.
const ROW_SHAPE = { object: 'object', integerId: 'integerId' };

async function readWithBytes(instance, method, path, body, rowShape = ROW_SHAPE.object) {
  const res = await instance.api(method, path, body);
  const rows = Array.isArray(res.json) ? res.json : [];
  const bodyBytes = Buffer.byteLength(res.text ?? '', 'utf8');
  return {
    request: `${method} ${path}`,
    status: res.status,
    rowShape,
    rows: rows.length,
    bodyBytes,
    bytesPerRow: rows.length === 0 ? null : Math.round((bodyBytes / rows.length) * 100) / 100,
    json: rows,
  };
}

// One path's reads at one corpus size. The request COUNT is the length of this list: a path that fanned out
// with the corpus would show a longer list at the larger size, which is the failure a byte figure cannot see.
async function measurePaths(instance) {
  const summary = [
    await readWithBytes(instance, 'GET', '/api/v3/movie?excludeLocalCovers=true'),
    await readWithBytes(instance, 'GET', '/api/v3/exclusions'),
  ];

  const siblings = await readWithBytes(
    instance,
    'GET',
    `/api/v3/movie/listbystudioforeignid?studioForeignId=${GENERATED_STUDIO_ID}`,
    undefined,
    ROW_SHAPE.integerId,
  );
  const catalogue = [siblings, await readWithBytes(instance, 'POST', '/api/v3/movie/bulk', siblings.json)];

  return { videosToolbarSummary: summary, perEntityCatalogue: catalogue };
}

const agreement = (a, b) => Math.abs(a - b) / Math.max(a, b);

test('the read-path ledger: each path\'s wire cost at two corpus sizes tenfold apart', async () => {
  const instance = await startWireInstance({
    version: 'v3',
    extraRecordings: generatedSceneRecordings(CORPUS_SIZES[1]),
  });

  try {
    const readings = [];
    let seeded = 0;
    for (const size of CORPUS_SIZES) {
      const seedResult = await seedGeneratedScenes(instance, seeded + 1, size);
      seeded = size;
      // Printed rather than recorded: the wall-clock varies run to run, and pinning it in an artifact that
      // fails on drift would make a container's scheduling a gate.
      process.stdout.write(
        `\nLEDGER SEEDING: +${seedResult.added} rows to ${seedResult.setSize} in ${seedResult.elapsedMs} ms\n`,
      );
      readings.push({ corpusSize: seedResult.setSize, paths: await measurePaths(instance) });
    }

    const [small, large] = readings;
    const PATHS = ['videosToolbarSummary', 'perEntityCatalogue'];

    // The corpus sizes come from what the instance reports it holds, never from the constants above: a
    // seeding failure that left the set short would otherwise be asserted against the number it was meant to
    // reach rather than the number that exists.
    assert.ok(
      large.corpusSize >= small.corpusSize * 10,
      `the two corpus sizes must be at least tenfold apart: ${small.corpusSize} and ${large.corpusSize}`,
    );

    for (const path of PATHS) {
      const smallReads = small.paths[path];
      const largeReads = large.paths[path];

      // A path that fanned out with the corpus issues more calls at the larger size.
      assert.equal(
        smallReads.length,
        largeReads.length,
        `${path} issued ${smallReads.length} requests at ${small.corpusSize} rows and ${largeReads.length} at ${large.corpusSize}`,
      );

      for (const [index, largeRead] of largeReads.entries()) {
        const smallRead = smallReads[index];
        assert.equal(largeRead.status, 200, `${path} read ${index} answered ${largeRead.status}`);
        if (largeRead.bytesPerRow === null || smallRead.bytesPerRow === null) {
          continue;
        }

        // The row count is asserted on EVERY read, including the bare-id one whose per-row bytes are not a
        // constant — a truncated answer is a truncated answer whatever its rows are made of.
        assert.equal(
          largeRead.rows,
          large.corpusSize,
          `${path} ${largeRead.request} returned ${largeRead.rows} rows against a corpus of ${large.corpusSize}`,
        );
        if (largeRead.rowShape !== 'object') {
          continue;
        }

        assert.ok(
          agreement(smallRead.bytesPerRow, largeRead.bytesPerRow) < BYTES_PER_ROW_TOLERANCE,
          `${path} ${largeRead.request} bytes/row disagree across the two sizes: ` +
            `${smallRead.bytesPerRow} at ${small.corpusSize} rows, ${largeRead.bytesPerRow} at ${large.corpusSize}`,
        );
      }
    }

    recordArtifact('bound-08-ledger.json', {
      what: 'what each Whisparr read path costs, per generation, in response bytes, retained bytes and outbound requests — with every figure naming the run, test class or artifact behind it',
      instanceVersion: instance.instanceVersion,
      corpus:
        'generated per run (lib/wire-seed.mjs seedGeneratedScenes) — one studio, no credits, one cover per row',
      corpusSizes: readings.map((r) => r.corpusSize),
      whatTheCorpusCanEstablish:
        'a corpus of tens or hundreds of rows establishes the SHAPE — a per-row constant agreeing across a tenfold change shows the figure carries no fixed term — and establishes nothing about absolute cost at a real library size',
      rows: [
        {
          path: 'videosToolbarSummary',
          generation: 'v3',
          responseBytes: {
            perRow: large.paths.videosToolbarSummary[0].bytesPerRow,
            atCorpusSize: large.corpusSize,
            agreementAcrossSizes:
              Math.round(
                agreement(
                  small.paths.videosToolbarSummary[0].bytesPerRow,
                  large.paths.videosToolbarSummary[0].bytesPerRow,
                ) * 10000,
              ) / 10000,
            source: 'measured here, GET /api/v3/movie?excludeLocalCovers=true at both corpus sizes',
          },
          retainedBytes: {
            perEntry: '≈650–790 B, against ≈3 230–3 390 B for the materialised wide rows',
            source: 'WhisparrSync.Tests/SceneStatus/SummaryReadCostTests, at 10 000 and 100 000 elements',
          },
          requestCount: {
            formula: '2 — one movie read plus one exclusion read, at any corpus size',
            measuredHere: large.paths.videosToolbarSummary.length,
            source: 'asserted in WhisparrSync.Tests/SceneStatus/SummaryIndexEquivalenceTests; the count above is this run',
          },
          qualification:
            'the read is still WHOLE-SET: the payload narrowed, the scope did not. Both columns grow with the Whisparr movie set — a constant factor of about five lower, not the absence of a ceiling',
        },
        {
          path: 'videosToolbarSummary',
          generation: 'v2',
          responseBytes: {
            perRow: null,
            source: null,
            why: 'not measured — this run brings up one instance and it is the newer generation. The older generation has no movie entity to read, so there is no per-row movie figure to take',
          },
          retainedBytes: {
            perEntry: null,
            source: null,
            why: 'not measured — the set is synthesized from series and episode rows rather than bound from a movie array, so a per-movie-entry figure does not describe it',
          },
          requestCount: {
            formula: '1 + 2N requests for N sites — the set is walked series -> episode -> episodefile',
            measuredHere: null,
            source: 'WhisparrSync/Adapters/V2Adapter, stated in docs/ARCHITECTURE.md',
          },
          qualification:
            'there is no narrow movie endpoint on this generation at all, and no reshaping on the extension side changes that — which is why the ceiling is stated per generation and not once',
        },
        {
          path: 'perEntityCatalogue',
          generation: 'v3',
          responseBytes: {
            perRow: large.paths.perEntityCatalogue[1].bytesPerRow,
            atCorpusSize: large.corpusSize,
            agreementAcrossSizes:
              Math.round(
                agreement(
                  small.paths.perEntityCatalogue[1].bytesPerRow,
                  large.paths.perEntityCatalogue[1].bytesPerRow,
                ) * 10000,
              ) / 10000,
            source: 'measured here, POST /api/v3/movie/bulk at both corpus sizes',
            siblingReadPerRow: {
              atSmallCorpus: small.paths.perEntityCatalogue[0].bytesPerRow,
              atLargeCorpus: large.paths.perEntityCatalogue[0].bytesPerRow,
              recordedNotAsserted:
                'the sibling read answers bare integer ids, so its per-row cost is the id\'s decimal width and grows logarithmically with the id space. Asserting agreement across a tenfold size change would be asserting the wrong thing; the row COUNT is asserted on it, which is what a truncated answer would fail',
            },
          },
          retainedBytes: {
            perEntry: null,
            source: null,
            why: 'not measured — no in-process harness samples this path mid-read, and a figure with no measurement behind it is not written down',
          },
          requestCount: {
            formula: '1 + ceil(k/1000) for k sibling ids',
            measuredHere: large.paths.perEntityCatalogue.length,
            source: 'asserted exactly at and one past a chunk boundary in WhisparrSync.Tests/Api/BoundedReadLedgerTests',
          },
          qualification:
            'bounded by the ENTITY rather than by the library, and the hydration loop is driven by the end of the id list rather than by a chunk count',
        },
        {
          path: 'perEntityCatalogue',
          generation: 'v2',
          responseBytes: { perRow: null, source: null, why: 'the route does not exist on this generation' },
          retainedBytes: { perEntry: null, source: null, why: 'the route does not exist on this generation' },
          requestCount: {
            formula: null,
            measuredHere: null,
            source: null,
            why: 'the catalogue role is not implemented on this generation, so the path does not run at all',
          },
          qualification:
            'capability is expressed by presence of the role interface; the older generation simply does not hold it',
        },
        {
          path: 'singleScenePush',
          generation: 'v3',
          responseBytes: {
            perRow: null,
            source: null,
            why: 'not measured — the response is at most one row, so a per-row byte figure describes nothing that grows',
          },
          retainedBytes: {
            perEntry: null,
            source: null,
            why: 'not measured — the path binds at most one row, so there is no per-entry term to sample',
          },
          requestCount: {
            formula: '0 whole-set movie reads and exactly 1 narrow read per scene',
            measuredHere: null,
            source: 'WhisparrSync.Tests/Api/BoundedReadLedgerTests, asserted at 3 and at 30 scenes',
          },
          qualification:
            'the positive narrow count is asserted beside the zero: a path that stopped asking Whisparr anything would satisfy the zero and answer every scene "not added"',
        },
        {
          path: 'bulkMarkWantedAdd',
          generation: 'v3',
          responseBytes: { perRow: null, source: null, why: 'the path issues writes; there is no row set to size' },
          retainedBytes: {
            perEntry: null,
            source: null,
            why: 'not measured — the loop holds one scene at a time, so there is no accumulating per-entry term',
          },
          requestCount: {
            formula: '2 context reads PER SCENE plus 1 once, on top of the per-scene add itself',
            measuredHere: null,
            source: 'WhisparrSync.Tests/Api/BoundedReadLedgerTests, measured at 3 scenes (7 reads) and 30 scenes (61), slope 2.00 per scene',
          },
          qualification:
            'a fixed cost PER SCENE is O(library) in requests across a full sync. This milestone did not change it; memoising the root, tag and profile resolve across a batch is what would, and the sibling batch add already does exactly that (its count does not move with the scene count at all)',
        },
        {
          path: 'librarySyncFanOut',
          generation: 'both',
          responseBytes: { perRow: null, source: null, why: 'a job fan-out issues no read of its own' },
          retainedBytes: {
            perEntry: '≈556 B per parked fan-out unit',
            source: 'WhisparrSync.Tests/Api/SyncFanOutCostTests, at 10 000 and 100 000 units',
          },
          requestCount: {
            formula: 'scene units capped at 64; entity units are one per studio and per performer',
            measuredHere: null,
            source: 'WhisparrSync.Tests/Api/SyncFanOutCostTests — 66 units at both 1e6 and 1e7 scenes',
          },
          qualification:
            'the SCENE fan-out no longer tracks the library; the ENTITY fan-out still does — on the order of 25 000 units at a million-scene library with ~5 000 studios and ~20 000 performers',
        },
      ],
      // The bodies themselves are dropped: what is being recorded is the SIZE of each read, and carrying a
      // hundred-and-twenty rows of it would put a corpus in an artifact whose subject is a byte count.
      readings: readings.map((reading) => ({
        corpusSize: reading.corpusSize,
        paths: Object.fromEntries(
          Object.entries(reading.paths).map(([path, reads]) => [
            path,
            reads.map(({ json: _rows, ...read }) => read),
          ]),
        ),
      })),
    });

    // Recorded and asserted separately from the artifact, so a re-record cannot quietly make either true.
    assert.equal(large.paths.videosToolbarSummary.length, 2);
    assert.equal(large.paths.perEntityCatalogue.length, 2);
    assert.equal(large.paths.perEntityCatalogue[0].rows, large.corpusSize);
  } finally {
    await instance.stop();
  }
}, { timeout: 900_000 });
