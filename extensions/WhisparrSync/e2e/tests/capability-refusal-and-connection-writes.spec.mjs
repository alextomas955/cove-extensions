// Two live facts a unit tier cannot reach, driven through a real browser against the shipped bundle.
//
// (1) A build that declares neither per-entity catalogue route refuses PERMANENTLY, and the surface must say so
//     without offering a control whose only outcome is the same refusal. The refusal is INDUCED rather than
//     fulfilled from a fixture: the extension is pointed at an endpoint that answers the movie list and does not
//     serve an API description at all, which is exactly the absent-document case the server's own capability read
//     resolves to. A refusal that is never induced is not verified, and a routed one asserts the fixture.
//
// (2) Save, Test connection and Register webhook run from three independent flags on one settings page, over ONE
//     options blob. The failure they can produce is not a visible error — it is a silent revert of the stored URL
//     and key by a write built on a stale read. So the assertion is what SURVIVES a reload, taken after the three
//     run back to back with no wait between them.
import { test, expect, seedCorpus } from "../lib/whisparrsync-fixtures.mjs";
import { startWhisparrApiStub } from "../lib/whisparr-api-stub.mjs";
import { startWhisparr } from "../lib/whisparr-container.mjs";
import { EntityDetailPage } from "../lib/pages/entity-detail-page.mjs";
import { WhisparrSettingsPage } from "../lib/pages/settings-page.mjs";

const EXTENSION_ID = "com.alextomas955.whisparrsync";

// The shipped BUILD_CAPABILITY_COPY. Written out rather than imported because the assertion is about what a
// user READS: a test that imported the same constant it renders would pass on any wording at all.
const BUILD_REFUSAL = "This Whisparr build doesn't offer the endpoints this needs";

// The generic retry sentences this refusal must never fall through to.
const RETRY_PHRASES = [/try again/i, /in a moment/i];

const STUB_API_KEY = "e2e-stub-key-not-real";

test("a build that declares neither catalogue route refuses permanently, with no retry offered", async ({
  harness,
  baseUrl,
  api,
  page,
}) => {
  const seeded = await seedCorpus({ container: harness.container, baseUrl });
  const studioId = [...seeded.values()].map((s) => s.studioId).find((id) => id != null);
  expect(studioId, "seedCorpus should link at least one scene to a studio").not.toBeUndefined();

  const networks = harness.container.getNetworkNames();
  expect(networks.length, "could not resolve the harness Docker network").toBeGreaterThan(0);

  // The document ARRIVES and declares only the movie index. That is the distinction the server refuses on: a
  // description that never answered says nothing about the build and is treated as an outage instead, so
  // withholding the document would induce a different code and prove nothing about this one.
  const stub = await startWhisparrApiStub({
    networkName: networks[0],
    movies: [],
    openApiPaths: ["/api/v3/movie", "/api/v3/system/status"],
  });

  try {
    const saved = await api.post(`/api/extensions/${EXTENSION_ID}/options`, {
      BaseUrl: stub.baseUrlFromCove,
      ApiKey: STUB_API_KEY,
      SelectedVersion: "v3",
    });
    expect(saved.status).toBe(200);

    // The server's own refusal, read before the browser: if this is not the capability code then whatever the
    // page renders is a different fact and the browser assertion below would be checking the wrong thing.
    const refusal = await api.post(`/api/extensions/${EXTENSION_ID}/discovery/entity`, {
      CoveEntityId: studioId,
      Kind: "studio",
    });
    expect(refusal.status).toBe(400);
    expect(refusal.json.code).toBe("CAPABILITY_UNAVAILABLE");

    const detail = new EntityDetailPage(page, baseUrl);
    await detail.gotoStudio(studioId);

    const missingTab = page.getByRole("tab", { name: /Missing/ });
    await expect(missingTab).toBeVisible();
    await missingTab.click();
    await expect(missingTab).toHaveAttribute("aria-selected", "true");

    // Filtered by the sentence rather than taken as "the status region": the host draws its own status
    // regions on this page, and an unscoped locator would count those too.
    const notice = page.getByRole("status").filter({ hasText: BUILD_REFUSAL });
    await expect(notice).toHaveCount(1);

    const sentence = await notice.innerText();
    for (const phrase of RETRY_PHRASES) {
      expect(sentence, `the permanent refusal invited a retry: ${sentence}`).not.toMatch(phrase);
    }

    // The affordance, not the wording: the notice carries no control at all, so there is nothing to press
    // that could only answer the same refusal again.
    await expect(notice.getByRole("button")).toHaveCount(0);
  } finally {
    await stub.stop();
  }
});

test("Save, Test connection and Register webhook in quick succession leave the stored connection intact", async ({
  harness,
  baseUrl,
  api,
  page,
}) => {
  const networks = harness.container.getNetworkNames();
  expect(networks.length, "could not resolve the harness Docker network").toBeGreaterThan(0);

  const whisparr = await startWhisparr({ networkName: networks[0], version: "v3" });

  try {
    const settings = new WhisparrSettingsPage(page, baseUrl);
    await settings.goto();
    await settings.setConnection(whisparr.baseUrlFromCove, whisparr.apiKey);

    // Back to back, with no settle between them: the defect this guards against is a write built on a read
    // taken before a sibling write landed, which a pause would hide.
    await settings.save();
    await settings.testConnection();
    await settings.registerWebhook();

    await settings.goto();

    // Read from the server, not from the form: the form could render a value it never persisted.
    const options = await api.get(`/api/extensions/${EXTENSION_ID}/options`);
    expect(options.status).toBe(200);
    expect(options.json.baseUrl).toBe(whisparr.baseUrlFromCove);
    // The key is never served back; its presence is the boolean the page renders from.
    expect(options.json.hasApiKey).toBe(true);

    await expect(settings.baseUrlInput).toHaveValue(whisparr.baseUrlFromCove);
    await expect(settings.apiKeyInput).toHaveAttribute("placeholder", /Key is set/);
  } finally {
    await whisparr.stop();
  }
});
