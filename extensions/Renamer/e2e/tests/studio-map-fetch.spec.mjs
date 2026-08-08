// The proof that the three entity-listing endpoints this extension used to serve have no caller
// left. They existed only to feed a local picker; the host's own entity selector and value renderer
// replaced it, so the routes were deleted and nothing may reach for them again.
//
// The core assertion is NEGATIVE — "no request went to these paths" — which is exactly the shape
// that passes by inspecting nothing. A page that never mounted, a selector that never opened, a
// harness that failed silently: all of them satisfy it. So it is paired with POSITIVE observations
// over the same captured traffic: the host's own entity search and its per-id label lookup must
// both be seen. The negative can only be trusted in a run the positives prove exercised the panel.
//
// The positives are pinned to the values THIS run seeded — the search `q` is the seeded name, the
// lookup path is the seeded id — because the host issues unrelated `/api/studios` and `/api/tags`
// traffic of its own on any settings page load. A bare "some /api/studios request happened" would
// be satisfied by that background traffic on a run where the panel never rendered at all.
//
// This is NOT the authenticated-fetch proof any more. It used to be — it watched the extension's
// own `list-studios` call and asserted its status — but that call is gone, and
// `authenticated-fetch.spec.mjs` owns that role now, against an auth-ENABLED instance where the
// claim is falsifiable at all.
import { test, expect } from "../lib/renamer-fixtures.mjs";
import { RenamerSettingsPage } from "../lib/pages/renamer-settings-page.mjs";

const EXTENSION_ID = "com.alextomas955.renamer";

// Pathname-exact, never a substring: a caller pointed at a neighbouring route (".../list-tags-2")
// must NOT satisfy the negative half, or the check would green-light the very defect it exists to
// catch.
const DELETED_ROUTES = [
  `/api/extensions/${EXTENSION_ID}/list-studios`,
  `/api/extensions/${EXTENSION_ID}/list-tags`,
  `/api/extensions/${EXTENSION_ID}/list-performers`,
];

test("a full settings interaction reaches the host for its entities and never the deleted extension routes", async ({
  page,
  baseUrl,
  api,
}) => {
  const errors = [];
  page.on("pageerror", (err) => errors.push(err.message));

  // Every same-origin API request the page makes, captured for the WHOLE interaction. Both halves
  // read this one list, so they are answering questions about the same run.
  const requests = [];
  page.on("request", (request) => {
    const url = new URL(request.url());
    if (url.pathname.startsWith("/api/")) {
      requests.push({ pathname: url.pathname, query: url.searchParams });
    }
  });

  // Unique per run: the per-worker Cove instance is shared, and the fields are driven by typing
  // these names, so each has to narrow to something.
  const stamp = Date.now();
  const studioName = `E2E NoCaller Studio ${stamp}`;
  const tagName = `E2E NoCaller Tag ${stamp}`;

  const studio = await api.post("/api/studios", { name: studioName });
  expect(studio.ok, `POST /api/studios returned ${studio.status}: ${studio.text}`).toBe(true);
  const tag = await api.post("/api/tags", { name: tagName });
  expect(tag.ok, `POST /api/tags returned ${tag.status}: ${tag.text}`).toBe(true);

  // Seed a committed rule in BOTH maps so both rule tables render a row whose key is a bare stored
  // id — the case that needs a label lookup. Double-encoded: the host's [FromBody] string binder
  // wants a JSON string literal.
  const seeded = await api.put(
    `/api/extensions/${EXTENSION_ID}/data/options`,
    JSON.stringify({
      EnableStudioDestinations: true,
      StudioDestinations: { [studio.json.id]: "/data/studio-dest" },
      EnableTagDestinations: true,
      TagDestinations: { [tag.json.id]: "/data/tag-dest" },
    }),
  );
  expect(seeded.ok, `seeding the options blob returned ${seeded.status}: ${seeded.text}`).toBe(
    true,
  );

  const settings = new RenamerSettingsPage(page, baseUrl);
  await settings.goto();
  await expect(settings.filenameTemplateInput).toBeVisible({ timeout: 15_000 });

  // --- both committed-rule tables, so both key labels resolve ---
  const studioCard = page
    .getByRole("heading", { name: "Per-studio destinations", exact: true })
    .locator("xpath=ancestor::section[1]");
  const tagCard = page
    .getByRole("heading", { name: "Per-tag destinations", exact: true })
    .locator("xpath=ancestor::section[1]");

  // The row anchor is the remove control, which still names the RAW stored key — so it locates the
  // row whether or not the label beside it resolved. That is the point: a locator keyed on the
  // resolved name could not tell an unresolved row from an absent one.
  const studioRow = studioCard
    .getByRole("button", { name: `Remove ${studio.json.id}`, exact: true })
    .locator("xpath=..");
  const tagRow = tagCard
    .getByRole("button", { name: `Remove ${tag.json.id}`, exact: true })
    .locator("xpath=..");

  await expect(studioRow, "the seeded per-studio rule did not render").toBeVisible({
    timeout: 15_000,
  });
  await expect(tagRow, "the seeded per-tag rule did not render").toBeVisible({ timeout: 15_000 });
  await expect(
    studioRow,
    `the committed studio rule never resolved to "${studioName}"`,
  ).toContainText(studioName, { timeout: 15_000 });
  await expect(tagRow, `the committed tag rule never resolved to "${tagName}"`).toContainText(
    tagName,
    {
      timeout: 15_000,
    },
  );

  // --- a tag field and a studio field, typed far enough to trigger a search ---
  const excludesHeader = page.getByRole("button", { name: /^Excludes/ });
  const excludes = excludesHeader.locator("xpath=..");
  await excludesHeader.click();

  // The host renders its results list in a portal at the document root, NOT inside the field's own
  // subtree — so the option lookup is page-scoped. Safe: the seeded names carry this run's stamp.
  const excludeTag = excludes.getByPlaceholder("Search tags…");
  await excludeTag.click();
  await excludeTag.fill(tagName);
  await expect(
    page.getByRole("option", { name: tagName, exact: true }),
    "typing a seeded tag name produced no host result — the search this test relies on never ran",
  ).toBeVisible({ timeout: 15_000 });

  const excludeStudio = excludes.getByPlaceholder("Search studios…");
  await excludeStudio.click();
  await excludeStudio.fill(studioName);
  await expect(
    page.getByRole("option", { name: studioName, exact: true }),
    "typing a seeded studio name produced no host result — the search this test relies on never ran",
  ).toBeVisible({ timeout: 15_000 });

  // --- POSITIVE: the host answered for its own entities, in this run ---
  const searched = (pathname, term) =>
    requests.some((r) => r.pathname === pathname && r.query.get("q") === term);
  expect(
    searched("/api/tags", tagName),
    `no GET /api/tags?q=${tagName} in the captured traffic — the tag field's search never reached the host, so the negative assertion below would be inspecting a run that did nothing`,
  ).toBe(true);
  expect(
    searched("/api/studios", studioName),
    `no GET /api/studios?q=${studioName} in the captured traffic — the studio field's search never reached the host, so the negative assertion below would be inspecting a run that did nothing`,
  ).toBe(true);

  const lookedUp = (pathname) => requests.some((r) => r.pathname === pathname);
  expect(
    lookedUp(`/api/studios/${studio.json.id}`),
    "the committed studio rule rendered no host label lookup — the label came from somewhere other than the host",
  ).toBe(true);
  expect(
    lookedUp(`/api/tags/${tag.json.id}`),
    "the committed tag rule rendered no host label lookup — the label came from somewhere other than the host",
  ).toBe(true);

  // --- NEGATIVE: and never the routes this extension no longer serves ---
  const hits = requests.filter((r) => DELETED_ROUTES.includes(r.pathname)).map((r) => r.pathname);
  expect(hits, `something still calls a deleted entity-listing route: ${hits.join(", ")}`).toEqual(
    [],
  );

  expect(errors, `the settings panel raised page errors: ${errors.join("; ")}`).toEqual([]);
});
