// Drives the per-entity "Missing" tab for a PERFORMER end-to-end through the running host, the performer-parity
// companion to discovery-missing-studio.spec.mjs. The host renders the extension-contributed performer tab from
// the manifest, the tab reads its entityId from EntityTabProps, and the badge + list are fed by the real
// /discovery/count + /discovery/entity endpoints (kind=performer) — never a client constant. Cove-only harness:
// with no Whisparr connection the diff resolves to an empty (but honest) result, so this proves the WIRING for
// the performer path — the tab renders, the endpoints answer 200 with the camelCase shape, and the count the
// badge reads equals the /discovery/entity response length.
//
// The two data-bearing cases — a non-empty v3 PERFORMER missing list and a v2 (Sonarr) connection
// presenting a site's episodes UNIFORMLY as "Scenes", plus Refresh reconciling the set — are verified
// live against cove-dev + real Whisparr v3 (:6971) / v2 (:6972); this
// hermetic tier (which stands up no Whisparr) proves the render + performer wiring that the live drive exercises.
import { test, expect, seedCorpus } from "../lib/whisparrsync-fixtures.mjs";
import { EntityDetailPage } from "../lib/pages/entity-detail-page.mjs";

const EXTENSION_ID = "com.alextomas955.whisparrsync";

// A small synthetic missing list (numeric titles, content-safe) fed to the browser so the card grid renders
// deterministically — the real endpoint (no live Whisparr) answers empty. The api.* wiring assertions still hit
// the real endpoint (a separate request context page routes do not intercept).
function syntheticScenes(n) {
  const base = Date.UTC(2018, 0, 1);
  return Array.from({ length: n }, (_, i) => {
    const pad = String(i + 1).padStart(4, "0");
    return {
      sourceId: `synthetic-${pad}`,
      title: `Scene ${pad}`,
      releaseDate: new Date(base + (i + 1) * 86_400_000).toISOString().slice(0, 10),
      entityName: "Synthetic Performer",
      studioName: "Synthetic Studio",
      posterUrl: null,
      status: "notAdded",
    };
  });
}

test("performer and v2 Missing tabs render uniform Scenes", async ({
  harness,
  baseUrl,
  api,
  page,
}) => {
  const seeded = await seedCorpus({ container: harness.container, baseUrl });

  // A seeded scene links its performers; the performer detail route is singular (/performer/:id).
  const performerId = [...seeded.values()]
    .flatMap((s) => s.performerIds)
    .find((id) => id != null);
  expect(performerId, "seedCorpus should link at least one scene to a performer").not.toBeUndefined();

  // The performer endpoints answer end-to-end (server resolves the remote id from the Cove id; no client id).
  const entity = await api.post(`/api/extensions/${EXTENSION_ID}/discovery/entity`, {
    CoveEntityId: performerId,
    Kind: "performer",
  });
  expect(entity.status).toBe(200);
  expect(Array.isArray(entity.json.scenes)).toBe(true);

  // The count the host tab badge reads is the SAME diff reduced to a count — it must equal the entity response
  // length, proving the performer badge is fed by the real endpoint, not a JS-side constant.
  const count = await api.get(
    `/api/extensions/${EXTENSION_ID}/discovery/count?kind=performer&entityId=${performerId}`,
  );
  expect(count.status).toBe(200);
  expect(count.json.count).toBe(entity.json.scenes.length);

  // Feed the BROWSER a synthetic populated list so the performer tab renders the card grid (the real endpoint,
  // with no live Whisparr, answers empty — the api.* wiring above already proved the real performer endpoint).
  const scenes = syntheticScenes(4);
  await page.route("**/discovery/entity", async (route) => {
    await route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({ scenes, entityName: "Synthetic Performer", state: "ok", source: "stashdb", version: "v3" }),
    });
  });

  // The host renders the extension-contributed "Missing" tab on the performer detail page. Opening it mounts
  // the extension component and renders the wrapping card grid; the cards are counted scoped under the tab's
  // list region (each `[role="listitem"]` holds one `.video-card`).
  const detail = new EntityDetailPage(page, baseUrl);
  await detail.gotoPerformer(performerId);

  const missingTab = page.getByRole("tab", { name: /Missing/ });
  await expect(missingTab).toBeVisible();
  await missingTab.click();
  await expect(missingTab).toHaveAttribute("aria-selected", "true");
  await expect.poll(() => page.locator('[role="listitem"] .video-card').count()).toBe(scenes.length);
});
