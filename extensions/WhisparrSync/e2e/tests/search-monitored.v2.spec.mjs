// Asking the older instance to search what an entity monitors.
//
// The one gesture on this surface that downloads. It is safe to press because the fixture instance
// holds no indexer and no download client, so a search it starts has nowhere to search. The only
// evidence the request arrived is the instance's own command queue, which is read for a bound before
// the press and for the command after it.
import { pollUntil } from "@cove-extensions/e2e/poll";

import { commandNames, expect, extensionRoute, SPEC_BUDGET_MS, test } from "../lib/v2-fixture.mjs";

test.describe.configure({ timeout: SPEC_BUDGET_MS });

test("it asks the instance to search what an entity monitors", async ({ v2 }) => {
  const { api, whisparrApi, studio } = v2;

  // The negative first. The instance runs commands of its own accord, so a search name present
  // before the press would make the assertion after it meaningless.
  const before = await commandNames(whisparrApi);
  expect(
    before.filter((name) => /search/i.test(name)),
    `the instance had already been asked to search: ${before.join(", ")}`,
  ).toEqual([]);

  const asked = await api.post(
    extensionRoute(`entity/studio/${String(studio.id)}/search-all-monitored`),
    {},
  );
  expect(asked.status, `the search was refused: ${asked.text?.slice(0, 300)}`).toBeLessThan(400);

  // Read off the instance's own queue. This is the one gesture on this surface that downloads, and
  // the only evidence that it reached the instance is the instance saying it was asked.
  const queued = await pollUntil(
    () => commandNames(whisparrApi),
    (names) => names.some((name) => /search/i.test(name)),
    {
      timeoutMs: 120_000,
      intervalMs: 2000,
      label: "the instance records a search command",
    },
  );
  expect(
    queued.filter((name) => /search/i.test(name)).length,
    "the instance was never asked to search",
  ).toBeGreaterThan(0);
});
