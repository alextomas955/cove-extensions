// v2 draws no scene tab on a video detail page, in a real containerized host.
//
// The absence comes from the registration rather than from a component. On this generation the
// video-page tab never reaches the manifest the host serves, so the page draws neither this
// extension's tab nor the host's own placeholder for a contributed tab it could not fill. Those are
// different DOM states, and only the first is what this generation promises.
//
// The manifest is read before the page, because which tab strip a detail page draws follows its
// viewport while a registration does not.
//
// IF THIS SPEC GOES RED, read the run log for a container-not-running line before debugging the UI.
// A red end-to-end run in this repository is usually the Cove container dying rather than the page
// under test.
import { randomUUID } from "node:crypto";

import {
  expect,
  EXTENSION_ID,
  seedCoveVideo,
  SETTLE_DWELL_MS,
  SPEC_BUDGET_MS,
  STASHDB_ENDPOINT,
  test,
} from "../../lib/connected-fixture.mjs";
import { visit } from "../../lib/steps.mjs";

// The tab's label, transcribed by hand from the manifest that advertises it. A spec importing the
// same constant the manifest declares would be asserting that a string equals itself.
const TAB_LABEL = "Whisparr";

test.describe.configure({ timeout: SPEC_BUDGET_MS });
test.use({ generation: "v2" });

/** The tab, by the only name the host draws it under. */
const whisparrTab = (page) => page.getByRole("tab", { name: TAB_LABEL, exact: true }).first();

/** The host's own detail-tab strip, which tells a page that has rendered from one still loading. */
const hostDetailTabs = (page) => page.getByRole("tablist").first();

/**
 * The host's own placeholder for a contributed tab whose component it could not resolve.
 *
 * The empty-versus-absent distinction in its tab form. A registration the host kept and could not
 * fill draws this; a registration that never reached the manifest draws nothing at all.
 */
const unresolvedExtensionComponent = (page) => page.getByText(/Extension component not found/i);

/** Every video-page tab this extension registers in the manifest the browser is served. */
async function registeredVideoTabs(api) {
  const manifest = await api.get("/api/extensions/manifest");
  expect(manifest.status, `GET the extension manifest answered ${String(manifest.status)}`).toBe(
    200,
  );
  return (manifest.json?.tabs ?? [])
    .filter((entry) => entry.extensionId === EXTENSION_ID && entry.pageType === "video")
    .map((entry) => entry.key);
}

test("v2 draws no scene tab, and no wrapper for one either", async ({
  page,
  baseUrl,
  connected,
}) => {
  const { api: coveApi } = connected;

  // No entry on the instance and none needed. Nothing is asked of it on this generation, and a
  // seeded entry would make an absent tab look like a tab with nothing to say.
  const video = await seedCoveVideo(coveApi, {
    title: `V2 ${randomUUID().slice(0, 8)}`,
    remoteIds: [{ endpoint: STASHDB_ENDPOINT, remoteId: randomUUID() }],
  });

  expect(
    await registeredVideoTabs(coveApi),
    "v2 registers a video-page tab, so a surface it has no meaning on reached the manifest the host served",
  ).toEqual([]);

  await visit(
    page,
    baseUrl,
    `/video/${String(video.id)}`,
    hostDetailTabs(page),
    "the video detail page on v2",
  );
  await page.waitForTimeout(SETTLE_DWELL_MS);

  await expect(
    whisparrTab(page),
    `v2 drew a ${TAB_LABEL} tab on the video detail page, so the registration is not conditional on the stored generation`,
  ).toHaveCount(0);

  await expect(
    unresolvedExtensionComponent(page),
    "the host drew its placeholder for a contributed tab it could not resolve, so a tab surface renders empty rather than being absent",
  ).toHaveCount(0);
});
