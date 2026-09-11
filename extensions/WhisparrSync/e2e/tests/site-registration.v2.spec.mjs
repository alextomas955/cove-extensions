// Registering a site the older instance does not hold, which no other generation can do.
//
// A site is the unit of presence on this generation, so a library run registers sites rather than
// scenes. What is asserted is the instance's own catalogue afterwards, and that the registration is
// presence only: one that monitored the catalogue it brought with it would want every scene in the
// site, which is the opposite of what registering presence is for.
import { pollUntil } from "@cove-extensions/e2e/poll";

import {
  expect,
  extensionRoute,
  seedCoveVideo,
  SPEC_BUDGET_MS,
  test,
  THEPORNDB_ENDPOINT,
} from "../lib/v2-fixture.mjs";

test.describe.configure({ timeout: SPEC_BUDGET_MS });

test("it registers a site the instance does not hold", async ({ v2 }) => {
  const { api, whisparrApi, unheldStudio, unheldSiteId, unheldTitle, run } = v2;

  // A site is the unit of presence on this generation, and registering one is a capability no other
  // generation has. Cove has to own something under it for a run to have anything to register. The
  // identifier is a number, not a UUID: this generation names a scene by the number its metadata
  // source issued.
  await seedCoveVideo(api, {
    title: `Owned ${run}`,
    studioId: unheldStudio.id,
    remoteIds: [{ endpoint: THEPORNDB_ENDPOINT, remoteId: String(unheldSiteId + 1) }],
  });

  const listedBefore = (await whisparrApi.get("/api/v3/series")).json ?? [];
  expect(
    listedBefore.filter((one) => one.tvdbId === unheldSiteId),
    "the instance already holds the site this test is about",
  ).toEqual([]);

  const started = await api.post(extensionRoute("sync/run"), { alsoMonitor: false });
  expect(started.status, `the run was refused: ${started.text?.slice(0, 400)}`).toBeLessThan(400);
  expect(
    started.json?.refusal ?? "none",
    `the run refused before it started: ${started.text?.slice(0, 300)}`,
  ).toBe("none");

  const job = await pollUntil(
    async () => (await api.get(extensionRoute(`job-status/${String(started.json?.jobId)}`))).json,
    (one) => /complete|fail/i.test(String(one?.status)),
    { timeoutMs: 180_000, intervalMs: 2000, label: "the run's own job status" },
  );

  // Asserted before waiting on the effect: the instance-side poll below takes minutes to fail and
  // says only that nothing arrived, while this says what the run decided.
  expect(job?.error ?? null, `the run faulted: ${job?.error}`).toBeNull();
  expect(job?.entitiesTotal ?? 0, `the run considered no site: ${job?.summary}`).toBe(2);
  expect(job?.entitiesRefused ?? 0, `the run refused a site: ${job?.summary}`).toBe(0);
  expect(
    job?.entitiesPassedOver ?? 0,
    `the run did not recognise the site the instance already holds: ${job?.summary}`,
  ).toBe(1);
  expect(job?.entitiesApplied ?? 0, `the run registered nothing: ${job?.summary}`).toBe(1);

  const registered = await pollUntil(
    async () => (await whisparrApi.get("/api/v3/series")).json ?? [],
    (rows) => rows.some((one) => one.tvdbId === unheldSiteId),
    {
      timeoutMs: 180_000,
      intervalMs: 2000,
      label: "the instance holds the site the run registered",
    },
  );

  const created = registered.find((one) => one.tvdbId === unheldSiteId);
  expect(created?.title, "the registered site carries another title").toBe(unheldTitle);

  // Presence only. A registration that monitored the catalogue it brought with it would want every
  // scene in that site, which is the opposite of what registering presence is for.
  expect(created?.monitored, "registering a site monitored it as well").toBe(false);
});
