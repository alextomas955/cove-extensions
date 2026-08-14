// UI-driven coverage for the settings surface the rest of the suite never clicks. Two subjects:
//
// 1. The FOLDER template ("Where files go" — relocates files on rename) and the whole-library
//    "Rename all files" button (elsewhere exercised only via the raw renamer-library API job, never
//    the actual button). Driven through the real UI via the Page Object Model; the proof is exact
//    on-disk + DB state (assertRenamedTo), never the panel's own success banner. Both are the only
//    coverage anywhere of their UI path, which is why they are here rather than trimmed away with
//    the state matrix that used to surround them.
//
// 2. The entity selector fields, which are now the HOST's component rather than a local picker.
//    Every assertion about them is by ARIA role, never by class name or component name: the local
//    picker was a bare <input> beside a <div> of <button>s and emitted no combobox, listbox or
//    option role at all, so a role assertion is simultaneously the adoption proof and the
//    accessibility record. There is deliberately no jsdom, no testing-library and no stub of the
//    host component anywhere in this repo — that is why this proof lives here, against a real
//    released image, and nowhere cheaper.
//
// What is NOT asserted here, on purpose. Two visual properties with no reliable non-flaky assertion:
// the in-flight "Loading..." row (asserting it means winning a race against a local container's
// response) and chip wrapping / long-name / long-query layout. And three properties that belong to
// the host rather than to this extension's contract: the pre-typing helper sentence, the dropdown
// escaping a clipping ancestor, and the per-entity-type no-results copy. Each of the latter three
// asserts the host's own styling or wording, which goes red when the host improves — the adoption
// claim this file exists to make is carried by the role identity and the results state below.
import {
  isolatedTest as test,
  expect,
  seedVideo,
  createApiClient,
} from "../lib/renamer-fixtures.mjs";
import { VideosPage } from "@cove-extensions/e2e/pages/videos-page";
import { RenamerSettingsPage } from "../lib/pages/renamer-settings-page.mjs";
import { assertRenamedTo } from "../lib/rename-assertions.mjs";

const RENAMER_ID = "com.alextomas955.renamer";

/**
 * The root element of a titled `GroupCard`, used to scope a per-field assertion. Four selector
 * fields on this page share the placeholder `Search tags…`, so a page-wide locator would be
 * ambiguous; the card title is the only unique anchor a user can also see.
 *
 * The hop count is read off `GroupCard` in this repo's own `shared/cove-extensions-ui` — heading →
 * title box → header row → card root — rather than matched on a class, which would silently follow a
 * restyle onto the wrong element instead of failing. A by-role locator would be better still and
 * there is none to reach for: `GroupCard` renders a bare `div` carrying no role, landmark or
 * accessible name. The coupling is at least to first-party shared code, so it cannot drift without a
 * change in this repo.
 */
function groupCard(page, title) {
  return page.getByRole("heading", { name: title, exact: true }).locator("xpath=../../..");
}

// "Rename all files" sweeps EVERY item in the library, so the whole-library test below runs on its
// own instance (`isolatedHarness`) — a sibling test's seeded media sharing the per-worker harness
// would be swept into this run's scope and could miss its own polling window (same rationale as
// library-jobs.spec.mjs).

test("setting a folder template through the UI relocates a renamed file to the exact folder on disk and in the DB", async ({
  page,
  harness,
  baseUrl,
  api,
}) => {
  const video = await seedVideo({ container: harness.container, baseUrl });
  const originalFilename = video.files[0].path.split("/").pop();
  const originalPath = video.files[0].path;

  // A literal "relocated" folder (no tokens) plus a "$title"-only filename makes the whole landing
  // path deterministic. With no routing rules and empty AllowedRoots the move is source-confined —
  // the file may only move WITHIN its own source root (/data) — so a bare relative sub-folder under
  // /data is a genuinely permitted target; a routed root outside /data would be rejected. The file
  // must therefore land at exactly /data/relocated/<title>.mp4.
  const title = "Folder Move Test";
  const folder = "relocated";
  const expectedBasename = `${title}.mp4`;
  const expectedFullPath = `/data/${folder}/${expectedBasename}`;

  const settingsPage = new RenamerSettingsPage(page, baseUrl);
  await settingsPage.goto();
  await settingsPage.setFilenameTemplate("$title");
  await settingsPage.setFolderTemplate(folder);
  await settingsPage.save();

  // Everything past the template mutation runs under try/finally, so the reset below does not
  // depend on every assertion above it having passed.
  try {
    const videosPage = new VideosPage(page, baseUrl);
    await videosPage.goto();
    // Select by filename BEFORE setting a Title: the card's accessible name follows the title once set.
    await videosPage.selectCard(originalFilename);

    const setTitle = await api.put(`/api/videos/${video.id}`, { Title: title });
    expect(setTitle.ok).toBe(true);

    await videosPage.renameSelected();

    const newPath = await assertRenamedTo({
      api,
      container: harness.container,
      videoId: video.id,
      expectedBasename,
      originalPath,
    });
    // assertRenamedTo proves the basename + that the DB path exists on disk and the old path is gone;
    // the folder-relocation claim needs the FULL path pinned, or a move to the wrong (but existing)
    // folder would slip through.
    expect(newPath, "renamed file must land in the exact folder the template computed").toBe(
      expectedFullPath,
    );
  } finally {
    // Reset templates so the folder move + "$title" naming do not leak into a sibling test sharing this
    // worker's Cove instance (mirrors core-paths.spec.mjs's cleanup).
    await settingsPage.goto();
    await settingsPage.setFilenameTemplate("{$date - }$title{ [$resolution]}");
    await settingsPage.setFolderTemplate("");
    await settingsPage.save();
  }
});

test('clicking "Rename all files" in the panel renames every library item to its exact name on disk and in the DB', async ({
  page,
  isolatedHarness,
}) => {
  const baseUrl = isolatedHarness.baseUrl;
  const container = isolatedHarness.container;
  const api = createApiClient(baseUrl, isolatedHarness.token);

  // Persist a "$title"-only template through the real settings UI (not the options API) so each item's
  // computed name is deterministic and the panel button — disabled while dirty — becomes clickable.
  const settingsPage = new RenamerSettingsPage(page, baseUrl);
  await settingsPage.goto();
  await settingsPage.setFilenameTemplate("$title");
  await settingsPage.save();

  const videos = await Promise.all([
    seedVideo({ container, baseUrl, destName: `panel-a-${Date.now()}.mp4` }),
    seedVideo({ container, baseUrl, destName: `panel-b-${Date.now()}.mp4` }),
    seedVideo({ container, baseUrl, destName: `panel-c-${Date.now()}.mp4` }),
  ]);
  const originalPaths = videos.map((v) => v.files[0].path);

  const titles = ["Panel Rename Alpha", "Panel Rename Bravo", "Panel Rename Charlie"];
  for (let i = 0; i < videos.length; i++) {
    const update = await api.put(`/api/videos/${videos[i].id}`, { Title: titles[i] });
    expect(update.ok).toBe(true);
  }

  // The whole-library rename is triggered by the actual panel button here — NOT a POST to
  // renamer-library. That is the gap this test closes: the button-driven counterpart to
  // library-jobs.spec.mjs's API-driven run.
  await settingsPage.renameAll();

  for (let i = 0; i < videos.length; i++) {
    await assertRenamedTo({
      api,
      container,
      videoId: videos[i].id,
      expectedBasename: `${titles[i]}.mp4`,
      originalPath: originalPaths[i],
    });
  }
});

test("the exclude fields are the host selector by role, and typing one character surfaces a seeded entity as an option in each", async ({
  page,
  baseUrl,
  api,
}) => {
  // Collected for the WHOLE interaction — attached before the first navigation and asserted after
  // the last click, so a throw at any point in it is caught. This is the panel's highest-severity
  // failure mode, not a formality: the host component calls useQueryClient(), so the panel must
  // mount inside the host's query provider or the render throws — and on this host one broken
  // extension bundle takes down every extension's UI on the page, not only this one's.
  const errors = [];
  page.on("pageerror", (err) => errors.push(err.message));
  // console.error is NOT asserted empty: the host logs recoverable React and network noise there,
  // so an empty-list assertion would couple this gate to host chatter. Narrowed to messages naming
  // this extension or the runtime specifiers it imports through, which is the surface at issue.
  const consoleErrors = [];
  page.on("console", (msg) => {
    if (msg.type() === "error") consoleErrors.push(msg.text());
  });

  // Stamped per run: the Cove instance is shared per worker, and the fields are driven by typing
  // these names. The leading "Qz" is what makes a SINGLE typed character selective enough to be
  // asserted on — the host searches the whole library, so a common first letter would be a race
  // against the ranked top-25 cap rather than a test.
  const stamp = Date.now();
  const tagName = `Qz Coverage Tag ${stamp}`;
  const studioName = `Qz Coverage Studio ${stamp}`;
  const tag = await api.post("/api/tags", { name: tagName });
  expect(tag.ok, `POST /api/tags returned ${tag.status}: ${tag.text}`).toBe(true);
  const studio = await api.post("/api/studios", { name: studioName });
  expect(studio.ok, `POST /api/studios returned ${studio.status}: ${studio.text}`).toBe(true);

  // "Freshly installed" is established, never assumed: a sibling test on this worker's instance may
  // have left values behind. The blob is replaced wholesale, so every unset field falls back to its
  // default — for both exclude lists, empty. Double-encoded: the [FromBody] string binder wants a
  // JSON string literal.
  const reset = await api.put(`/api/extensions/${RENAMER_ID}/data/options`, JSON.stringify({}));
  expect(reset.ok, `resetting the options blob returned ${reset.status}: ${reset.text}`).toBe(true);

  const settings = new RenamerSettingsPage(page, baseUrl);
  await settings.goto();
  await expect(settings.filenameTemplateInput).toBeVisible({ timeout: 15_000 });

  const excludesHeader = page.getByRole("button", { name: /^Excludes/ });
  await excludesHeader.click();
  const tagCard = groupCard(page, "Exclude by tag");
  const studioCard = groupCard(page, "Exclude by studio");

  // --- ADOPTION: the field IS a combobox (E1) ---
  const tagInput = tagCard.getByRole("combobox");
  const studioInput = studioCard.getByRole("combobox");
  await expect(
    tagInput,
    "no combobox in the exclude-by-tag card — the deleted local picker was a bare input beside a div of buttons and emitted no combobox role at all, so this is the assertion that separates the host component from what it replaced",
  ).toHaveCount(1);
  await expect(studioInput, "no combobox in the exclude-by-studio card").toHaveCount(1);
  await expect(tagInput).toHaveAttribute("aria-autocomplete", "list");
  await expect(tagInput).toHaveAttribute("aria-expanded", "false");
  // Ties the role assertion to THIS extension's field rather than to any combobox the host page
  // might render: the placeholder is the extension's own copy, not the host default `Search tags...`.
  await expect(tagInput).toHaveAttribute("placeholder", "Search tags…");
  await expect(studioInput).toHaveAttribute("placeholder", "Search studios…");

  // --- RESULTS: one typed character surfaces a seeded entity, as an option, in BOTH fields (E1) ---
  // Both entity types, not just one: each field is a separate instantiation of the host component
  // against a different host search route, and this is the only place in the suite that types into
  // the studio field at all.
  //
  // The listbox count is read BEFORE typing and again after, so the "no listbox yet" reading is
  // proven to be a locator that can match rather than one that never matches anything.
  const listbox = page.getByRole("listbox");
  await expect(listbox).toHaveCount(0);

  await tagInput.fill("Q");
  // Page-scoped, not card-scoped: the dropdown portals to the document root, so it is NOT inside
  // the field's own subtree. Safe against a false match because the name carries this run's stamp.
  await expect(
    page.getByRole("option", { name: tagName, exact: true }),
    `typing one character surfaced no option named "${tagName}" — either the host search never ran or its results do not carry the option role`,
  ).toBeVisible({ timeout: 15_000 });
  await expect(
    listbox,
    "the listbox locator that read 0 before typing must read 1 now — otherwise the reading above was taken with a locator that never matches anything",
  ).toHaveCount(1);

  await studioInput.fill("Q");
  await expect(
    page.getByRole("option", { name: studioName, exact: true }),
    `typing one character surfaced no option named "${studioName}" — the studio field is a second instantiation against a second host route, so the tag field passing says nothing about it`,
  ).toBeVisible({ timeout: 15_000 });

  // --- MOUNT: the whole interaction raised nothing (E4) ---
  expect(
    errors,
    `the settings surface raised page errors during the interaction: ${errors.join("; ")}`,
  ).toEqual([]);
  const ours = consoleErrors.filter((m) => /renamer|@cove\/runtime/i.test(m));
  expect(ours, `the console named this extension in an error: ${ours.join("; ")}`).toEqual([]);
});

test("selected values are host chips carrying the real entity name, none renders when nothing is selected, and no count or plural copy exists on this surface", async ({
  page,
  baseUrl,
  api,
}) => {
  const errors = [];
  page.on("pageerror", (err) => errors.push(err.message));

  const stamp = Date.now();
  const first = `Qz Chip Tag Alpha ${stamp}`;
  const second = `Qz Chip Tag Bravo ${stamp}`;
  for (const name of [first, second]) {
    const created = await api.post("/api/tags", { name });
    expect(created.ok, `POST /api/tags returned ${created.status}: ${created.text}`).toBe(true);
  }
  const reset = await api.put(`/api/extensions/${RENAMER_ID}/data/options`, JSON.stringify({}));
  expect(reset.ok, `resetting the options blob returned ${reset.status}: ${reset.text}`).toBe(true);

  const settings = new RenamerSettingsPage(page, baseUrl);
  await settings.goto();
  await expect(settings.filenameTemplateInput).toBeVisible({ timeout: 15_000 });
  await page.getByRole("button", { name: /^Excludes/ }).click();
  const tagCard = groupCard(page, "Exclude by tag");
  const tagInput = tagCard.getByRole("combobox");

  // --- ZERO: no chip row at all, and no copy standing in for one (E2) ---
  const chips = tagCard.getByRole("button", { name: /^Remove / });
  await expect(
    chips,
    "a chip rendered with nothing selected — at zero values the host renders no chip row at all",
  ).toHaveCount(0);
  expect(
    await tagCard.innerText(),
    "a nothing-selected message appeared; the contract is that the field simply stands alone",
  ).not.toMatch(/selected/i);

  // --- ONE: the chip settles on the entity's REAL name (E2) ---
  await tagInput.fill(first);
  await page.getByRole("option", { name: first, exact: true }).click();
  await expect(
    tagCard.getByRole("button", { name: `Remove ${first}`, exact: true }),
    `the chip never settled on "${first}" — the host names a chip's remove control after the resolved label, so an unresolved chip announces the bare entity type instead`,
  ).toBeVisible({ timeout: 15_000 });
  await expect(
    tagCard,
    "the chip is still showing the interim loading string rather than the resolved name",
  ).not.toContainText("Loading tag...");

  // --- MANY: identical in kind, with nothing counting them (E2) ---
  await tagInput.fill(second);
  await page.getByRole("option", { name: second, exact: true }).click();
  await expect(chips, "the second selection did not produce a second chip").toHaveCount(2);
  await expect(
    tagCard.getByRole("button", { name: `Remove ${second}`, exact: true }),
  ).toBeVisible();
  const withTwo = await tagCard.innerText();
  expect(
    withTwo,
    "a count appeared alongside the chips — there is no count or plural copy on this surface, which is why none can be got wrong",
  ).not.toMatch(/\b\d+\s+tags?\b/i);
  expect(withTwo, "a selected-count message appeared alongside the chips").not.toMatch(/selected/i);

  expect(errors, `the settings surface raised page errors: ${errors.join("; ")}`).toEqual([]);
});
