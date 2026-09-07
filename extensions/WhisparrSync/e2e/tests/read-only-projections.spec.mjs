// Hermetic (Cove-only, no Whisparr container): a read-gated projection mutates nothing. A seeded video's record
// is re-read before and after the projection read and asserted unchanged, so a read route that quietly wrote to
// the library would fail here rather than in production.
//
// /import-log is the vehicle for the first case because it is the surviving pure store read: read-gated and
// answerable with no Whisparr configured. It replaced /reconciliation, which was removed along with the view
// that never shipped.
//
// The second case takes the library-wide status summary as its vehicle, because that is the read which pairs a
// Whisparr call with a Cove LIBRARY read — the one shape where a hidden write could hide, and the one neither
// unit tier sees (the C# tiers fake the HTTP boundary and never touch a real Cove DB). A one-route stub answers
// the movie list so the handler runs its whole body: transport, library fold, join, wire envelope.
import { test, expect, seedCorpus, seedVideo } from "../lib/whisparrsync-fixtures.mjs";
import { startWhisparrApiStub } from "../lib/whisparr-api-stub.mjs";

const EXTENSION_ID = "com.alextomas955.whisparrsync";

// Fabricated, and deliberately unlike any id the seeder attaches, so exactly one seeded video can join the one
// stub movie. No real id, no real title, no real key.
const SYNTHETIC_STASH_ID = "f0000000-0000-4000-8000-00000000e2e1";
const STASHDB_ENDPOINT = "https://stashdb.org/graphql";
const STUB_API_KEY = "e2e-stub-key-not-real";

test("a read-gated projection returns 200 and mutates nothing", async ({ harness, baseUrl, api }) => {
  const seeded = await seedCorpus({ container: harness.container, baseUrl });
  const [firstScene] = seeded.values();

  const before = await api.get(`/api/videos/${firstScene.coveVideoId}`);
  expect(before.status).toBe(200);
  const pathBefore = before.json.files[0].path;

  const log = await api.get(`/api/extensions/${EXTENSION_ID}/import-log`);
  expect(log.status).toBe(200);

  const after = await api.get(`/api/videos/${firstScene.coveVideoId}`);
  expect(after.status).toBe(200);
  expect(after.json.files[0].path).toBe(pathBefore);
});

test("the status summary joins a Whisparr read to the Cove library and mutates nothing", async ({
  harness,
  baseUrl,
  api,
}) => {
  // A uniquely-named video rather than the fixture corpus: this file's own first case already registered the
  // corpus at its fixed synthetic paths into this worker's shared instance, and Cove rejects a second
  // registration of the same path. One video is all the library half needs.
  const video = await seedVideo({ container: harness.container, baseUrl });

  // The join key. Attached to exactly one video, so the monitored count below names a ROW rather than counting
  // an array: any other value would leave every video notAdded and the assertion could not tell the two halves
  // apart from a Whisparr read that never happened.
  const linked = await api.put(`/api/videos/${video.id}`, {
    RemoteIds: [{ Endpoint: STASHDB_ENDPOINT, RemoteId: SYNTHETIC_STASH_ID }],
  });
  expect(linked.status).toBe(200);

  const networks = harness.container.getNetworkNames();
  expect(networks.length, "could not resolve the harness Docker network").toBeGreaterThan(0);

  const stub = await startWhisparrApiStub({
    networkName: networks[0],
    movies: [
      {
        id: 9001,
        title: "Scene 9001",
        stashId: SYNTHETIC_STASH_ID,
        itemType: "scene",
        monitored: true,
        hasFile: true,
        movieFile: { id: 1, path: "/data/media/synthetic/scene-9001.mp4" },
      },
    ],
  });

  try {
    const saved = await api.post(`/api/extensions/${EXTENSION_ID}/options`, {
      BaseUrl: stub.baseUrlFromCove,
      ApiKey: STUB_API_KEY,
      SelectedVersion: "v3",
    });
    expect(saved.status).toBe(200);

    const beforeRead = await api.get(`/api/videos/${video.id}`);
    expect(beforeRead.status).toBe(200);
    const pathBefore = beforeRead.json.files[0].path;

    // A raw fetch, not the `api` fixture: the response HEADERS are the assertion. An unmatched extension route
    // falls through to Cove's SPA catch-all, which answers 200 with `text/html` — so a status-code check alone
    // would report a route that does not exist as live.
    const res = await fetch(`${baseUrl}/api/extensions/${EXTENSION_ID}/scene-status-summary`);
    const raw = await res.text();
    expect(res.status, `unexpected body: ${raw}`).toBe(200);
    const contentType = res.headers.get("content-type") ?? "";
    expect(contentType).toContain("application/json");
    expect(contentType).not.toContain("text/html");

    const { counts } = JSON.parse(raw);
    // The library fold ran over more than the one linked video, and the Whisparr half joined exactly that one.
    expect(counts.total).toBeGreaterThan(1);
    expect(counts.monitored).toBe(1);
    expect(counts.inLibrary).toBe(1);
    expect(counts.notAdded).toBe(counts.total - 1);

    const afterRead = await api.get(`/api/videos/${video.id}`);
    expect(afterRead.status).toBe(200);
    expect(afterRead.json.files[0].path).toBe(pathBefore);
  } finally {
    await stub.stop();
  }
});
