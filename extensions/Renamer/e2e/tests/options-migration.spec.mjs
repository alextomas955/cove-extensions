// The one-time name→id options conversion, against a real containerized Cove started on top of a
// stored legacy blob, with the outcome read off the settings panel a user would open.
//
// Three things the backend tier replaces with a double are real only here: that the host runs
// InitializeAsync before it serves the panel; that the elevated library read returns rows through
// Cove's own authorization filters, which exist only under Npgsql and so are absent from the SQLite
// unit tier; and that the panel renders converted ids as entity names rather than as numbers, empty
// fields, or the host's "Loading tag..." placeholder. The rename at the end covers an exclude that
// converted but stopped matching, which is invisible in the blob.
//
// The blob below is written by this test rather than taken from a real installation.
import { test as base, isolatedHarnessFixtures } from "@cove-extensions/e2e";
import {
  expect,
  pollUntil,
  seedVideo,
  clientFor,
  storedOptions,
  RENAMER_EXTENSION,
  EXTENSION_ID,
  ROUTE,
} from "../lib/renamer-fixtures.mjs";
import { RenamerSettingsPage } from "../lib/pages/renamer-settings-page.mjs";
import { pollRenamerJob } from "../lib/poll-renamer-job.mjs";

// Its own Cove instance, because this test restarts the host: a restart re-binds the published port
// and invalidates every token minted before it, which would pull the base URL out from under every
// sibling test sharing a worker. The fixture installs the extension with no stored blob, which is
// what makes the seed below reachable: the conversion returns before stamping when there is nothing
// to convert, so the schema stamp is still unset when this test writes its legacy blob.
const test = base.extend(isolatedHarnessFixtures(RENAMER_EXTENSION));

/**
 * A titled card, by the name it exposes. Every card names its `<section>` after its own heading, so
 * this is one query rather than a count of the elements between the two - a count follows a restyle
 * onto the wrong element instead of failing.
 */
function card(page, title) {
  return page.getByRole("region", { name: title, exact: true });
}

// A `Field` renders a `<label>` and a `FieldGroup` a `<fieldset>`, so either shape is a candidate, and
// a candidate containing another candidate is not one: `hasText` matches an ancestor as readily as a
// leaf, and `PerKindRows` wraps fields of its own in a `<fieldset>` row.
const FIELD_SELECTOR = "label:not(:has(label, fieldset)), fieldset:not(:has(label, fieldset))";

/**
 * One `Field` within a scope. Two selector fields share the Performers card and two share the Tags
 * card, so the card alone cannot separate a whitelist chip from a blacklist chip. The exclusion in
 * {@link FIELD_SELECTOR}, not the absence of a second match, is what keeps this the narrowest scope
 * that can; resolving strictly is what says so when it stops being true.
 */
function field(scope, label) {
  return scope.locator(FIELD_SELECTOR).filter({ hasText: label });
}

test("a legacy blob stored before the host starts converts at initialize, and the panel renders the surviving rules as entity names", async ({
  page,
  isolatedHarness,
}) => {
  // Two container lifecycles (a boot for the isolated instance, then a full restart on top of it)
  // plus a browser-driven panel run do not fit the project's default per-test budget.
  test.setTimeout(360_000);

  const errors = [];
  page.on("pageerror", (err) => errors.push(err.message));

  const stamp = Date.now();
  const seedApi = clientFor(isolatedHarness);

  const names = {
    tagKeep: `Qzmig Keep Tag ${stamp}`,
    // Two performers differing only by letter case, created in this order deliberately: the
    // converter keeps the lowest id, and Postgres hands out ascending ids, so `caseFirst` is the
    // predictable survivor.
    caseFirst: `qzmig case performer ${stamp}`,
    caseSecond: `QZMIG CASE PERFORMER ${stamp}`,
    tagRoute: `Qzmig Route Tag ${stamp}`,
    tagExclude: `Qzmig Exclude Tag ${stamp}`,
    performerKeep: `Qzmig Keep Performer ${stamp}`,
    performerBlock: `Qzmig Block Performer ${stamp}`,
  };
  // Named by the blob, deliberately never created - a rule pointing at something the library no
  // longer has, which every real upgrade carries at least one of.
  const vanishedTag = `Qzmig Vanished Tag ${stamp}`;

  const ids = {};
  for (const key of ["tagKeep", "tagRoute", "tagExclude"]) {
    const created = await seedApi.post("/api/tags", { name: names[key] });
    expect(
      created.ok,
      `POST /api/tags "${names[key]}" answered ${created.status}: ${created.text}`,
    ).toBe(true);
    ids[key] = created.json.id;
  }
  for (const key of ["performerKeep", "performerBlock"]) {
    const created = await seedApi.post("/api/performers", { name: names[key] });
    expect(
      created.ok,
      `POST /api/performers "${names[key]}" answered ${created.status}: ${created.text}`,
    ).toBe(true);
    ids[key] = created.json.id;
  }
  // A performer's identity is the name key paired with the disambiguation key, so the host holds
  // both spellings only because each carries its own disambiguation.
  for (const key of ["caseFirst", "caseSecond"]) {
    const created = await seedApi.post("/api/performers", {
      name: names[key],
      disambiguation: `${key} ${stamp}`,
    });
    expect(
      created.ok,
      `POST /api/performers "${names[key]}" answered ${created.status}: ${created.text} - a 409 here means this host folds performer names by case even with distinct disambiguations, which would make the case-collapse assertion below unreachable rather than merely failing`,
    ).toBe(true);
    ids[key] = created.json.id;
  }
  expect(
    ids.caseFirst < ids.caseSecond,
    `the case-variant performers were created as ${ids.caseFirst} and ${ids.caseSecond} - not ascending, so the collapse below has no predictable survivor`,
  ).toBe(true);

  // One video carries the tag the stored exclusion names and one carries nothing, which is what
  // proves the run would have renamed the first.
  const renameSubjects = {};
  for (const key of ["excluded", "control"]) {
    const video = await seedVideo({
      container: isolatedHarness.container,
      baseUrl: isolatedHarness.baseUrl,
      token: isolatedHarness.token,
    });
    const update = await seedApi.put(`/api/videos/${video.id}`, {
      Title: `Qzmig ${key} ${stamp}`,
      TagIds: key === "excluded" ? [ids.tagExclude] : [],
    });
    expect(update.ok, `titling the ${key} video answered ${update.status}: ${update.text}`).toBe(
      true,
    );
    renameSubjects[key] = {
      id: video.id,
      fileId: video.files[0].id,
      originalPath: video.files[0].path,
    };
  }
  expect(
    (await seedApi.get(`/api/videos/${renameSubjects.excluded.id}`)).json.tags?.map((t) => t.id),
    "the excluded video must actually carry the tag, or its staying put below proves nothing",
  ).toContain(ids.tagExclude);

  // All six migrated fields in their name-keyed form, both groups carrying both legacy keys
  // including the empty array a real install always emitted, and three fields the converter does
  // not model. The template names $performers and $tags because the panel renders those two token
  // cards only for tokens the template uses. The two routing toggles are on because a toggle card
  // renders no children while it is off.
  const legacyBlob = {
    FilenameTemplate: "$title - $performers [$tags]",
    // A key the current model no longer has: an old blob must still load, with this ignored.
    AllowedRoots: ["/data"],
    PathDestinations: [{ Pattern: "/data/legacy", Dest: "/data/archive", IsRegex: false }],
    EnableAdvancedRouting: true,
    EnableTagDestinations: true,
    Performers: {
      Whitelist: [names.performerKeep, names.caseFirst, names.caseSecond],
      Blacklist: [names.performerBlock],
    },
    Tags: { Whitelist: [names.tagKeep], Blacklist: [] },
    ExcludeTags: [names.tagExclude, vanishedTag],
    TagDestinations: { [names.tagRoute]: "/data/routed" },
  };
  // Single JSON.stringify: the [FromBody] string binder wants exactly one JSON string literal.
  const seeded = await seedApi.put(
    `/api/extensions/${EXTENSION_ID}/data/options`,
    JSON.stringify(legacyBlob),
  );
  expect(
    seeded.ok,
    `seeding the legacy options blob answered ${seeded.status}: ${seeded.text}`,
  ).toBe(true);

  // The conversion runs at InitializeAsync and nowhere else, so there is no way to reach it while
  // the host stays up.
  await isolatedHarness.restart();
  const api = clientFor(isolatedHarness);

  // Read from the store rather than from the panel: the panel mounts once and would then show the
  // pre-conversion state forever, so a page opened too early would fail for the wrong reason.
  await pollUntil(
    () => storedOptions(api),
    (o) => o !== undefined && Array.isArray(o.Performers?.WhitelistIds),
    {
      timeoutMs: 120_000,
      label: "the initialize-time conversion to rewrite the stored blob to ids",
    },
  );

  const baseUrl = isolatedHarness.baseUrl;
  const settings = new RenamerSettingsPage(page, baseUrl);
  await settings.goto();

  const tagsCard = card(page, "Tags");
  const tagOnlyInclude = field(tagsCard, "Only include");
  await expect(
    tagOnlyInclude.getByRole("button", { name: `Remove ${names.tagKeep}`, exact: true }),
    `the tag whitelist rule stored as the name "${names.tagKeep}" is not on the panel as a chip carrying that name`,
  ).toBeVisible({ timeout: 15_000 });
  await expect(
    tagOnlyInclude.getByRole("button", { name: /^Remove / }),
    "the one stored tag whitelist name must land as exactly one chip",
  ).toHaveCount(1);
  await expect(
    field(tagsCard, "Never include").getByRole("button", { name: /^Remove / }),
    "the empty legacy Blacklist a real install always emitted must convert to an empty id list - not to a chip, and not by stranding the whole conversion on a half it had nothing to resolve",
  ).toHaveCount(0);

  const performersCard = card(page, "Performers");
  const performerOnlyInclude = field(performersCard, "Only include");
  await expect(
    performerOnlyInclude.getByRole("button", {
      name: `Remove ${names.performerKeep}`,
      exact: true,
    }),
    "the performer whitelist rule did not survive as a named chip",
  ).toBeVisible();
  await expect(
    performerOnlyInclude.getByRole("button", { name: `Remove ${names.caseFirst}`, exact: true }),
    "the surviving half of the case-variant pair must be the LOWEST id, whose name is the first-created spelling",
  ).toBeVisible();
  await expect(
    performerOnlyInclude.getByRole("button", { name: `Remove ${names.caseSecond}`, exact: true }),
    "both case variants survived as separate chips - the two stored names resolve to one id, so this rule now covers one performer where it covered two, and that narrowing is what the changelog discloses",
  ).toHaveCount(0);
  await expect(
    performerOnlyInclude.getByRole("button", { name: /^Remove / }),
    "three stored names must land as exactly two chips: the keep performer, plus one survivor of the case-variant pair",
  ).toHaveCount(2);
  await expect(
    field(performersCard, "Never include").getByRole("button", {
      name: `Remove ${names.performerBlock}`,
      exact: true,
    }),
    "the performer blacklist rule did not survive as a named chip - the two groups convert independently, so a whitelist that landed says nothing about a blacklist that did not",
  ).toBeVisible();

  await page.getByRole("button", { name: /^Excludes/ }).click();
  // The tag exclude names itself through its own `Field` rather than through a card heading, so that
  // block is the narrowest scope that holds its chips.
  const excludeTagCard = field(page, "Exclude by tag");
  await expect(
    excludeTagCard.getByRole("button", { name: `Remove ${names.tagExclude}`, exact: true }),
    'the exclusion that DID resolve is missing, so nothing below distinguishes "the vanished name was dropped" from "the whole field was emptied"',
  ).toBeVisible({ timeout: 15_000 });
  await expect(
    excludeTagCard.getByRole("button", { name: /^Remove / }),
    "the exclusion list holds more than the one rule that could resolve - a name matching nothing in the library must be dropped, because it could never have matched anything",
  ).toHaveCount(1);
  const excludeText = await excludeTagCard.innerText();
  expect(excludeText, `the dropped name "${vanishedTag}" is still on the panel`).not.toContain(
    vanishedTag,
  );
  expect(
    excludeText,
    "a chip is stuck on the host loading placeholder, which is what an id resolving to no entity looks like - the conversion wrote an id the library does not have",
  ).not.toContain("Loading tag...");

  // A `<label>` forwards a click anywhere inside it to the first labelable descendant, and the host
  // selector renders each chip's Remove button ahead of its input.
  await expect(
    excludeTagCard,
    "the exclusion field no longer resolves to exactly one block, so everything scoped to it below is being read off some other part of the page",
  ).toHaveCount(1);
  await excludeTagCard.getByText("Exclude by tag", { exact: true }).click();
  await expect(
    excludeTagCard.getByRole("button", { name: /^Remove / }),
    "clicking the field's heading text deleted a configured exclusion - the rule is gone with no message shown and nothing to put it back",
  ).toHaveCount(1);
  await expect(
    excludeTagCard,
    "the field's block carries no accessible name, so the heading a user reads names nothing",
  ).toHaveAccessibleName("Exclude by tag");
  await expect(
    excludeTagCard.getByRole("combobox", { name: "Exclude by tag", exact: true }),
    "the host selector's search input carries no name of its own - a group's name does not reach a control nested inside it, so the field label has to be passed to the input",
  ).toHaveCount(1);

  const tagDestinations = card(page, "Per-tag destinations");
  // The committed row, addressed by the remove button its own map key names. The `has:` locator is
  // rooted on `page` rather than on the card: Playwright re-anchors an inner locator at each candidate,
  // so one built from the card would require the card's heading to sit inside the row. `.last()` picks
  // the innermost matching div, i.e. the row itself rather than the editor wrapping it.
  const routedRow = tagDestinations
    .locator("div")
    .filter({ has: page.getByRole("button", { name: `Remove ${ids.tagRoute}`, exact: true }) })
    .last();
  await expect(
    routedRow,
    "the committed per-tag destination row is gone - its map key did not survive re-keying from the tag name to that tag's id",
  ).toBeVisible({ timeout: 15_000 });
  await expect(
    routedRow.getByText(names.tagRoute, { exact: true }),
    "the destination row shows its opaque id rather than the tag name the host resolves it to",
  ).toBeVisible();
  // A destination now names one of Cove's library paths plus a folder rendered under it, so the
  // single legacy string has to split into the library path that holds it and the remainder.
  await expect(
    routedRow.getByRole("combobox"),
    "the stored path did not become the library path holding it",
  ).toHaveValue("/data");
  await expect(
    routedRow.getByRole("textbox"),
    "the remainder of the stored path did not survive as the folder rendered under that root",
  ).toHaveValue("routed");

  await expect(
    settings.filenameTemplateInput,
    "the filename template changed across a conversion that has no business touching it - this is the field a typed converter would have reset to its default, and the whole reason the conversion works on raw JSON",
  ).toHaveValue(legacyBlob.FilenameTemplate);
  const advancedRouting = card(page, "Source-path destinations");
  await expect(
    advancedRouting.getByRole("textbox", { name: "Source path" }),
    "the stored source-path rule did not survive the conversion",
  ).toHaveValue("/data/legacy");
  // This rule's destination splits the same way the per-tag one above does.
  await expect(
    advancedRouting.getByRole("combobox", { name: "Under" }),
    "the source-path rule survived but its destination root did not",
  ).toHaveValue("/data");
  await expect(
    advancedRouting.getByRole("textbox", { name: "Folder template" }),
    "the source-path rule's destination root survived but the folder under it did not",
  ).toHaveValue("archive");

  // `htmlFor` carries the label-to-control pairing, so this holds whether or not the label wraps
  // its control.
  await advancedRouting.getByText("Source path", { exact: true }).click();
  await expect(
    advancedRouting.getByRole("textbox", { name: "Source path" }),
    "clicking a plain field's heading text no longer put the cursor in its input, so the label element was taken off every Field rather than off the entity selectors alone",
  ).toBeFocused();

  const subjectIds = [renameSubjects.excluded.id, renameSubjects.control.id];
  const preview = await api.post(`${ROUTE}/preview`, {
    EntityType: "video",
    EntityIds: subjectIds,
  });
  expect(preview.status, `preview answered ${preview.status}: ${preview.text}`).toBe(200);
  const statusOf = (subject) =>
    preview.json.items.find((item) => item.fileId === subject.fileId)?.status;
  expect(
    statusOf(renameSubjects.excluded),
    "the video carrying the converted exclusion's tag is not planned as excluded",
  ).toBe("skipExcluded");
  expect(
    statusOf(renameSubjects.control),
    "the untagged video would not be renamed either, so the exclusion above decides nothing",
  ).toBe("rename");

  const enqueue = await api.post(`${ROUTE}/renamer`, {
    EntityType: "video",
    EntityIds: subjectIds,
  });
  expect(enqueue.status, `the rename answered ${enqueue.status}: ${enqueue.text}`).toBe(202);
  const job = await pollRenamerJob(api, ROUTE, enqueue.json.jobId);
  expect(job.status.toLowerCase(), `the rename job ended ${job.status}: ${job.error}`).toBe(
    "completed",
  );
  await pollUntil(
    () => api.get(`/api/videos/${renameSubjects.control.id}`).then((r) => r.json),
    (v) => v.files[0].path !== renameSubjects.control.originalPath,
    { label: "the untagged video to be renamed" },
  );
  expect(
    (await api.get(`/api/videos/${renameSubjects.excluded.id}`)).json.files[0].path,
    "the excluded video was renamed - the converted exclusion no longer matches its tag",
  ).toBe(renameSubjects.excluded.originalPath);

  expect(errors, `the settings surface raised page errors: ${errors.join("; ")}`).toEqual([]);
});
