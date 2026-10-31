import { test, expect, beforeEach, vi } from "vitest";

import { resetServer, server } from "./testRun";
import { answered } from "./testRunOptions";

vi.mock("@cove-extensions/ui-shared/extensionRequest", async () => {
  const { extensionRequestDouble } = await import("./testRun");
  return extensionRequestDouble();
});

const { fingerprintBody, identifyBody, imagesBody, scanBody, startHostStage } =
  await import("./hostJobs");

beforeEach(resetServer);

test("the scan body asks for the two fingerprint passes and nothing else", () => {
  // The key set is pinned, not just the values: a key added by a careless spread would turn a
  // hash-only pass into an encode of the whole library, and `scanGenerators` alone turns on every
  // generator Cove has.
  expect(scanBody(answered({ rescan: true }))).toEqual({
    scanGenerateMd5: true,
    scanGeneratePhashes: true,
    rescan: true,
  });
});

test("the scan body carries the rescan switch the run was claimed with", () => {
  expect(scanBody(answered({ rescan: false })).rescan).toBe(false);
});

test("starting the scan posts to Cove's own scan route and answers the job id it minted", async () => {
  server.hostJobId = "host-scan-7";

  const jobId = await startHostStage("scan", answered());

  expect(jobId).toBe("host-scan-7");
  expect(server.calls).toEqual([
    {
      path: "/metadata/scan",
      method: "POST",
      body: { scanGenerateMd5: true, scanGeneratePhashes: true, rescan: false },
    },
  ]);
});

test("the fingerprint body asks for the five hashes and turns the host's thumbnail default off", () => {
  // Every expected value below is copied from Cove's own GenerateOptionsDto and IdentifyOptionsDto.
  // `thumbnails` is the one member of Cove's generate options that defaults true, so it is asserted
  // present and false: absent and false are the two outcomes that differ here, and absent would
  // encode a cover for every video in the library during a hash-only pass.
  expect(fingerprintBody()).toEqual({
    md5: true,
    phashes: true,
    imagePhashes: true,
    audioPhashes: true,
    textPhashes: true,
    thumbnails: false,
    overwrite: false,
  });
});

test("the identify body drives all three create flags from the one switch", () => {
  expect(identifyBody(answered({ createMissingEntities: false, markOrganized: true }))).toEqual({
    createTags: false,
    createPerformers: false,
    createStudios: false,
    markOrganized: true,
  });
});

test("the images body carries the six visual switches and refuses to overwrite", () => {
  expect(
    imagesBody(
      answered({
        thumbnails: true,
        previews: false,
        sprites: true,
        vrStereo: true,
        imageThumbnails: false,
        galleryThumbnails: true,
      }),
    ),
  ).toEqual({
    thumbnails: true,
    previews: false,
    sprites: true,
    vrStereo: true,
    imageThumbnails: false,
    galleryThumbnails: true,
    overwrite: false,
  });
});

test("the images body is still a request when every visual switch is off", () => {
  expect(
    imagesBody(
      answered({
        thumbnails: false,
        previews: false,
        sprites: false,
        vrStereo: false,
        imageThumbnails: false,
        galleryThumbnails: false,
      }),
    ),
  ).toEqual({
    thumbnails: false,
    previews: false,
    sprites: false,
    vrStereo: false,
    imageThumbnails: false,
    galleryThumbnails: false,
    overwrite: false,
  });
});

test("the fingerprint and images stages post to the same generate route with their own bodies", async () => {
  await startHostStage("fingerprints", answered());
  await startHostStage("images", answered());

  expect(server.calls.map((call) => call.path)).toEqual([
    "/metadata/generate",
    "/metadata/generate",
  ]);
  expect(server.calls[0].body).toEqual(fingerprintBody());
  expect(server.calls[1].body).toEqual(imagesBody(answered()));
});

test("starting identify posts to Cove's own identify route", async () => {
  server.hostJobId = "host-identify-3";

  const jobId = await startHostStage("identify", answered());

  expect(jobId).toBe("host-identify-3");
  expect(server.calls).toEqual([
    {
      path: "/metadata/identify",
      method: "POST",
      body: {
        createTags: true,
        createPerformers: true,
        createStudios: true,
        markOrganized: false,
      },
    },
  ]);
});
