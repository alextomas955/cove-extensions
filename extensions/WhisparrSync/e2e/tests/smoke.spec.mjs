// Smoke test — proves the whole WhisparrSync E2E scaffold works end to end: the built extension installs
// into a fresh Cove container and reports enabled with UI + API, the synthetic fixture corpus seeds into
// Cove through its real API, and the extension's own /status endpoint answers even with no Whisparr
// configured. Deliberately HERMETIC: it never requests the `whisparr` fixture, so it needs only the Cove
// container (this is the CI-green, hermetic subset of the E2E suite).
import { startHarness } from '@cove-extensions/e2e/harness';
import { test as base, expect, seedCorpus, WHISPARRSYNC_EXTENSION } from '../lib/whisparrsync-fixtures.mjs';

const EXTENSION_ID = 'com.alextomas955.whisparrsync';

// The unconfigured-/status assertion below needs an instance nothing has configured. The default harness
// is WORKER-scoped: per-test data is uniquely named and safe to share, but the extension's OPTIONS are one
// blob per instance, so a sibling spec that saves a connection makes "no Whisparr configured" false for
// everything after it in that worker. That went unnoticed while the suite could not load the extension at
// all and only a handful of specs ran. A fresh instance is the honest fixture for a claim about the
// unconfigured state; the rest of this file keeps the shared harness.
const test = base.extend({
  isolatedHarness: [
    async ({}, use) => {
      const isolatedHarness = await startHarness();
      isolatedHarness.owner = await isolatedHarness.bootstrapOwner();
      await isolatedHarness.installExtension(WHISPARRSYNC_EXTENSION);
      await use(isolatedHarness);
      await isolatedHarness.stop();
    },
    { scope: 'test' },
  ],
});

test('extension installs and reports enabled with UI + API', async ({ api }) => {
  const { json } = await api.get('/api/extensions');
  const whisparrSync = json.find((e) => e.id === EXTENSION_ID);
  expect(whisparrSync).toBeTruthy();
  expect(whisparrSync.enabled).toBe(true);
  expect(whisparrSync.hasUI).toBe(true);
  expect(whisparrSync.hasApi).toBe(true);
});

test('the extension answers /status with no Whisparr configured', async ({ isolatedHarness }) => {
  const res = await fetch(`${isolatedHarness.baseUrl}/api/extensions/${EXTENSION_ID}/status`);
  expect(res.status).toBe(200);
  // With nothing configured the honest projection is "not configured" — but the extension still responds.
  expect(await res.json()).toMatchObject({ configured: false });
});

test('the five synthetic scenes seed into Cove as videos', async ({ harness, baseUrl, api }) => {
  const seeded = await seedCorpus({ container: harness.container, baseUrl });
  expect(seeded.size).toBe(5);

  // Every seeded scene resolves to a real Cove video record (ids resolve — the corpus is genuinely present).
  for (const [sceneId, entry] of seeded) {
    const video = await api.get(`/api/videos/${entry.coveVideoId}`);
    expect(video.status, `scene ${sceneId} video ${entry.coveVideoId} resolves`).toBe(200);
  }
});
