// A folder answer belongs to the instance that gave it, driven against one Cove holding both
// generations.
//
// WHAT THIS ESTABLISHES. That a path stated and stored against one generation is not reported for
// the other, and that switching back returns the first generation's own answer.
//
// WHY IT IS NOT PART OF THE SHARED FOLDER-MAPPING SPEC. That spec parameterises on the generation
// with `test.use`, which gives each its own Cove installation. Both generations are never in one
// installation there, and the selection never moves, so the defect this covers cannot arise in it.
//
// THE ARRANGEMENT. The stored blob is written directly through Cove's own bulk data route rather
// than through a probe. What is under test is which generation an answer is filed under and read
// back from, not whether a probe resolves, and the probe path is already covered.
//
// IF THIS SPEC GOES RED, read the run log for a container-not-running line before debugging the
// product. A red end-to-end run in this repository is usually the Cove container dying.
import { expect } from "@cove-extensions/e2e";

import { instanceSettings, storedOptions, writeOptions } from "../../lib/steps.mjs";
import { isolatedCoveFixture, test as base } from "../../lib/whisparr-sync-fixtures.mjs";

const COVE_ROOT = "/cove-library";
const V3_PATH = "/library";
const V2_PATH = "/i-downloads-p";

const test = base.extend({
  isolatedHarness: isolatedCoveFixture(),
});

const mappingFor = (instanceRoot) => ({
  OutboundMappings: [{ CoveRoot: COVE_ROOT, InstanceRoot: instanceRoot }],
  RootsEstablished: true,
});

test("a folder settled on one generation is not settled on the other", async ({ api }) => {
  await writeOptions(api, {
    SelectedGeneration: "v3",
    InstanceSettingsV3: mappingFor(V3_PATH),
  });

  const onV3 = await storedOptions(api);

  expect(
    instanceSettings(onV3, "v3").OutboundMappings?.[0]?.InstanceRoot,
    "v3 did not keep the path stated for it",
  ).toBe(V3_PATH);

  expect(
    instanceSettings(onV3, "v2").OutboundMappings ?? [],
    "v2 was handed the path v3 answered for, which is the defect this covers",
  ).toEqual([]);
});

test("each generation keeps its own answer across a switch", async ({ api }) => {
  await writeOptions(api, {
    SelectedGeneration: "v3",
    InstanceSettingsV3: mappingFor(V3_PATH),
  });

  // Switching and answering on the other generation must leave the first one's answer alone.
  const held = await storedOptions(api);
  await writeOptions(api, {
    ...held,
    SelectedGeneration: "v2",
    InstanceSettingsV2: mappingFor(V2_PATH),
  });

  const onV2 = await storedOptions(api);
  expect(
    instanceSettings(onV2).OutboundMappings?.[0]?.InstanceRoot,
    "the selected generation did not read its own answer",
  ).toBe(V2_PATH);

  await writeOptions(api, { ...onV2, SelectedGeneration: "v3" });

  const backOnV3 = await storedOptions(api);
  expect(
    instanceSettings(backOnV3).OutboundMappings?.[0]?.InstanceRoot,
    "switching back did not return v3's own answer",
  ).toBe(V3_PATH);
  expect(
    instanceSettings(backOnV3, "v2").OutboundMappings?.[0]?.InstanceRoot,
    "v2 lost its answer when the selection moved away from it",
  ).toBe(V2_PATH);
});

test("the settings page reports the folders of the generation in use", async ({
  api,
  page,
  baseUrl,
}) => {
  await writeOptions(api, {
    SelectedGeneration: "v2",
    InstanceSettingsV3: mappingFor(V3_PATH),
    InstanceSettingsV2: mappingFor(V2_PATH),
  });

  const answered = page.waitForResponse(
    (response) =>
      new URL(response.url()).pathname.endsWith("/addressing/folder-mappings") &&
      response.request().method() === "GET",
  );
  await page.goto(`${baseUrl}/settings/whisparr-sync`);
  const body = await (await answered).json().catch(() => null);

  // The line carries the stated path as `mapping`, which is the name the wire document declares.
  const roots = body?.roots ?? [];
  expect(
    roots.map((line) => line.mapping),
    `the read answered ${JSON.stringify(body)}, which does not name v2's own path`,
  ).toContain(V2_PATH);
  expect(
    roots.map((line) => line.mapping),
    "the read carried v3's path while v2 is the generation in use",
  ).not.toContain(V3_PATH);
});
