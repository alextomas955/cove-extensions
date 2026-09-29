// The host entity selector the panel uses for id-keyed tag rules, asserted against a real host because
// nothing local can: the extension declares the host component's props in an ambient .d.ts, so a
// type-check only confirms the call sites agree with that transcription - it would agree just as
// happily with a wrong one. The one-time conversion of a name-keyed blob is options-migration.spec.mjs.
import { test, expect, pollUntil, storedOptions } from "../lib/renamer-fixtures.mjs";
import { RenamerSettingsPage } from "../lib/pages/renamer-settings-page.mjs";

/** Creates a library tag through the host's own API and returns the id it assigned. */
async function createTag(api, name) {
  const created = await api.post("/api/tags", { Name: name });
  expect(created.ok, `creating tag '${name}' failed with ${created.status}`).toBe(true);
  expect(typeof created.json.id, `tag '${name}' came back with no numeric id`).toBe("number");
  return created.json.id;
}

/** Opens the per-tag destinations card and returns its add-row selector input. */
async function tagSelector(page) {
  const toggle = page.getByRole("switch", { name: /Per-tag destinations/i });
  await toggle.waitFor({ state: "visible", timeout: 30_000 });
  if ((await toggle.getAttribute("aria-checked")) !== "true") {
    await toggle.click();
  }

  // The host control renders a plain text input reached through the add row rather than by a label:
  // the in-row selector deliberately carries no visible label of its own.
  const input = page.getByPlaceholder(/Search tags/i).first();
  await input.waitFor({ state: "visible", timeout: 30_000 });
  return input;
}

test("the host tag selector stores the picked tag's id and renders its name back", async ({
  page,
  baseUrl,
  api,
  restoredOptions: _restoredOptions,
}) => {
  const tagId = await createTag(api, "e2e-routed-tag");

  const settings = new RenamerSettingsPage(page, baseUrl);
  await settings.goto();
  const input = await tagSelector(page);
  await input.fill("e2e-routed-tag");

  // The host searches server-side and renders its own result row. Clicking it proves the component is
  // the host's and is wired to a real query rather than rendering nothing.
  const result = page.getByText("e2e-routed-tag", { exact: true }).first();
  await result.waitFor({ state: "visible", timeout: 30_000 });
  await result.click();

  // Scoped to the add row rather than to the page: every destination on this panel draws the same
  // two controls, so an unscoped lookup would reach whichever one happens to render first.
  const addRow = page.getByRole("button", { name: /Add tag rule/i }).locator("xpath=..");
  await addRow.locator("select").selectOption("/data");
  await addRow.getByPlaceholder("$studio/$year").fill("routed");
  await page.getByRole("button", { name: /Add tag rule/i }).click();
  await settings.save();

  // What persisted is the id, not the name. A name here would mean the panel and the backend disagree
  // about the vocabulary, which fails to bind and is answered with defaults.
  const saved = await pollUntil(
    () => storedOptions(api),
    (o) => o !== undefined && Object.keys(o.TagDestinations ?? {}).length > 0,
    { label: "a saved tag destination" },
  );
  expect(Object.keys(saved.TagDestinations)).toContain(String(tagId));
  // The root is one of Cove's library paths, chosen from the list, and the template is relative to
  // it. A typed absolute path here would mean the panel is still storing a copy of a Cove setting.
  expect(saved.TagDestinations[String(tagId)]).toEqual({ Root: "/data", Template: "routed" });

  // The committed row reads as the tag's name after a reload, so an id-keyed rule stays identifiable
  // - and therefore removable - by the person who wrote it.
  await settings.goto();
  await expect(page.getByText("e2e-routed-tag").first()).toBeVisible({ timeout: 30_000 });
});

test("the host selector offers no way to create a tag from the settings panel", async ({
  page,
  baseUrl,
  api,
}) => {
  // The host control offers an inline create row by default, which would write a real entity into the
  // user's library from a screen that only configures rules over it. The adapter turns it off at one
  // declaration site; this asserts the flag reaches the host.
  await createTag(api, "e2e-create-probe");

  const settings = new RenamerSettingsPage(page, baseUrl);
  await settings.goto();
  const input = await tagSelector(page);

  // Search for the existing tag first, so the absence asserted below is a control that has queried
  // rather than one that has not started.
  await input.fill("e2e-create-probe");
  await page
    .getByText("e2e-create-probe", { exact: true })
    .first()
    .waitFor({ state: "visible", timeout: 30_000 });

  // The host draws its empty-result line only where it would otherwise draw the create row, and only
  // once the query for this text has settled. So the line appearing is what makes the absence below a
  // decision rather than a control that has not answered yet.
  const absent = "e2e-absent-tag-name";
  await input.fill(absent);
  await expect(page.getByText("No tags found").first()).toBeVisible({ timeout: 30_000 });
  await expect(page.getByText(new RegExp(`Create .*${absent}`, "i"))).toHaveCount(0);
});
