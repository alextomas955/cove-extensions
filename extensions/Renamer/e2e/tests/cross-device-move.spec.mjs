// A move from /data into /data2 crosses filesystems: /data2 is a tmpfs mount, so the kernel raises
// EXDEV for a plain rename and the volume classifier keys it as a second volume. The move must go
// through the copy-verify-delete path and land the file at the new path with nothing left behind.
import { test, expect, seedVideo, EXTENSION_ID, ROUTE } from "../lib/renamer-fixtures.mjs";
import { pollRenamerJob } from "../lib/poll-renamer-job.mjs";

async function fileExists(container, path) {
  const probe = await container.exec(["test", "-f", path]);
  return probe.exitCode === 0;
}

test("a move onto another filesystem copies, verifies, and removes the source", async ({
  harness,
  baseUrl,
  api,
}) => {
  // The container restart before each spec leaves /data2 root-owned with no world write.
  await harness.container.exec(["chown", "cove:cove", "/data2"], { user: "root" });

  const video = await seedVideo({ container: harness.container, baseUrl });
  const originalPath = video.files[0].path;

  const put = await api.put(
    `/api/extensions/${EXTENSION_ID}/data/options`,
    JSON.stringify({ FolderRoot: "/data2" }),
  );
  expect(put.ok).toBe(true);

  try {
    const enqueue = await api.post(`${ROUTE}/renamer`, {
      EntityType: "video",
      EntityIds: [video.id],
    });
    expect(enqueue.status).toBe(202);

    const job = await pollRenamerJob(api, ROUTE, enqueue.json.jobId);
    expect(job.status.toLowerCase()).toBe("completed");

    const after = await api.get(`/api/videos/${video.id}`);
    const finalPath = after.json.files[0].path;

    expect(finalPath.startsWith("/data2/"), `the file stayed at ${finalPath}`).toBe(true);
    expect(await fileExists(harness.container, finalPath), `no file at ${finalPath}`).toBe(true);
    expect(
      await fileExists(harness.container, originalPath),
      `source left at ${originalPath}`,
    ).toBe(false);
  } finally {
    // The options are shared by every later spec on this worker.
    const reset = await api.put(
      `/api/extensions/${EXTENSION_ID}/data/options`,
      JSON.stringify({ FolderRoot: "" }),
    );
    expect(reset.ok, `restoring FolderRoot returned ${reset.status}`).toBe(true);
  }
});
