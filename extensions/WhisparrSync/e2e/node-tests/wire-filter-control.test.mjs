// The four-part filter control, applied twice over ONE seeded instance: once to the narrow read, and
// once to the un-narrowed read it replaces.
//
// Both legs are permanent. The inverted leg is not a historical note about a failure someone once saw —
// it re-observes, on every run, that this control still fails when the filter is dropped. A control that
// only ever runs green cannot distinguish "the filter works" from "the assertion is vacuous", which is
// exactly the failure mode Whisparr's unbound-parameter behaviour produces.
import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import { startWireInstance, recordArtifact } from '../lib/wire-measure.mjs';
import { seedWireCorpus, SEED_IDS } from '../lib/wire-seed.mjs';
import { runFilterControl, describeReport } from '../lib/wire-control.mjs';

let instance;
let seed;

before(async () => {
  instance = await startWireInstance({ version: 'v3' });
  seed = await seedWireCorpus(instance);
}, { timeout: 600_000 });

after(async () => {
  await instance?.stop();
}, { timeout: 180_000 });

async function rows(path) {
  const res = await instance.api('GET', path);
  assert.equal(res.status, 200, `${path} answered ${res.status}`);
  return Array.isArray(res.json) ? res.json : [];
}

// The narrow read selects on ForeignId, not on StashId, despite the parameter's name — so identity is
// read off the row's foreignId. Reading it off stashId would agree here by coincidence and disagree on
// the row whose two ids differ.
const identityOf = (row) => row.foreignId;

const sharedReads = {
  setSize: async () => (await rows('/api/v3/movie')).length,
  unrecognisedParam: () => rows(`/api/v3/movie?bogusParam=${SEED_IDS.sceneOne}`),
  identityOf,
};

const expectations = () => ({
  seedSize: seed.setSize,
  presentId: SEED_IDS.sceneOne,
  absentId: SEED_IDS.sceneAbsent,
});

test('the control is RED against the un-narrowed read, failing exactly parts 2 and 3', async () => {
  // The read the extension issues today, with the filter dropped and nothing else changed.
  const report = await runFilterControl(
    { ...sharedReads, byId: () => rows('/api/v3/movie') },
    expectations(),
  );

  assert.deepEqual(
    report.failedParts,
    [2, 3],
    `the inverted control must fail exactly parts 2 and 3\n${describeReport(report)}`,
  );
  assert.deepEqual(
    report.passedParts,
    [1, 4],
    `the inverted control must still pass parts 1 and 4\n${describeReport(report)}`,
  );

  // Committed with its conditions: a report without the instance version and the seeded set size it was
  // taken under cannot be compared to a later reading.
  recordArtifact('red-control-observation.json', {
    what: 'the four-part filter control applied to the un-narrowed GET /api/v3/movie',
    instanceVersion: instance.instanceVersion,
    seedSize: seed.setSize,
    presentId: SEED_IDS.sceneOne,
    absentId: SEED_IDS.sceneAbsent,
    report,
  });
});

test('the control is GREEN against the narrow read, all four parts', async () => {
  const report = await runFilterControl(
    { ...sharedReads, byId: (id) => rows(`/api/v3/movie?stashId=${id}`) },
    expectations(),
  );

  assert.ok(report.allPassed, `the narrow read must pass all four parts\n${describeReport(report)}`);
});
