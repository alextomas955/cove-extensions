// Tag rules keyed on stable ids, proven on the two paths nothing else covers: the one-time conversion
// of a name-keyed options blob, and the host entity selector the panel now uses.
//
// The conversion is the half that can lose a user's configuration. A fresh install has nothing to
// convert, so it passes whether the conversion works or not; the cases that discriminate are an
// upgrade whose names resolve and an upgrade whose names do not. The second is the dangerous one:
// resolving every name to nothing would write the whole rule set away behind a stamp that stops it
// ever being retried, so the refusal to convert is asserted directly.
//
// The selector half is asserted against a real host because nothing local can. The extension declares
// the host component's props in an ambient .d.ts, so a type-check only confirms the call sites agree
// with that transcription - it would agree just as happily with a wrong one.
import { test as base, createApiClient, isolatedHarnessFixture } from "@cove-extensions/e2e";
import { test, expect, RENAMER_EXTENSION, seedVideo, pollUntil } from "../lib/renamer-fixtures.mjs";
import { RenamerSettingsPage } from "../lib/pages/renamer-settings-page.mjs";
import { pollRenamerJob } from "../lib/poll-renamer-job.mjs";

const EXTENSION_ID = "com.alextomas955.renamer";
const ROUTE = `/api/extensions/${EXTENSION_ID}`;

// The conversion runs at initialize time, which only a restart reaches, so these two need an instance
// of their own: a restart and a rewritten options blob would both leak into every sibling test in a
// shared worker instance.
const restartTest = base.extend({
  isolatedHarness: isolatedHarnessFixture(RENAMER_EXTENSION),
});

function clientFor(harness) {
  return createApiClient(
    () => harness.baseUrl,
    () => harness.token,
  );
}

/** Creates a library tag through the host's own API and returns the id it assigned. */
async function createTag(api, name) {
  const created = await api.post("/api/tags", { Name: name });
  expect(created.ok, `creating tag '${name}' failed with ${created.status}`).toBe(true);
  expect(typeof created.json.id, `tag '${name}' came back with no numeric id`).toBe("number");
  return created.json.id;
}

/** The extension's stored options blob, parsed, or undefined when the key is absent. */
async function storedOptions(api) {
  const all = await api.get(`${ROUTE}/data`);
  expect(all.ok).toBe(true);
  const blob = (all.json ?? {}).options;
  return blob ? JSON.parse(blob) : undefined;
}

restartTest(
  "a name-keyed options blob converts to ids on load, and its rule keeps applying",
  async ({ isolatedHarness }) => {
    const harness = isolatedHarness;
    const api = clientFor(harness);

    // The id comes from the host, so the assertion below cannot pass against a conversion that merely
    // invented a plausible number.
    const excludedId = await createTag(api, "e2e-excluded");

    const video = await seedVideo({
      container: harness.container,
      baseUrl: harness.baseUrl,
      token: harness.token,
    });
    expect(
      (await api.put(`/api/videos/${video.id}`, { Title: "Entity Id Rules", TagIds: [excludedId] }))
        .ok,
    ).toBe(true);
    // Read back rather than trust the write: a tag that never attached would make the exclude
    // assertion below pass for the wrong reason.
    expect(
      (await api.get(`/api/videos/${video.id}`)).json.tags?.map((t) => t.id),
      "the seeded video must actually carry the tag",
    ).toContain(excludedId);

    // The shape an upgrading installation carries: the rule names the tag, and the key the current
    // model reads does not exist yet.
    await api.put(
      `${ROUTE}/data/options`,
      JSON.stringify({ FilenameTemplate: "$title", ExcludeTags: ["e2e-excluded"] }),
    );

    // A restart is the only way into an initialize-time path: it does not run again while the host is up.
    await harness.restart();
    const after = clientFor(harness);

    const converted = await pollUntil(
      () => storedOptions(after),
      (o) => o !== undefined && Array.isArray(o.ExcludeTagIds),
      { label: "the stored options blob to carry ExcludeTagIds" },
    );

    // (1) The rule names the id the host assigned, and the name-keyed key is gone rather than left
    //     beside it, which is a state the model cannot express.
    expect(converted.ExcludeTagIds).toEqual([excludedId]);
    expect(Object.keys(converted)).not.toContain("ExcludeTags");
    expect(converted.FilenameTemplate, "an unrelated setting must survive the conversion").toBe(
      "$title",
    );

    // (2) The converted rule still does what the user configured it to do. An exclude that converted
    //     but stopped matching is invisible in the blob, and this is what catches it.
    const before = (await after.get(`/api/videos/${video.id}`)).json.files[0].path;
    const enqueue = await after.post(`${ROUTE}/renamer`, {
      EntityType: "video",
      EntityIds: [video.id],
    });
    expect(enqueue.status).toBe(202);
    await pollRenamerJob(after, ROUTE, enqueue.json.jobId);

    expect(
      (await after.get(`/api/videos/${video.id}`)).json.files[0].path,
      "the excluded item must not have been renamed",
    ).toBe(before);
  },
);

restartTest(
  "a name-keyed blob whose names resolve to nothing is left intact rather than emptied",
  async ({ isolatedHarness }) => {
    const harness = isolatedHarness;
    const api = clientFor(harness);

    // No tag of either name exists, which is the state a library the extension cannot read yet
    // presents. Converting here would resolve both rules to nothing.
    const legacy = JSON.stringify({
      FilenameTemplate: "$title",
      ExcludeTags: ["never-created"],
      TagDestinations: { "also-never-created": "/somewhere" },
    });
    expect((await api.put(`${ROUTE}/data/options`, legacy)).ok).toBe(true);

    await harness.restart();

    const all = await clientFor(harness).get(`${ROUTE}/data`);
    expect(all.ok).toBe(true);

    // The blob is untouched and no schema stamp was written, so a later load - once the library is
    // readable - still converts. A stamp here would make the loss permanent.
    expect((all.json ?? {}).options).toBe(legacy);
    expect(Object.keys(all.json ?? {})).not.toContain("options.schema");
  },
);

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

  // The committed row reads as the tag's NAME after a reload, so an id-keyed rule stays identifiable
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

  const absent = "e2e-absent-tag-name";
  await input.fill(absent);
  await expect(page.getByText(new RegExp(`Create .*${absent}`, "i"))).toHaveCount(0);

  const tags = await api.get("/api/tags?perPage=200");
  const names = (tags.json.items ?? tags.json ?? []).map((t) => t.name);
  expect(names, "no tag may have been created from the settings panel").not.toContain(absent);
});
