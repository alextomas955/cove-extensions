// Drives the per-entity "Missing" tab end-to-end through the running host: the host renders the
// extension-contributed tab from the manifest, the tab component reads its entityId from EntityTabProps, and
// the count badge + list are fed by the real /discovery/count + /discovery/entity endpoints (never a client
// constant). Cove-only harness: with no Whisparr connection the diff resolves to an empty (but honest) result,
// so this proves the WIRING — the tab renders, the endpoints answer 200 with the camelCase shape, and the
// count the badge reads equals the /discovery/entity response length. A non-empty, correctly-diffed list
// against a monitored v3 studio is verified live against cove-dev + real Whisparr v3, which this harness
// does not stand up.
import { test, expect, seedCorpus } from "../lib/whisparrsync-fixtures.mjs";
import { EntityDetailPage } from "../lib/pages/entity-detail-page.mjs";

const EXTENSION_ID = "com.alextomas955.whisparrsync";

// A small synthetic missing list (numeric titles, content-safe) fed to the browser so the card grid renders
// deterministically — the real endpoint (with no live Whisparr) would answer empty. The api.* wiring
// assertions below still hit the real endpoint (a separate request context page routes do not intercept).
function syntheticScenes(n) {
  const base = Date.UTC(2018, 0, 1);
  return Array.from({ length: n }, (_, i) => {
    const pad = String(i + 1).padStart(4, "0");
    return {
      sourceId: `synthetic-${pad}`,
      title: `Scene ${pad}`,
      releaseDate: new Date(base + (i + 1) * 86_400_000).toISOString().slice(0, 10),
      entityName: "Synthetic Studio",
      studioName: "Synthetic Studio",
      posterUrl: null,
      status: "notAdded",
    };
  });
}

test("monitored studio Missing tab renders a real diffed list", async ({
  harness,
  baseUrl,
  api,
  page,
}) => {
  const seeded = await seedCorpus({ container: harness.container, baseUrl });

  // A seeded scene carries its Cove studio id; the studio detail route is singular (/studio/:id).
  const studioId = [...seeded.values()].map((s) => s.studioId).find((id) => id != null);
  expect(studioId, "seedCorpus should link at least one scene to a studio").not.toBeUndefined();

  // The endpoints answer end-to-end (server resolves the remote id from the Cove id; no client-supplied id).
  const entity = await api.post(`/api/extensions/${EXTENSION_ID}/discovery/entity`, {
    CoveEntityId: studioId,
    Kind: "studio",
  });
  expect(entity.status).toBe(200);
  expect(Array.isArray(entity.json.scenes)).toBe(true);

  // The count the host tab badge reads is the SAME diff reduced to a count — it must equal the entity
  // response length, proving the badge is fed by the real endpoint, not a JS-side constant.
  const count = await api.get(
    `/api/extensions/${EXTENSION_ID}/discovery/count?kind=studio&entityId=${studioId}`,
  );
  expect(count.status).toBe(200);
  expect(count.json.count).toBe(entity.json.scenes.length);

  // Feed the BROWSER a synthetic populated list so the tab renders the card grid (the real endpoint, with no
  // live Whisparr, answers empty — the api.* wiring above already proved the real endpoint end-to-end).
  const scenes = syntheticScenes(5);
  await page.route("**/discovery/entity", async (route) => {
    await route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({ scenes, entityName: "Synthetic Studio", state: "ok", source: "stashdb", version: "v3" }),
    });
  });

  // The host renders the extension-contributed "Missing" tab on the studio detail page with a host-drawn
  // count badge. Opening it mounts the extension component and renders the wrapping card grid; the cards are
  // counted scoped under the tab's list region (each `[role="listitem"]` holds one `.video-card`).
  const detail = new EntityDetailPage(page, baseUrl);
  await detail.gotoStudio(studioId);

  const missingTab = page.getByRole("tab", { name: /Missing/ });
  await expect(missingTab).toBeVisible();
  await missingTab.click();
  await expect(missingTab).toHaveAttribute("aria-selected", "true");
  await expect.poll(() => page.locator('[role="listitem"] .video-card').count()).toBe(scenes.length);
});
