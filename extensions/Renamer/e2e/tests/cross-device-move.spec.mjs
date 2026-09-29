// A move from /data into /data2 crosses filesystems: /data2 is a tmpfs mount, so the kernel raises
// EXDEV for a plain rename and the volume classifier keys it as a second volume. The move must go
// through the copy-verify-delete path and land the file at the new path with nothing left behind.
import {
  test,
  expect,
  seedVideo,
  pollUntil,
  EXTENSION_ID,
  ROUTE,
} from "../lib/renamer-fixtures.mjs";
import { pollRenamerJob } from "../lib/poll-renamer-job.mjs";
import { fileExists } from "../lib/rename-assertions.mjs";

/** The device id each path's filesystem reports inside the Cove container. */
async function deviceIds(container, paths) {
  const probe = await container.exec(["stat", "-L", "-c", "%d", ...paths]);
  expect(probe.exitCode, `stat failed: ${probe.output}`).toBe(0);
  return probe.stdout.trim().split(/\s+/);
}

test("a move onto another filesystem copies, verifies, and removes the source", async ({
  harness,
  baseUrl,
  api,
  restoredOptions: _restoredOptions,
}) => {
  // The premise, asserted rather than trusted to the compose file: on one filesystem the same move is
  // a plain rename(), and every assertion below would pass without the copy path ever running.
  const [dataDevice, data2Device] = await deviceIds(harness.container, ["/data", "/data2"]);
  expect(
    data2Device,
    "/data and /data2 report the same device, so this move would not cross filesystems",
  ).not.toBe(dataDevice);

  // The container restart before each spec leaves /data2 root-owned with no world write.
  await harness.container.exec(["chown", "cove:cove", "/data2"], { user: "root" });

  const video = await seedVideo({ container: harness.container, baseUrl });
  const originalPath = video.files[0].path;

  const put = await api.put(
    `/api/extensions/${EXTENSION_ID}/data/options`,
    JSON.stringify({ FolderRoot: "/data2" }),
  );
  expect(put.ok).toBe(true);

  const enqueue = await api.post(`${ROUTE}/renamer`, {
    EntityType: "video",
    EntityIds: [video.id],
  });
  expect(enqueue.status).toBe(202);

  const job = await pollRenamerJob(api, ROUTE, enqueue.json.jobId);
  expect(job.status.toLowerCase()).toBe("completed");

  // Polled: the record behind a completed job is not guaranteed read-your-writes on the next request.
  const after = await pollUntil(
    () => api.get(`/api/videos/${video.id}`).then((r) => r.json),
    (v) => v.files[0].path.startsWith("/data2/"),
    { label: `video ${video.id} to move under /data2` },
  );
  const finalPath = after.files[0].path;

  expect(await fileExists(harness.container, finalPath), `no file at ${finalPath}`).toBe(true);
  expect(await fileExists(harness.container, originalPath), `source left at ${originalPath}`).toBe(
    false,
  );
});
