// The scope of a monitored site, on the generation that keeps a row per scene under one.
//
// Both capabilities driven here belong to this generation alone: it keeps those rows, and a scope is
// what marks them. The scopes are pressed in the order that makes each observable, and the narrow
// one is observed writing before the wider one is pressed - without that, two posts that reached the
// instance and changed nothing pass.
import { pollUntil } from "@cove-extensions/e2e/poll";

import { expect, extensionRoute, sceneRows, SPEC_BUDGET_MS, test } from "../../lib/v2-fixture.mjs";

test.describe.configure({ timeout: SPEC_BUDGET_MS });

test("the wider scope marks the site's own scene rows", async ({ v2 }) => {
  const { api, whisparrApi, studio, seeded } = v2;

  // The two capabilities this generation holds that no other does: it keeps a row per scene under a
  // site, and a scope is what marks them. Both scopes are driven, in the order that makes each one
  // observable: the seeded scene is dated in the past and starts monitored, so the narrow scope is
  // what clears it and the wider one is what brings it back.
  const before = await sceneRows(whisparrApi, seeded.seriesId);
  expect(before.length, "the seeded site carries no scene row to mark").toBeGreaterThan(0);
  expect(
    before.every((row) => row.monitored === true),
    "the seeded scene rows did not start monitored, so clearing them proves nothing",
  ).toBe(true);

  const narrowed = await api.post(extensionRoute(`entity/studio/${String(studio.id)}/scope`), {
    scope: "futureScenes",
  });
  expect(
    narrowed.status,
    `the narrow scope was refused: ${narrowed.text?.slice(0, 300)}`,
  ).toBeLessThan(400);

  // Observed before the wider scope is pressed. Without it the rows below are the ones the seed
  // wrote, and two posts that reached the instance and changed nothing pass this test.
  const cleared = await pollUntil(
    () => sceneRows(whisparrApi, seeded.seriesId),
    (rows) => rows.length > 0 && rows.every((row) => row.monitored === false),
    {
      timeoutMs: 120_000,
      intervalMs: 2000,
      label: "the instance's own scene rows read as unmonitored under the narrow scope",
    },
  );
  expect(cleared.length, "the site lost its scene rows under the narrow scope").toBe(before.length);

  const widened = await api.post(extensionRoute(`entity/studio/${String(studio.id)}/scope`), {
    scope: "allScenes",
  });
  expect(
    widened.status,
    `the wider scope was refused: ${widened.text?.slice(0, 300)}`,
  ).toBeLessThan(400);

  const marked = await pollUntil(
    () => sceneRows(whisparrApi, seeded.seriesId),
    (rows) => rows.every((row) => row.monitored === true),
    {
      timeoutMs: 120_000,
      intervalMs: 2000,
      label: "the instance's own scene rows read as monitored",
    },
  );
  expect(marked.length, "the site lost its scene rows").toBe(before.length);
});
