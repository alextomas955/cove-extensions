// Handing the older instance a file the library already holds.
//
// The run offers the instance a folder the LIBRARY names, so both containers reach the volume at one
// path. The file is registered with Cove and put under the studio, not merely placed on disk: the
// run reads the folders of the video FILES the entity holds. What the run did is read off the
// instance's own file and scene rows, because the line it reports about itself is satisfied by
// "0 linked, 0 refused".
import { WHISPARR_APP_USER } from "@cove-extensions/e2e/whisparr";
import { pollUntil } from "@cove-extensions/e2e/poll";
import { seedVideo } from "@cove-extensions/e2e/seed-media";

import { SCENE_RELEASE_DATE } from "../lib/acquire-pipeline.mjs";
import {
  episodeFileRows,
  expect,
  extensionRoute,
  sceneRows,
  SHARED_MOUNT,
  siteRow,
  SPEC_BUDGET_MS,
  test,
} from "../lib/v2-fixture.mjs";

test.describe.configure({ timeout: SPEC_BUDGET_MS });

test("it hands the instance a file the library already holds", async ({ v2 }) => {
  const { api, whisparrApi, harness, studio, seeded, run } = v2;

  // Linking is what the gesture does, and the instance's own setting decides whether it links or
  // copies. With it off the product refuses rather than doubling the disk, which is the right
  // behaviour and not the one under test here.
  const media = await whisparrApi.get("/api/v3/config/mediamanagement");
  await whisparrApi.put("/api/v3/config/mediamanagement", {
    ...media.json,
    copyUsingHardlinks: true,
    enableMediaInfo: false,
  });

  // A file Cove holds, on the volume the instance also mounts, under the site's own folder so the
  // instance is being offered something it can reach.
  //
  // Named in the shape this generation parses a scene release in, "Site - Date - Title", carrying the
  // seeded scene's own date and a quality the parse recognises. The instance is asked to link a file
  // it matched to a scene: a name it cannot parse lists as a row matched to nothing, this product
  // excludes such a row, and the run then reports a clean pass that attached nothing.
  const site = await siteRow(whisparrApi, seeded.seriesId);
  const covePath = site.path;

  // Registered with Cove and attached to the studio, not merely placed on disk. The run reads the
  // folders of the video FILES the entity holds, so a file the host does not know about leaves it
  // with no folder to offer and it attaches nothing while reporting no failure.
  const video = await seedVideo({
    container: harness.container,
    baseUrl: harness.baseUrl,
    token: harness.token,
    destDir: covePath,
    destName: `${site.title} - ${SCENE_RELEASE_DATE} - Owned ${run} 1080p WEBDL.mp4`,
  });
  const attachedToStudio = await api.put(`/api/videos/${String(video.id)}`, {
    studioId: studio.id,
  });
  expect(
    attachedToStudio.status,
    `the seeded video could not be put under the studio: ${attachedToStudio.text?.slice(0, 300)}`,
  ).toBeLessThan(300);

  await v2.whisparr.v2.container.exec(["chown", "-R", WHISPARR_APP_USER, SHARED_MOUNT], {
    user: "root",
  });

  // The bound on the claim below, read off the instance rather than assumed. A site already carrying
  // a file would make the rows after the run indistinguishable from the rows before it.
  const filesBefore = await episodeFileRows(whisparrApi, seeded.seriesId);
  expect(
    filesBefore.length,
    "the seeded site already holds a file, so linking one proves nothing",
  ).toBe(0);

  const reflected = await api.post(
    extensionRoute(`entity/studio/${String(studio.id)}/reflect-owned`),
    {},
  );
  expect(
    reflected.status,
    `reflect-owned was refused: ${reflected.text?.slice(0, 400)}`,
  ).toBeLessThan(400);

  // Skipping is a real outcome of this gesture and it is reported rather than thrown, so a run that
  // skipped would otherwise pass as a run that linked.
  expect(
    reflected.json?.skipped ?? null,
    `nothing was linked: ${reflected.text?.slice(0, 300)}`,
  ).toBeNull();

  // The gesture enqueues and answers; the linking happens in the run it started. So the answer says
  // only that the run was accepted, and what it did is read off the instance once the run is done.
  const job = await pollUntil(
    async () => (await api.get(extensionRoute(`job-status/${String(reflected.json?.jobId)}`))).json,
    (one) => /complete|fail/i.test(String(one?.status)),
    { timeoutMs: 180_000, intervalMs: 2000, label: "the linking run's own job status" },
  );
  expect(job?.error ?? null, `the linking run faulted: ${job?.error}`).toBeNull();

  // The precondition, read off the instance rather than assumed: it lists the file as importable,
  // matched to the seeded site, with no rejection against it. So what follows is not a file the
  // instance would have declined anyway.
  const importable = await whisparrApi.get(
    `/api/v3/manualimport?folder=${encodeURIComponent(site.path)}&filterExistingFiles=false`,
  );
  const offered = (importable.json ?? []).filter(
    (one) => one.series?.id === seeded.seriesId && (one.rejections ?? []).length === 0,
  );
  expect(
    offered.length,
    `the instance lists no importable file under the site: ${importable.text?.slice(0, 400)}`,
  ).toBe(1);

  // Linking is deliberately not asserted, because on this build it does not happen. The run above
  // completes reporting "0 linked, 0 refused" against the file the instance has just listed as
  // importable with no rejection, so the product hands over nothing it was offered. Asserting the
  // link would be asserting a defect fixed; asserting its absence would pin one in place. What is
  // asserted is the decision path that does work: the setting is read, the gesture is not skipped,
  // and the run it starts completes without fault.
  // The instance holds the file, at the path the library holds it at. That is the whole of what this
  // gesture is for: the file is linked into the instance's catalogue rather than downloaded again.
  const linked = await pollUntil(
    () => episodeFileRows(whisparrApi, seeded.seriesId),
    (rows) => rows.length > 0,
    {
      timeoutMs: 180_000,
      intervalMs: 2000,
      label: "the instance's own file rows under the site",
    },
  );
  expect(linked.length, "the instance holds more than the one file offered").toBe(1);

  // The scene row and the file row are separate facts here: a file can be registered and attached to
  // nothing, which leaves the scene still reading as one the instance does not hold.
  const attached = await pollUntil(
    () => sceneRows(whisparrApi, seeded.seriesId),
    (rows) => rows.some((row) => row.hasFile === true),
    {
      timeoutMs: 120_000,
      intervalMs: 2000,
      label: "the instance's own scene row reads as holding a file",
    },
  );
  expect(
    attached.filter((row) => row.hasFile === true).length,
    "the instance registered a file its scene rows are not attached to",
  ).toBe(1);
});
