// Smoke test — proves the whole WhisparrSync E2E scaffold works end to end: the built extension installs
// into a fresh Cove container and reports enabled with UI + API, the synthetic fixture corpus seeds into
// Cove through its real API, and the extension's own /status endpoint answers even with no Whisparr
// configured. Deliberately HERMETIC: it never requests the `whisparr` fixture, so it needs only the Cove
// container (this is the CI-green, hermetic subset of the E2E suite).
import { test, expect, seedCorpus } from '../lib/whisparrsync-fixtures.mjs';

const EXTENSION_ID = 'com.alextomas955.whisparrsync';

test('extension installs and reports enabled with UI + API', async ({ api }) => {
  const { json } = await api.get('/api/extensions');
  const whisparrSync = json.find((e) => e.id === EXTENSION_ID);
  expect(whisparrSync).toBeTruthy();
  expect(whisparrSync.enabled).toBe(true);
  expect(whisparrSync.hasUI).toBe(true);
  expect(whisparrSync.hasApi).toBe(true);
});

test('the extension answers /status with no Whisparr configured', async ({ api }) => {
  const status = await api.get(`/api/extensions/${EXTENSION_ID}/status`);
  expect(status.status).toBe(200);
  // With nothing configured the honest projection is "not configured" — but the extension still responds.
  expect(status.json).toMatchObject({ configured: false });
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
