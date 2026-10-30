// What the v2 connection says it can do, and the branch that adds a studio the instance does not
// hold.
//
// Monitoring a studio it already holds is one scenario collected on both generations, in
// entity-monitor.shared.spec.mjs. What is left here is this generation's own: the capability list,
// which differs from the other generation's in both directions, and the add branch, which exists
// because a site is the unit of presence here and arrives as a side effect of a scene add there.
//
// The capability list is asserted whole rather than per capability, so one gained or lost is
// reported by this test rather than by a control that quietly stops appearing.
import { pollUntil } from "@cove-extensions/e2e/poll";

import { V2_CAPABILITIES } from "../../lib/capability-sets.mjs";
import { expect, extensionRoute, SPEC_BUDGET_MS, test } from "../../lib/v2-fixture.mjs";

test.describe.configure({ timeout: SPEC_BUDGET_MS });

test("the connection names its generation and everything it can do", async ({ v2 }) => {
  const { api, studio } = v2;

  const monitoringRoute = extensionRoute(`entity/studio/${String(studio.id)}/monitoring`);
  const read = await pollUntil(
    async () => (await api.get(monitoringRoute)).json,
    (view) => view?.refusal !== undefined,
    { timeoutMs: 120_000, label: "the entity's own monitoring read" },
  );

  expect(read.refusal, `the read refused: ${JSON.stringify(read).slice(0, 400)}`).toBe("none");
  expect(read.generation, "the read names a generation other than the connected one").toBe("v2");
  expect(read.present, "the instance holds no entry for the seeded site").toBe(true);

  expect([...read.capabilities].sort(), "the connection advertises another capability set").toEqual(
    [...V2_CAPABILITIES].sort(),
  );
});

test("it adds a studio the instance does not hold, and monitors what it added", async ({ v2 }) => {
  const { api, whisparrApi, unheldStudio, unheldSiteId } = v2;

  // The path a reader meets first, and the one nothing drove: every other monitoring spec seeds the
  // entity into the instance before pressing, so all of them take the already-held branch. Pressing
  // on an entity the instance holds nothing for is what makes the product add it.
  const before = (await whisparrApi.get("/api/v3/series")).json ?? [];
  expect(
    before.filter((one) => one.tvdbId === unheldSiteId),
    "the instance already holds the studio this test is about",
  ).toEqual([]);

  const pressed = await api.post(
    extensionRoute(`entity/studio/${String(unheldStudio.id)}/monitor`),
    {},
  );
  expect(pressed.status, `monitor was refused: ${pressed.text?.slice(0, 300)}`).toBeLessThan(400);
  expect(
    pressed.json?.refusal ?? "none",
    `the press named a reason rather than acting: ${pressed.text?.slice(0, 300)}`,
  ).toBe("none");

  // The instance's own catalogue. A route that answered 200 having added nothing would pass every
  // assertion made against its answer.
  const added = await pollUntil(
    async () => (await whisparrApi.get("/api/v3/series")).json ?? [],
    (rows) => rows.some((one) => one.tvdbId === unheldSiteId),
    {
      timeoutMs: 180_000,
      intervalMs: 2000,
      label: "the instance holds the studio the press added",
    },
  );

  const created = added.find((one) => one.tvdbId === unheldSiteId);
  expect(
    created?.monitored,
    "the press added the studio and left it unmonitored, so the reader pressed monitor and got a row that wants nothing",
  ).toBe(true);
});
