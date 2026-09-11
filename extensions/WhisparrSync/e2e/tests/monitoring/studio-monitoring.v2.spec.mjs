// Monitoring a studio on the older generation, and the capability set the connection advertises.
//
// The monitored flag is read off the instance's own site row, never off this product's answer. Both
// directions are driven: a route writing a constant would pass either one alone.
//
// The capability list is asserted whole here rather than per capability, so one gained or lost is
// reported by this test rather than by a control that quietly stops appearing.
import { pollUntil } from "@cove-extensions/e2e/poll";

import {
  expect,
  extensionRoute,
  siteRow,
  SPEC_BUDGET_MS,
  test,
  V2_CAPABILITIES,
} from "../../lib/v2-fixture.mjs";

test.describe.configure({ timeout: SPEC_BUDGET_MS });

test("it monitors a studio, and says what else it can do", async ({ v2 }) => {
  const { api, whisparrApi, studio, seeded } = v2;

  const monitoringRoute = extensionRoute(`entity/studio/${String(studio.id)}/monitoring`);
  const read = await pollUntil(
    async () => (await api.get(monitoringRoute)).json,
    (view) => view?.refusal !== undefined,
    { timeoutMs: 120_000, label: "the entity's own monitoring read" },
  );

  expect(read.refusal, `the read refused: ${JSON.stringify(read).slice(0, 400)}`).toBe("none");
  expect(read.generation, "the read names a generation other than the connected one").toBe("v2");
  expect(read.present, "the instance holds no entry for the seeded site").toBe(true);

  // The whole list, so a capability gained or lost is reported here rather than by a control that
  // quietly stops appearing.
  expect([...read.capabilities].sort(), "the connection advertises another capability set").toEqual(
    [...V2_CAPABILITIES].sort(),
  );

  // Seeded monitored, so the first press is the one that turns it off. Both directions are driven,
  // because a route writing a constant would pass either one alone.
  expect(read.monitored, "the seeded site did not start monitored").toBe(true);

  const off = await api.post(extensionRoute(`entity/studio/${String(studio.id)}/unmonitor`), {});
  expect(off.status, `unmonitor was refused: ${off.text?.slice(0, 300)}`).toBeLessThan(400);
  expect(
    (await siteRow(whisparrApi, seeded.seriesId))?.monitored,
    "the instance's own row still reads as monitored after the unmonitor",
  ).toBe(false);

  const on = await api.post(extensionRoute(`entity/studio/${String(studio.id)}/monitor`), {});
  expect(on.status, `monitor was refused: ${on.text?.slice(0, 300)}`).toBeLessThan(400);
  expect(
    (await siteRow(whisparrApi, seeded.seriesId))?.monitored,
    "the instance's own row did not come back monitored",
  ).toBe(true);

  const after = (await api.get(monitoringRoute)).json;
  expect(after?.monitored, "the product reports a state the instance does not hold").toBe(true);
});
